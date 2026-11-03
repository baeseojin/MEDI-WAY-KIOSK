using MediWay.Contracts;
using MediWay.Display.Services;
using MediWay.Display.ViewModels;
using System.IO;

if (args.FirstOrDefault() == "--floors-integration")
{
    await FloorsIntegration.RunAsync(args[1]);
    return;
}

if (args.FirstOrDefault() == "--order-integration")
{
    await QueueOrderIntegration.RunAsync(args[1]);
    return;
}

if (args.FirstOrDefault() == "--tts-wave")
{
    var path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using (var voice = new WindowsAnnouncementVoice(path))
    {
        Console.WriteLine(voice.Description);
        if (!voice.IsAvailable) { Console.WriteLine("SKIP: Korean speech engine not available"); return; }
        await voice.SpeakAsync("101번 환자분, 내과 1진료실로 들어오세요.", default);
    }
    var bytes = await File.ReadAllBytesAsync(path);
    if (bytes.Length <= 44 || System.Text.Encoding.ASCII.GetString(bytes,0,4) != "RIFF") throw new Exception("No WAV audio generated");
    Console.WriteLine("PASS: Real Korean TTS engine generated WAV audio");
    return;
}

if (args.FirstOrDefault() == "--integration")
{
    await Integration.Run(args[1], args[2]);
    return;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
static async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) await Task.Delay(10, timeout.Token);
}

var roomId = Guid.NewGuid();
var api = new FakeApi(roomId);
var connection = new FakeConnection();
await using var vm = new DisplayViewModel(api, connection, action => action(), TimeSpan.FromMilliseconds(100));
vm.Start();
await Until(() => vm.IsFresh && vm.IsRealtime);
Check(api.Calls >= 2 && connection.Subscriptions.Contains(roomId), "Subscribe then fetch latest snapshot on startup");
Check(vm.Rooms.Count == 1 && vm.Rooms[0].Number == "—", "Empty current panel");
var visit = new DisplayCurrent(Guid.NewGuid(), 101, "Called", 2, DateOnly.FromDateTime(DateTime.Today));
api.Current = visit;
connection.Emit();
await Until(() => vm.Rooms[0].Number == "101");
Check(vm.Rooms[0].Status == "입실해 주세요" && vm.Rooms[0].Emphasize, "New call refreshes and emphasizes current number");
connection.Emit();
await Until(() => !vm.Rooms[0].Emphasize);
Check(vm.Rooms[0].Current?.Version == 2, "Repeated notification does not replay call emphasis");
api.Current = visit with { Status = "InService", Version = 3 };
// No event: periodic reconciliation must still discover it.
await Until(() => vm.Rooms[0].Status == "진료 중");
Check(true, "Periodic reconciliation repairs a missed event");
connection.FailConnect = true; connection.Disconnect();
await Until(() => vm.IsFresh && !vm.IsRealtime);
Check(vm.ConnectionText.Contains("주기 조회"), "HTTP fallback stays available when realtime fails");
api.Fail = true; vm.RequestRefresh();
await Until(() => !vm.IsFresh);
Check(vm.Rooms[0].Number == "101" && vm.ConnectionText.Contains("마지막"), "Snapshot failure keeps previous data with stale indicator");
api.Fail = false; connection.FailConnect = false;
api.Current = null;
await Until(() => vm.IsFresh && vm.IsRealtime && vm.Rooms[0].Number == "—");
Check(true, "Reconnect resubscribes and restores latest state");

// Capture a stale first fetch, then issue a change while it is in flight.
api.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
vm.RequestRefresh();
await Until(() => api.WaitingOnGate);
api.Current = visit with { QueueNumber = 202 };
connection.Emit(); connection.Emit();
api.Gate.SetResult(api.Snapshot(null));
api.Gate = null;
await Until(() => vm.Rooms[0].Number == "202");
Check(api.MaxConcurrency == 1, "In-flight event changes are coalesced and snapshot requests stay serialized");
await vm.DisposeAsync();
var calls = api.Calls;
connection.Emit(); vm.RequestRefresh();
await Task.Delay(150);
Check(api.Calls == calls && connection.Disposed && connection.HandlerCount == 0, "Dispose stops polling, unsubscribes and closes realtime connection");
await SpeechChecks.RunAsync();
await RotationChecks.RunAsync();
Console.WriteLine("ALL DISPLAY TESTS PASSED");

sealed class FakeApi(Guid roomId) : IDisplayApi
{
    public DisplayCurrent? Current;
    public bool Fail;
    public int Calls;
    public int MaxConcurrency;
    private int active;
    public bool WaitingOnGate;
    public TaskCompletionSource<DisplaySnapshot>? Gate;
    public DisplaySnapshot Snapshot(DisplayCurrent? current) => new(DateTime.UtcNow, DateOnly.FromDateTime(DateTime.Today),
        [new(roomId, "내과 1진료실", "내과", current, [])]);
    public async Task<DisplaySnapshot> SnapshotAsync(CancellationToken ct)
    {
        Calls++; var count = Interlocked.Increment(ref active); MaxConcurrency = Math.Max(MaxConcurrency, count);
        try
        {
            if (Fail) throw new System.Net.Http.HttpRequestException();
            var gate = Gate;
            if (gate is not null) { WaitingOnGate = true; return await gate.Task.WaitAsync(ct); }
            return Snapshot(Current);
        }
        finally { Interlocked.Decrement(ref active); }
    }
}
sealed class FakeConnection : IQueueConnection
{
    public bool IsConnected { get; private set; }
    public Guid[] Subscriptions = [];
    public bool FailConnect;
    public bool Disposed;
    public event Action<QueueChanged>? Changed;
    public event Action? ConnectionChanged;
    public int HandlerCount => (Changed?.GetInvocationList().Length ?? 0) + (ConnectionChanged?.GetInvocationList().Length ?? 0);
    public Task EnsureConnectedAsync(Guid[] roomIds, CancellationToken ct)
    {
        if (FailConnect) throw new System.Net.Http.HttpRequestException();
        Subscriptions = roomIds; IsConnected = true; return Task.CompletedTask;
    }
    public void Emit() => Changed?.Invoke(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "Called", DateTime.UtcNow));
    public void Disconnect() { IsConnected = false; ConnectionChanged?.Invoke(); }
    public ValueTask DisposeAsync() { Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
}
