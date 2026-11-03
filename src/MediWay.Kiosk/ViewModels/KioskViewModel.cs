using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using MediWay.Contracts;
using MediWay.Kiosk.Services;

namespace MediWay.Kiosk.ViewModels;

public enum KioskScreen { Input, Confirm, Result, Guide, Qr }

public sealed class UiCommand(Action<object?> execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncUiCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    private bool running;
    public bool CanExecute(object? parameter) => !running && canExecute();
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        running = true; Refresh();
        try { await execute(); }
        finally { running = false; Refresh(); }
    }
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class KioskViewModel : INotifyPropertyChanged, IDisposable
{
    public ServerConnectionMonitor? Connection { get; init; }
    private readonly IKioskApiClient api;
    private readonly Func<DateTimeOffset> now;
    private readonly IWaitingTicketWriter? ticketWriter;
    private readonly IOfflineRegistrationStore? offlineStore;
    private bool offlineInitialized, persisted;
    private DateTimeOffset retryAt;
    private readonly CancellationTokenSource lifetime = new();
    private DateTimeOffset deadline;
    private bool disposed;
    private string patientNumber = "";
    private RoomResponse? selectedRoom;
    private CreateVisitRequest? pending;
    private PatientResponse? patient;
    private string barcodeInput = "";
    private DateTimeOffset nextScanAt;

    public KioskViewModel(IKioskApiClient api, Func<DateTimeOffset>? now = null, IWaitingTicketWriter? ticketWriter = null, IOfflineRegistrationStore? offlineStore = null)
    {
        this.api = api;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.ticketWriter = ticketWriter;
        this.offlineStore = offlineStore; offlineInitialized = offlineStore is null;
        LookupCommand = new(LookupAsync, () => CanEdit && Screen == KioskScreen.Input && NormalizedNumber.Length > 1);
        RegisterCommand = new(RegisterAsync, () => !disposed && offlineInitialized && !IsBusy && IsConfirm && (pending is not null || patient is not null && SelectedRoom is not null));
        HomeCommand = new(_ => Reset(), () => !disposed && offlineInitialized && !IsBusy && pending is null);
        KeyCommand = new(Key, () => CanEdit && Screen == KioskScreen.Input);
        GuideCommand = new(ShowGuideAsync, () => !disposed && !IsBusy && (IsResult || IsGuide || IsQr) && Result?.Visit.Status is "Waiting" or "Called" or "InService");
        ReceiptCommand = new(_ => ShowReceipt(), () => !disposed && !IsBusy && (IsGuide || IsQr));
        MobileCommand = new(ShowMobileAsync, () => !disposed && !IsBusy && (IsResult || IsGuide || IsQr) && Result?.Visit.Status is "Waiting" or "Called" or "InService");
        SaveTicketCommand = new(SaveTicketAsync, () => !disposed && !IsBusy && IsResult && Result is not null && ticketWriter is not null);
        ScanCommand = new(ScanAsync, () => CanEdit && IsInput);
        DemoScanCommand = new(async () => { BarcodeInput = "MEDIWAY:P0001"; await ScanAsync(); }, () => CanEdit && IsInput);
        InitializeCommand = new(InitializeAsync, () => !disposed && !IsBusy && !offlineInitialized);
        ReconcileCommand = new(RegisterAsync, () => !disposed && !IsBusy && offlineInitialized && persisted && pending is not null && this.now() >= retryAt);
        Touch();
    }

    public KioskScreen Screen { get; private set; }
    public bool IsInput => Screen == KioskScreen.Input;
    public bool IsConfirm => Screen == KioskScreen.Confirm;
    public bool IsResult => Screen == KioskScreen.Result;
    public bool IsGuide => Screen == KioskScreen.Guide;
    public bool IsQr => Screen == KioskScreen.Qr;
    public MobileGuideTicket? MobileGuide { get; private set; }
    public string QrPngBase64 => MobileGuide?.QrPngBase64 ?? "";
    public string QrExpires => MobileGuide is null ? "" : "안내 유효시간: " + TimeZoneInfo.ConvertTime(MobileGuide.ExpiresAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")).ToString("MM-dd HH:mm");
    public string QrNotice => MobileGuide?.IsLocalOnly == true ? "현재 PC에서만 열리는 시연용 QR입니다. 휴대폰 연결은 서버 주소 설정 후 사용할 수 있습니다." : "휴대폰 카메라로 QR을 스캔하면 길 안내를 볼 수 있습니다.";
    public NavigationRoute? Route { get; private set; }
    public string GuideSummary => $"{TicketNumber}번 · {ResultRoom}";
    public string GuideDistance => Route is null ? "" : $"이동 거리 {Route.DistanceMeters:0.#}m · 표시된 경로를 따라 이동해 주세요.";
    public string GuideNotice => Route?.Map.Links.Any(l => l.IsClosed) == true
        ? "일부 통로가 폐쇄되어 있습니다. 표시된 경로를 따라 이동해 주세요." : "현재 위치에서 진료실 입구까지 안내합니다.";
    public bool IsBusy { get; private set; }
    public bool CanEdit => !disposed && offlineInitialized && !IsBusy && pending is null;
    public bool CanSelectRoom => CanEdit;
    public string Message { get; private set; } = "";
    public string TicketFilePath { get; private set; } = "";
    public string TicketSaveMessage { get; private set; } = "";
    public string LocalStatus => offlineStore is null ? "" : !offlineInitialized ? "로컬 복구 상태 확인이 필요합니다."
        : pending is null ? "네트워크 단절 복구 준비됨" : persisted ? "로컬 접수 요청 1건 보관 · 서버 확정 전 · 5초 간격 재확인" : "로컬 저장 실패 · 서버 전송 중지";
    public bool NeedsLocalInitialization => !offlineInitialized;
    public string BarcodeInput
    {
        get => barcodeInput;
        set { if (!CanEdit || !IsInput) return; barcodeInput = value ?? ""; Message = ""; Touch(); Notify(); }
    }
    public string MaskedName => patient is null ? "" : Mask(patient.DisplayName);
    public IReadOnlyList<RoomResponse> Rooms { get; private set; } = [];
    public CreateVisitResponse? Result { get; private set; }
    public string TicketNumber => Result?.Visit.QueueNumber.ToString("D3") ?? "";
    public string ResultRoom => Rooms.FirstOrDefault(r => r.Id == Result?.Visit.RoomId)?.Name ?? "진료실 확인 필요";
    public string ResultTitle => Result?.IsExisting == true ? "기존 접수를 확인했어요" : "접수가 완료되었어요";
    public string ResultStatus => Result?.Visit.Status switch
    {
        "Waiting" => "대기 중",
        "Called" => "호출됨",
        "InService" => "진료 중",
        "Completed" => "진료 완료",
        "Cancelled" => "접수 취소",
        _ => ""
    };
    public string ResultTime => Result is null ? "" :
        TimeZoneInfo.ConvertTimeFromUtc(Result.Visit.CreatedAt.ToUniversalTime(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")).ToString("yyyy-MM-dd HH:mm");
    public string RegisterLabel => IsBusy ? "접수 결과 확인 중…" : pending is null ? "접수하고 번호 받기" : "같은 요청으로 결과 재확인";
    public int RemainingSeconds { get; private set; } = 60;
    public string TimerText => IsBusy ? "처리 중입니다. 잠시만 기다려 주세요."
        : pending is not null ? "결과 확인이 필요합니다. 아래 재확인 버튼을 눌러 주세요."
        : $"{RemainingSeconds}초 후 처음 화면으로 돌아갑니다.";

    public string PatientNumber
    {
        get => patientNumber;
        set
        {
            if (!CanEdit || !IsInput) return;
            patientNumber = (value ?? "").Trim().ToUpperInvariant();
            Message = ""; Touch(); Notify();
        }
    }
    private string NormalizedNumber => patientNumber.StartsWith('P') ? patientNumber : "P" + patientNumber;
    public RoomResponse? SelectedRoom
    {
        get => selectedRoom;
        set { if (!CanEdit || selectedRoom == value) return; selectedRoom = value; Touch(); Notify(); }
    }
    public AsyncUiCommand LookupCommand { get; }
    public AsyncUiCommand RegisterCommand { get; }
    public UiCommand HomeCommand { get; }
    public UiCommand KeyCommand { get; }
    public AsyncUiCommand GuideCommand { get; }
    public UiCommand ReceiptCommand { get; }
    public AsyncUiCommand MobileCommand { get; }
    public AsyncUiCommand SaveTicketCommand { get; }
    public AsyncUiCommand ScanCommand { get; }
    public AsyncUiCommand DemoScanCommand { get; }
    public AsyncUiCommand InitializeCommand { get; }
    public AsyncUiCommand ReconcileCommand { get; }

    private async Task InitializeAsync()
    {
        IsBusy = true; Notify();
        try
        {
            var saved = offlineStore is null ? null : await offlineStore.LoadAsync(lifetime.Token);
            if (disposed) return;
            if (saved is not null)
            {
                pending = saved.Request; Rooms = saved.Rooms; selectedRoom = Rooms.Single(r => r.Id == pending.RoomId);
                persisted = true; Screen = KioskScreen.Confirm; retryAt = now();
                Message = "이전 접수 요청을 복구했습니다. 새 접수 없이 같은 요청으로 서버 결과를 확인합니다.";
            }
            offlineInitialized = true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            if (!disposed) Message = "로컬 복구 저장소를 확인하지 못했습니다. 다른 키오스크 실행 여부와 저장 폴더를 확인한 뒤 복구 상태 확인을 눌러 주세요.";
        }
        finally { if (!disposed) { IsBusy = false; Touch(); Notify(); } }
    }
    private static bool StorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or OperationCanceledException;
    private async Task<bool> ClearLocalAsync()
    {
        try
        {
            if (offlineStore is not null && pending is not null) await offlineStore.ClearAsync(pending.RequestId, lifetime.Token);
            if (disposed) return false;
            persisted = false; return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            if (!disposed) Message = "서버 응답은 확인했지만 로컬 요청 정리를 완료하지 못했습니다. 같은 요청으로 다시 확인해 주세요.";
            return false;
        }
    }

    private async Task ScanAsync()
    {
        if (now() < nextScanAt) { barcodeInput = ""; Message = "연속 입력을 잠시 기다린 뒤 다시 스캔해 주세요."; Notify(); return; }
        var raw = barcodeInput; barcodeInput = "";
        if (!PatientBarcode.TryParse(raw, out var number))
        {
            Message = "환자 바코드 형식을 확인해 주세요. 예: P0001 또는 MEDIWAY:P0001"; Notify(); return;
        }
        nextScanAt = now().AddSeconds(2);
        PatientNumber = number;
        await LookupAsync();
    }

    private async Task WriteTicketAsync()
    {
        if (ticketWriter is null || Result is null) return;
        try
        {
            var path = await ticketWriter.SaveAsync(Result.Visit, ResultRoom, lifetime.Token);
            if (disposed) return;
            TicketFilePath = path;
            TicketSaveMessage = "대기표 파일을 저장했습니다.\n저장 위치: " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or OperationCanceledException)
        {
            if (disposed) return;
            TicketSaveMessage = "접수는 완료되었습니다. 대기표 파일을 저장하지 못했습니다. 저장 폴더를 확인한 뒤 ‘대기표 파일 저장’을 다시 눌러 주세요.";
        }
    }

    private async Task SaveTicketAsync()
    {
        IsBusy = true; Notify();
        try { await WriteTicketAsync(); }
        finally
        {
            if (!disposed) { IsBusy = false; deadline = now().AddSeconds(60); RemainingSeconds = 60; Notify(); }
        }
    }

    private async Task ShowMobileAsync()
    {
        if (Result is null) return;
        IsBusy = true; Message = ""; Notify();
        try
        {
            var ticket = MobileGuide is { } saved && saved.ExpiresAt > now() ? saved : await api.CreateMobileGuideAsync(Result.Visit.Id, lifetime.Token);
            if (disposed) return;
            MobileGuide = ticket; Route = null; Screen = KioskScreen.Qr; deadline = now().AddSeconds(60); RemainingSeconds = 60;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (disposed) return;
            MobileGuide = null; Route = null; Screen = KioskScreen.Result; deadline = now().AddSeconds(20); RemainingSeconds = 20;
            Message = "모바일 안내를 표시하지 못했습니다. " + (ex is ApiFailure ? ex.Message : "잠시 후 다시 시도하거나 화면 길 안내를 이용해 주세요.");
        }
        finally { if (!disposed) { IsBusy = false; Notify(); } }
    }

    private async Task ShowGuideAsync()
    {
        if (Result is null) return;
        IsBusy = true; Message = ""; Notify();
        try
        {
            var route = await api.RouteAsync(Result.Visit.RoomId, lifetime.Token);
            if (disposed) return;
            Route = route; Screen = KioskScreen.Guide; deadline = now().AddSeconds(60); RemainingSeconds = 60;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (disposed) return;
            Route = null; Screen = KioskScreen.Result;
            deadline = now().AddSeconds(20); RemainingSeconds = 20;
            Message = "접수는 유지됩니다. " + (ex is ApiFailure ? ex.Message : "길 안내를 읽지 못했습니다. 다시 시도하거나 안내 데스크에 문의해 주세요.");
        }
        finally { if (!disposed) { IsBusy = false; Notify(); } }
    }
    private void ShowReceipt()
    {
        Route = null; Screen = KioskScreen.Result; Message = "";
        deadline = now().AddSeconds(20); RemainingSeconds = 20; Notify();
    }

    private async Task LookupAsync()
    {
        IsBusy = true; Message = ""; Notify();
        try
        {
            var found = await api.FindPatientAsync(NormalizedNumber, lifetime.Token);
            var rooms = await api.GetRoomsAsync(lifetime.Token);
            if (disposed) return;
            if (rooms.Count == 0) { Message = "접수 가능한 진료실이 없습니다."; return; }
            patient = found; Rooms = rooms; selectedRoom = rooms[0]; Screen = KioskScreen.Confirm;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!disposed) Message = ex is ApiFailure ? ex.Message : "서버에 연결하지 못했습니다. 환자번호를 확인하고 다시 조회해 주세요.";
        }
        finally { if (!disposed) { IsBusy = false; Touch(); Notify(); } }
    }

    private async Task RegisterAsync()
    {
        if (pending is null && (patient is null || selectedRoom is null)) return;
        pending ??= new(patient!.Id, selectedRoom!.Id, Guid.NewGuid(), offlineStore is null ? null :
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")).DateTime));
        IsBusy = true; Message = ""; Notify();
        try
        {
            if (offlineStore is not null && !persisted)
            {
                await offlineStore.SaveAsync(new(pending, Rooms, now()), lifetime.Token);
                if (disposed) return;
                persisted = true;
            }
            var response = await api.RegisterAsync(pending, lifetime.Token);
            if (disposed) return;
            if (!await ClearLocalAsync()) return;
            Result = response; pending = null; Screen = KioskScreen.Result;
            TicketFilePath = ""; TicketSaveMessage = "";
            await WriteTicketAsync();
            if (disposed) return;
            RemainingSeconds = ticketWriter is null ? 20 : 60;
            deadline = now().AddSeconds(RemainingSeconds);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (disposed) return;
            if (ex is ApiFailure { MayHaveCommitted: false })
            {
                if (await ClearLocalAsync())
                {
                    pending = null; Message = ex.Message;
                    if (patient is null || ex is ApiFailure { Code: "SERVICE_DATE_CHANGED" })
                    { Screen = KioskScreen.Input; patient = null; patientNumber = ""; selectedRoom = null; Rooms = []; }
                }
            }
            else Message = persisted ? "접수 결과가 아직 확정되지 않았습니다. 요청을 이 PC에 보관했고 5초 간격으로 같은 요청을 재확인합니다. 대기번호는 서버 확인 후 안내합니다."
                : "접수 결과를 확인하지 못했습니다. 중복 발권을 막기 위해 같은 요청으로 재확인해 주세요.";
        }
        catch (Exception ex) when (StorageError(ex))
        {
            if (!disposed) Message = "접수 요청을 로컬에 저장하지 못해 서버로 보내지 않았습니다. 저장 폴더를 확인하고 같은 요청으로 재시도해 주세요.";
        }
        finally { if (!disposed) { IsBusy = false; retryAt = now().AddSeconds(5); if (!IsResult && pending is null) Touch(); Notify(); } }
    }

    private static bool IsExpected(Exception ex) => ex is ApiFailure or HttpRequestException or OperationCanceledException or JsonException;
    private void Key(object? parameter)
    {
        var key = parameter?.ToString();
        if (key == "clear") PatientNumber = "";
        else if (key == "back") PatientNumber = patientNumber.Length > 0 ? patientNumber[..^1] : "";
        else if (key is { Length: 1 } && char.IsAsciiDigit(key[0]) && patientNumber.Length < 12) PatientNumber += key;
    }
    public void Touch()
    {
        if (disposed || IsResult || pending is not null) return;
        deadline = now().AddSeconds(60); RemainingSeconds = 60;
    }
    public void Tick()
    {
        if (disposed || IsBusy || pending is not null) return;
        RemainingSeconds = Math.Max(0, (int)Math.Ceiling((deadline - now()).TotalSeconds));
        if (RemainingSeconds == 0) Reset(); else Notify();
    }
    private void Reset()
    {
        if (IsBusy || pending is not null || disposed) return;
        patientNumber = ""; barcodeInput = ""; patient = null; selectedRoom = null; Rooms = []; Result = null;
        Route = null; MobileGuide = null; Message = ""; TicketFilePath = ""; TicketSaveMessage = ""; Screen = KioskScreen.Input; Touch(); Notify();
    }
    private static string Mask(string name) => name.Length <= 1 ? "*" : name.Length == 2 ? name[..1] + "*" : name[..1] + new string('*', name.Length - 2) + name[^1];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        LookupCommand.Refresh(); RegisterCommand.Refresh(); HomeCommand.Refresh(); KeyCommand.Refresh();
        GuideCommand.Refresh(); ReceiptCommand.Refresh();
        MobileCommand.Refresh();
        SaveTicketCommand.Refresh();
        ScanCommand.Refresh(); DemoScanCommand.Refresh();
        InitializeCommand.Refresh(); ReconcileCommand.Refresh();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); offlineStore?.Dispose(); lifetime.Dispose();
    }
}
