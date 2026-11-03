using System.Net.Http;
using System.Net.Http.Json;
using MediWay.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace MediWay.Display.Services;

public interface IDisplayApi
{
    Task<DisplaySnapshot> SnapshotAsync(CancellationToken ct);
}

public sealed class DisplayApi(HttpClient http) : IDisplayApi
{
    public async Task<DisplaySnapshot> SnapshotAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<DisplaySnapshot>("api/display/snapshot", ct)
        ?? throw new HttpRequestException("Empty display snapshot");
}

public interface IQueueConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    event Action<QueueChanged>? Changed;
    event Action? ConnectionChanged;
    Task EnsureConnectedAsync(Guid[] roomIds, CancellationToken ct);
}

public sealed class QueueConnection : IQueueConnection
{
    private readonly HubConnection hub;
    private readonly IDisposable subscription;
    private Guid[] subscribed = [];
    private volatile bool joined;
    public QueueConnection(Uri apiBase)
    {
        // Retry is owned by the display worker: initial failures and later disconnects use the same path.
        hub = new HubConnectionBuilder().WithUrl(new Uri(apiBase, "hubs/queue")).Build();
        hub.ServerTimeout = TimeSpan.FromSeconds(15);
        hub.KeepAliveInterval = TimeSpan.FromSeconds(5);
        subscription = hub.On<QueueChanged>("QueueChanged", change => Changed?.Invoke(change));
        hub.Closed += OnClosed;
    }
    public bool IsConnected => joined && hub.State == HubConnectionState.Connected;
    public event Action<QueueChanged>? Changed;
    public event Action? ConnectionChanged;
    private Task OnClosed(Exception? error)
    {
        joined = false; ConnectionChanged?.Invoke(); return Task.CompletedTask;
    }
    public async Task EnsureConnectedAsync(Guid[] roomIds, CancellationToken ct)
    {
        if (roomIds.Length == 0) return;
        if (hub.State == HubConnectionState.Disconnected) await hub.StartAsync(ct);
        if (!joined || !subscribed.SequenceEqual(roomIds))
        {
            await hub.InvokeAsync("SubscribeRooms", roomIds, ct);
            subscribed = roomIds.ToArray(); joined = true;
        }
    }
    public async ValueTask DisposeAsync()
    {
        hub.Closed -= OnClosed;
        subscription.Dispose(); joined = false;
        await hub.DisposeAsync();
    }
}
