using MediWay.Contracts;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace MediWay.Api;

public sealed class QueueHub(NpgsqlDataSource db) : Hub
{
    public static string RoomGroup(Guid roomId) => $"room:{roomId:D}";

    public async Task SubscribeRooms(Guid[] roomIds)
    {
        if (roomIds is null || roomIds.Length is < 1 or > 32 || roomIds.Any(x => x == Guid.Empty))
            throw new HubException("진료실을 1~32개 선택해 주세요.");
        var distinct = roomIds.Distinct().ToArray();
        await using var command = db.CreateCommand("SELECT count(*) FROM rooms WHERE id=ANY(@ids)");
        command.Parameters.AddWithValue("ids", distinct);
        if ((long)(await command.ExecuteScalarAsync(Context.ConnectionAborted))! != distinct.Length)
            throw new HubException("존재하지 않는 진료실입니다.");
        foreach (var id in distinct)
            await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(id), Context.ConnectionAborted);
    }
}

public sealed class QueueEvents(IHubContext<QueueHub> hub, ILogger<QueueEvents> logger)
{
    public Task PublishOrderAsync(Guid roomId, Guid visitId, int version) => SendAsync(new(Guid.NewGuid(), roomId, visitId, version, "QueueReordered", DateTime.UtcNow));
    public Task PublishStatusChangedAsync(Guid visitId, Guid roomId, int version, string status) => SendAsync(new(Guid.NewGuid(), roomId, visitId, version, status, DateTime.UtcNow));
    public Task PublishAsync(VisitResponse visit) => SendAsync(new(Guid.NewGuid(), visit.RoomId, visit.Id, visit.Version, visit.Status, DateTime.UtcNow));
    private async Task SendAsync(QueueChanged change)
    {
        // DB commit is complete. Do not bind delivery to the terminal's request cancellation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await hub.Clients.Group(QueueHub.RoomGroup(change.RoomId)).SendAsync("QueueChanged", change, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Queue event delivery failed after commit: {ExceptionType}", ex.GetType().Name);
        }
    }
}
