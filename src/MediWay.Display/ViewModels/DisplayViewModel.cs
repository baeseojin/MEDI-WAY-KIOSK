using System.ComponentModel;
using System.Threading.Channels;
using MediWay.Contracts;
using MediWay.Display.Services;

namespace MediWay.Display.ViewModels;

public sealed class DisplayRoomViewModel : INotifyPropertyChanged
{
    private DisplayRoomSnapshot snapshot;
    public DisplayRoomViewModel(DisplayRoomSnapshot snapshot) { this.snapshot = snapshot; }
    public Guid RoomId => snapshot.RoomId;
    public string RoomName => snapshot.RoomName;
    public string DepartmentName => snapshot.DepartmentName;
    public string? FloorCode => snapshot.FloorCode;
    public string? FloorName => snapshot.FloorName;
    public string Number => snapshot.Current?.QueueNumber.ToString("D3") ?? "—";
    public string Status => snapshot.Current?.Status switch { "Called" => "입실해 주세요", "InService" => "진료 중", _ => "호출 대기" };
    public int WaitingCount => snapshot.Waiting.Count;
    public IReadOnlyList<QueueItem> Waiting => snapshot.Waiting;
    public DisplayCurrent? Current => snapshot.Current;
    public bool Emphasize { get; private set; }
    public void Apply(DisplayRoomSnapshot next)
    {
        var newCall = next.Current is { Status: "Called" } called
            && (snapshot.Current?.VisitId != called.VisitId || snapshot.Current.Version != called.Version);
        snapshot = next;
        // Restart the visual cue only for a newly observed Called state, never for a repeated event/snapshot.
        Emphasize = false; PropertyChanged?.Invoke(this, new(nameof(Emphasize)));
        PropertyChanged?.Invoke(this, new(null));
        if (newCall) { Emphasize = true; PropertyChanged?.Invoke(this, new(nameof(Emphasize))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DisplayViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IDisplayApi api;
    private readonly IQueueConnection realtime;
    private readonly Action<Action> dispatch;
    private readonly TimeSpan pollInterval;
    private readonly CallAnnouncer? announcer;
    private readonly Channel<byte> requests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private Task? ticker;
    private Task? rotation;
    private readonly Func<DateTimeOffset> clock;
    private DateTimeOffset nextPage;
    private int pageIndex;
    private string pageSignature = "";
    private IReadOnlyList<DisplayRoomViewModel[]> pages = [];
    private volatile bool disposed;
    public DisplayViewModel(IDisplayApi api, IQueueConnection realtime, Action<Action> dispatch, TimeSpan? pollInterval = null, CallAnnouncer? announcer = null, Func<DateTimeOffset>? clock = null)
    {
        this.api = api; this.realtime = realtime; this.dispatch = dispatch;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        this.announcer = announcer;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (announcer is not null) announcer.Changed += OnSpeechChanged;
    }
    public IReadOnlyList<DisplayRoomViewModel> Rooms { get; private set; } = [];
    public IReadOnlyList<DisplayRoomViewModel> VisibleRooms => pages.Count == 0 ? [] : pages[pageIndex];
    public string PageText => VisibleRooms.Count == 0 ? "진료실을 확인하고 있습니다" :
        $"{VisibleRooms[0].FloorName ?? VisibleRooms[0].DepartmentName} · {VisibleRooms[0].DepartmentName}  ({pageIndex + 1}/{pages.Count}) · 10초마다 전환";
    public void TickPage()
    {
        if (disposed || pages.Count < 2 || clock() < nextPage) return;
        var steps = (long)((clock() - nextPage).TotalSeconds / 10) + 1;
        pageIndex = (int)((pageIndex + steps) % pages.Count); nextPage = nextPage.AddSeconds(steps * 10); Notify();
    }
    public bool IsFresh { get; private set; }
    public bool IsRealtime { get; private set; }
    public string ConnectionText => !IsFresh ? "갱신 중단 · 마지막 정보입니다" : IsRealtime ? "실시간 연결됨" : "주기 조회로 갱신 중 · 실시간 연결 재시도";
    public string UpdatedText { get; private set; } = "서버 현황을 확인하고 있습니다";
    public string Message { get; private set; } = "";
    public string SpeechStatus => announcer?.StatusText ?? "음성 안내 사용 안 함";
    private void OnSpeechChanged() => Post(Notify);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Start()
    {
        if (worker is not null || disposed) return;
        realtime.Changed += OnChanged; realtime.ConnectionChanged += RequestRefresh;
        worker = Task.Run(RunAsync); ticker = Task.Run(TickAsync); rotation = Task.Run(RotateAsync); RequestRefresh();
    }
    private async Task RotateAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) Post(TickPage); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private void OnChanged(QueueChanged change) => RequestRefresh();
    public void RequestRefresh() { if (!disposed) requests.Writer.TryWrite(0); }
    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(pollInterval);
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) RequestRefresh(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task RunAsync()
    {
        try
        {
            while (await requests.Reader.WaitToReadAsync(lifetime.Token))
            {
                while (requests.Reader.TryRead(out _)) { }
                try
                {
                    // Learn room IDs, subscribe, then fetch again to close the subscribe/snapshot race.
                    var initial = await api.SnapshotAsync(lifetime.Token);
                    try
                    {
                        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                        await realtime.EnsureConnectedAsync(initial.Rooms.Select(r => r.RoomId).ToArray(), connectTimeout.Token);
                    }
                    catch (Exception) when (!lifetime.IsCancellationRequested) { /* HTTP snapshot still provides fallback */ }
                    var latest = await api.SnapshotAsync(lifetime.Token);
                    var connected = realtime.IsConnected;
                    Post(() => Apply(latest, connected));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    Post(() => { announcer?.Suspend(); IsFresh = false; IsRealtime = false; Message = "연결을 확인하고 있습니다. 이전 안내는 변경되었을 수 있습니다."; Notify(); });
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private void Post(Action update) => dispatch(() => { if (!disposed) update(); });
    private void Apply(DisplaySnapshot snapshot, bool connected)
    {
        announcer?.Observe(snapshot);
        var previous = Rooms.ToDictionary(r => r.RoomId);
        Rooms = snapshot.Rooms.Select(room =>
        {
            if (previous.TryGetValue(room.RoomId, out var existing)) { existing.Apply(room); return existing; }
            return new DisplayRoomViewModel(room);
        }).ToArray();
        var nextPages = Rooms.GroupBy(r => r.FloorCode ?? r.DepartmentName).SelectMany(group => group.Chunk(2)).ToArray();
        var signature = string.Join("|", nextPages.Select(page => string.Join(",", page.Select(r => r.RoomId))));
        if (signature != pageSignature) { pageIndex = 0; nextPage = clock().AddSeconds(10); pageSignature = signature; }
        pages = nextPages;
        IsFresh = true; IsRealtime = connected; Message = Rooms.Count == 0 ? "등록된 진료실이 없습니다." : "";
        UpdatedText = "마지막 확인 " + TimeZoneInfo.ConvertTimeFromUtc(snapshot.AsOf.ToUniversalTime(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")).ToString("yyyy-MM-dd HH:mm:ss");
        Notify();
    }
    private void Notify() => PropertyChanged?.Invoke(this, new(null));
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (announcer is not null) announcer.Changed -= OnSpeechChanged;
        realtime.Changed -= OnChanged; realtime.ConnectionChanged -= RequestRefresh;
        lifetime.Cancel(); requests.Writer.TryComplete();
        if (worker is not null) await worker;
        if (ticker is not null) await ticker;
        if (rotation is not null) await rotation;
        await realtime.DisposeAsync();
        if (announcer is not null) await announcer.DisposeAsync();
        lifetime.Dispose();
    }
}
