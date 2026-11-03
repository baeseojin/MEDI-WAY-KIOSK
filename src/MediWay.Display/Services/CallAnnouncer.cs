using System.Threading.Channels;
using MediWay.Contracts;

namespace MediWay.Display.Services;

public interface IAnnouncementVoice : IDisposable
{
    bool IsAvailable { get; }
    string Description { get; }
    Task SpeakAsync(string text, CancellationToken ct);
}

public sealed class CallAnnouncer : IAsyncDisposable
{
    private sealed record Call(Guid Room, Guid Visit, int Version, string Text, DateTimeOffset At, long Generation, bool Test = false);
    private readonly IAnnouncementVoice voice;
    private readonly Func<DateTimeOffset> now;
    private readonly object gate = new();
    private readonly Channel<Call> queue = Channel.CreateBounded<Call>(new BoundedChannelOptions(16) { SingleReader = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, (Guid Visit, int Version)> current = [];
    private readonly HashSet<(Guid, int)> seen = [];
    private readonly Queue<(Guid, int)> seenOrder = [];
    private readonly Task worker;
    private CancellationTokenSource? speaking;
    private Call? active;
    private long generation;
    private bool initialized, enabled = true, disposed;
    private string error = "";

    public CallAnnouncer(IAnnouncementVoice voice, Func<DateTimeOffset>? now = null)
    {
        this.voice = voice; this.now = now ?? (() => DateTimeOffset.UtcNow);
        worker = Task.Run(RunAsync);
    }
    public event Action? Changed;
    public bool Enabled
    {
        get { lock (gate) return enabled; }
        set
        {
            lock (gate) { if (disposed || enabled == value) return; enabled = value; Invalidate(); }
            Changed?.Invoke();
        }
    }
    public string StatusText
    {
        get { lock (gate) return !voice.IsAvailable ? voice.Description : !enabled ? "음성 안내 꺼짐" : error.Length > 0 ? error : voice.Description; }
    }
    public void Observe(DisplaySnapshot snapshot)
    {
        lock (gate)
        {
            if (disposed) return;
            current.Clear();
            foreach (var room in snapshot.Rooms)
                if (room.Current is { Status: "Called" } call) current[room.RoomId] = (call.VisitId, call.Version);
            if (active is { Test: false } && !Valid(active)) speaking?.Cancel();
            foreach (var room in snapshot.Rooms.OrderBy(r => r.RoomId))
            {
                if (room.Current is not { Status: "Called" } call) continue;
                var key = (call.VisitId, call.Version);
                if (!seen.Add(key)) continue;
                seenOrder.Enqueue(key);
                while (seenOrder.Count > 1024) seen.Remove(seenOrder.Dequeue());
                if (!initialized || !enabled || !voice.IsAvailable) continue;
                var text = $"{call.QueueNumber}번 환자분, {room.RoomName}로 들어오세요.";
                if (!queue.Writer.TryWrite(new(room.RoomId, call.VisitId, call.Version, text, now(), generation)))
                    error = "음성 대기열이 가득 찼습니다. 전광판을 확인해 주세요.";
            }
            initialized = true;
        }
        Changed?.Invoke();
    }
    public void Suspend()
    {
        lock (gate) { if (disposed) return; Invalidate(); current.Clear(); initialized = false; }
    }
    public void Test()
    {
        lock (gate)
        {
            if (disposed || !enabled || !voice.IsAvailable) return;
            queue.Writer.TryWrite(new(Guid.Empty, Guid.Empty, 0, "음성 안내 테스트입니다. 호출번호와 진료실을 확인해 주세요.", now(), generation, true));
        }
    }
    private bool Valid(Call call) => call.Generation == generation && (call.Test || current.TryGetValue(call.Room, out var value) && value == (call.Visit, call.Version));
    private void Invalidate() { generation++; speaking?.Cancel(); while (queue.Reader.TryRead(out _)) { } }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var call in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                CancellationTokenSource cancellation;
                lock (gate)
                {
                    if (disposed || !enabled || !Valid(call) || now() - call.At > TimeSpan.FromSeconds(15)) continue;
                    speaking = cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); active = call;
                }
                try { await voice.SpeakAsync(call.Text, cancellation.Token); lock (gate) error = ""; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception) { lock (gate) error = "음성을 재생하지 못했습니다. 전광판을 확인해 주세요."; }
                finally
                {
                    lock (gate) { speaking = null; active = null; cancellation.Dispose(); }
                    Changed?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; Invalidate(); queue.Writer.TryComplete(); lifetime.Cancel(); }
        await worker; voice.Dispose(); lifetime.Dispose();
    }
}
