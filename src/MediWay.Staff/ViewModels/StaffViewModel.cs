using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using MediWay.Contracts;
using MediWay.Staff.Services;

namespace MediWay.Staff.ViewModels;

public sealed class StaffCommand(Func<Task> execute, Func<bool> enabled) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && enabled();
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        running = true; Refresh();
        try { await execute(); } finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record StaffAction(Guid VisitId, string Action, int Version);

public sealed class StaffViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IStaffApiClient api;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private RoomResponse? selectedRoom;
    private QueueItem? selectedWaiting;
    private StaffAction? pending;
    private StaffAction? cancellation;
    private (Guid RoomId, QueueOrderRequest Request)? pendingOrder;
    private DateOnly queueDate;
    private readonly List<StaffCommand> commands = [];
    private StaffCommand Command(Func<Task> action, Func<bool> allowed)
    {
        var command = new StaffCommand(action, allowed); commands.Add(command); return command;
    }
    public StaffViewModel(IStaffApiClient api)
    {
        this.api = api;
        RefreshCommand = Command(RefreshAsync, () => CanNavigate);
        CallCommand = Command(() => ChangeAsync(new(SelectedWaiting!.VisitId, "call", SelectedWaiting.Version)), () => CanOperate && Current is null && SelectedWaiting is not null);
        StartCommand = Command(() => ChangeAsync(new(Current!.Id, "start", Current.Version)), () => CanOperate && Current?.Status == "Called");
        CompleteCommand = Command(() => ChangeAsync(new(Current!.Id, "complete", Current.Version)), () => CanOperate && Current?.Status == "InService");
        CancelWaitingCommand = Command(() => ArmCancel(new(SelectedWaiting!.VisitId, "cancel", SelectedWaiting.Version)), () => CanOperate && SelectedWaiting is not null);
        CancelCurrentCommand = Command(() => ArmCancel(new(Current!.Id, "cancel", Current.Version)), () => CanOperate && Current?.Status == "Called");
        ConfirmCancelCommand = Command(async () => { var action = cancellation!; cancellation = null; await ChangeAsync(action); }, () => !disposed && !IsBusy && cancellation is not null);
        DismissCancelCommand = Command(() => { cancellation = null; Notify(); return Task.CompletedTask; }, () => !disposed && !IsBusy && cancellation is not null);
        RetryCommand = Command(() => pendingOrder is { } order ? ChangeOrderAsync(order.RoomId, order.Request) : ChangeAsync(pending!), () => !disposed && !IsBusy && (pending is not null || pendingOrder is not null));
        MoveUpCommand = Command(() => MoveSelectedAsync(-1), () => CanOperate && SelectedIndex > 0);
        MoveDownCommand = Command(() => MoveSelectedAsync(1), () => CanOperate && SelectedIndex >= 0 && SelectedIndex < Waiting.Count - 1);
        PriorityCommand = Command(() => MoveSelectedAsync(-SelectedIndex), () => CanOperate && SelectedIndex > 0);
    }
    public IReadOnlyList<RoomResponse> Rooms { get; private set; } = [];
    public IReadOnlyList<QueueItem> Waiting { get; private set; } = [];
    public VisitResponse? Current { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsConnected { get; private set; }
    public bool CanNavigate => !disposed && !IsBusy && pending is null && pendingOrder is null && cancellation is null;
    public bool CanOperate => CanNavigate && IsConnected;
    public bool NeedsRetry => pending is not null || pendingOrder is not null;
    public bool NeedsCancelConfirmation => cancellation is not null;
    public string Message { get; private set; } = "";
    public string ConnectionText => IsBusy ? "서버 확인 중" : IsConnected ? "연결됨" : "연결 확인 필요 · 표시된 정보는 이전 조회 결과입니다";
    public string LastUpdated { get; private set; } = "아직 조회하지 않았습니다";
    public string CurrentNumber => Current?.QueueNumber.ToString("D3") ?? "—";
    public string CurrentStatus => Current?.Status switch { "Called" => "호출됨", "InService" => "진료 중", _ => "진행 중인 접수가 없습니다" };
    public string CurrentDate => Current is null ? "" : $"진료일 {Current.ServiceDate:yyyy-MM-dd}";
    public RoomResponse? SelectedRoom
    {
        get => selectedRoom;
        set
        {
            if (!CanNavigate || selectedRoom == value) return;
            selectedRoom = value; Waiting = []; Current = null; selectedWaiting = null; IsConnected = false;
            Message = "진료실을 변경했습니다. 새로고침해 주세요."; Notify();
        }
    }
    public QueueItem? SelectedWaiting
    {
        get => selectedWaiting;
        set { if (!CanNavigate || selectedWaiting == value) return; selectedWaiting = value; Notify(); }
    }
    public StaffCommand RefreshCommand { get; }
    public StaffCommand CallCommand { get; }
    public StaffCommand StartCommand { get; }
    public StaffCommand CompleteCommand { get; }
    public StaffCommand CancelWaitingCommand { get; }
    public StaffCommand CancelCurrentCommand { get; }
    public StaffCommand ConfirmCancelCommand { get; }
    public StaffCommand DismissCancelCommand { get; }
    public StaffCommand RetryCommand { get; }
    public StaffCommand MoveUpCommand { get; }
    public StaffCommand MoveDownCommand { get; }
    public StaffCommand PriorityCommand { get; }
    private int SelectedIndex => selectedWaiting is null ? -1 : Waiting.ToList().FindIndex(x => x.VisitId == selectedWaiting.VisitId);

    private Task MoveSelectedAsync(int offset)
    {
        var index = SelectedIndex;
        if (!CanOperate || index < 0) return Task.CompletedTask;
        var ids = Waiting.Select(x => x.VisitId).ToList(); var id = ids[index]; ids.RemoveAt(index);
        ids.Insert(Math.Clamp(index + offset, 0, ids.Count), id);
        return SubmitOrderAsync(ids);
    }
    public Task MoveBeforeAsync(Guid source, Guid target)
    {
        if (!CanOperate || source == target || !Waiting.Any(x => x.VisitId == source) || !Waiting.Any(x => x.VisitId == target)) return Task.CompletedTask;
        var ids = Waiting.Select(x => x.VisitId).ToList(); ids.Remove(source); ids.Insert(ids.IndexOf(target), source);
        return SubmitOrderAsync(ids);
    }
    private Task SubmitOrderAsync(IReadOnlyList<Guid> ids)
    {
        if (selectedRoom is null || ids.SequenceEqual(Waiting.Select(x => x.VisitId))) return Task.CompletedTask;
        return ChangeOrderAsync(selectedRoom.Id, new(Guid.NewGuid(), queueDate,
            Waiting.Select(x => new QueueOrderEntry(x.VisitId, x.Version)).ToArray(), ids));
    }
    private async Task ChangeOrderAsync(Guid roomId, QueueOrderRequest request)
    {
        pendingOrder = (roomId, request); IsBusy = true; Message = ""; Notify();
        try
        {
            try { await api.ReorderAsync(roomId, request, lifetime.Token); }
            catch (Exception ex) when (Expected(ex))
            {
                if (disposed) return;
                IsConnected = false;
                if (ex is StaffApiFailure { IsUncertain: false })
                { pendingOrder = null; await RefreshCore(); Message = ex.Message; }
                else Message = "순서 변경 결과가 불명확합니다. 같은 작업 결과 재확인 버튼을 눌러 주세요.";
                return;
            }
            if (disposed) return;
            pendingOrder = null;
            var refreshed = await RefreshCore();
            Message = refreshed ? "대기 순서를 저장했습니다. 대기번호는 그대로 유지됩니다." : "순서는 저장했지만 목록을 읽지 못했습니다. 새로고침해 주세요.";
        }
        finally { if (!disposed) { IsBusy = false; Notify(); } }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true; Message = ""; Notify();
        try
        {
            if (Rooms.Count == 0)
            {
                var rooms = await api.RoomsAsync(lifetime.Token);
                if (disposed) return;
                Rooms = rooms; selectedRoom = rooms.FirstOrDefault();
            }
            await RefreshCore();
        }
        catch (Exception ex) when (Expected(ex)) { if (!disposed) { IsConnected = false; Message = Explain(ex); } }
        finally { if (!disposed) { IsBusy = false; Notify(); } }
    }
    private async Task<bool> RefreshCore()
    {
        if (selectedRoom is null) { IsConnected = false; Message = "등록된 진료실이 없습니다."; return false; }
        try
        {
            var room = selectedRoom.Id;
            var queue = await api.QueueAsync(room, lifetime.Token);
            var current = await api.CurrentAsync(room, lifetime.Token);
            if (disposed) return false;
            var old = selectedWaiting?.VisitId;
            Waiting = queue.Items; queueDate = queue.ServiceDate; Current = current.Visit;
            selectedWaiting = Waiting.FirstOrDefault(x => x.VisitId == old) ?? Waiting.FirstOrDefault();
            IsConnected = true; LastUpdated = "마지막 조회 " + TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")).ToString("HH:mm:ss");
            return true;
        }
        catch (Exception ex) when (Expected(ex))
        {
            if (!disposed) { IsConnected = false; Message = Explain(ex); }
            return false;
        }
    }
    private Task ArmCancel(StaffAction action)
    {
        cancellation = action; Message = "선택한 접수를 취소하려면 취소 확정을 눌러 주세요."; Notify(); return Task.CompletedTask;
    }
    private async Task ChangeAsync(StaffAction action)
    {
        pending = action; IsBusy = true; Message = ""; Notify();
        try
        {
            try { await api.ChangeAsync(action.VisitId, action.Action, action.Version, lifetime.Token); }
            catch (Exception ex) when (Expected(ex))
            {
                if (disposed) return;
                IsConnected = false;
                if (ex is StaffApiFailure { IsUncertain: false })
                {
                    pending = null; await RefreshCore(); Message = ex.Message;
                }
                else Message = "처리 결과가 불명확합니다. 같은 작업 결과 재확인 버튼을 눌러 주세요.";
                return;
            }
            if (disposed) return;
            pending = null;
            var refreshed = await RefreshCore();
            Message = refreshed ? "처리했습니다. 최신 목록을 확인해 주세요." : "처리는 완료됐지만 목록을 읽지 못했습니다. 새로고침해 주세요.";
        }
        finally { if (!disposed) { IsBusy = false; Notify(); } }
    }
    private static bool Expected(Exception ex) => ex is StaffApiFailure or HttpRequestException or OperationCanceledException or JsonException;
    private static string Explain(Exception ex) => ex is StaffApiFailure ? ex.Message : "서버에 연결하지 못했습니다. 새로고침해 주세요.";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify() { PropertyChanged?.Invoke(this, new(null)); foreach (var command in commands) command.Refresh(); }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
