using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediWay.Contracts;
using Npgsql;

namespace MediWay.Api;

public static class QueueOrderEndpoints
{
    public static void MapQueueOrderEndpoints(this WebApplication app) => app.MapPost("/api/rooms/{roomId:guid}/queue/order", Change);

    private static async Task<IResult> Change(Guid roomId, QueueOrderRequest request, NpgsqlDataSource db, QueueService queue, QueueEvents events, CancellationToken ct)
    {
        if (roomId == Guid.Empty || request.RequestId == Guid.Empty || request.Expected is null || request.OrderedVisitIds is null
            || request.Expected.Count is < 1 or > 1000 || request.OrderedVisitIds.Count != request.Expected.Count
            || request.Expected.Any(x => x is null || x.VisitId == Guid.Empty || x.Version <= 0)
            || request.Expected.Select(x => x.VisitId).Distinct().Count() != request.Expected.Count
            || request.OrderedVisitIds.Distinct().Count() != request.Expected.Count
            || !request.OrderedVisitIds.ToHashSet().SetEquals(request.Expected.Select(x => x.VisitId)))
            return Results.BadRequest(new ApiError("INVALID_ORDER", "현재 대기 목록 전체의 ID·버전과 중복 없는 변경 순서가 필요합니다."));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(roomId.ToString("D") + JsonSerializer.Serialize(request))));
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        NpgsqlCommand Command(string sql) => new(sql, connection, tx) { CommandTimeout = 5 };
        async Task Lock(string key)
        {
            await using var command = Command("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))");
            command.Parameters.AddWithValue("key", key); await command.ExecuteNonQueryAsync(ct);
        }
        await Lock("queue-order-request:" + request.RequestId.ToString("D"));
        await using (var command = Command("SELECT fingerprint FROM queue_order_requests WHERE request_id=@id"))
        {
            command.Parameters.AddWithValue("id", request.RequestId);
            if (await command.ExecuteScalarAsync(ct) is string prior)
                return prior == fingerprint ? Results.NoContent() : Results.Conflict(new ApiError("REQUEST_KEY_CONFLICT", "같은 요청 ID를 다른 순서 변경에 사용할 수 없습니다."));
        }
        await Lock("room-state:" + roomId.ToString("D"));
        await using (var command = Command("SELECT EXISTS(SELECT 1 FROM rooms WHERE id=@room)"))
        {
            command.Parameters.AddWithValue("room", roomId);
            if (!(bool)(await command.ExecuteScalarAsync(ct))!) return Results.NotFound(new ApiError("ROOM_NOT_FOUND", "진료실을 찾을 수 없습니다."));
        }
        await using (var command = Command("SELECT (clock_timestamp() AT TIME ZONE 'Asia/Seoul')::date"))
            if ((DateOnly)(await command.ExecuteScalarAsync(ct))! != request.ServiceDate)
                return Results.Conflict(new ApiError("PAST_QUEUE", "오늘 대기 목록만 변경할 수 있습니다. 새로고침해 주세요."));
        var current = new List<QueueOrderEntry>();
        await using (var command = Command("SELECT id,version FROM visits WHERE room_id=@room AND service_date=@date AND status='Waiting' ORDER BY queue_priority DESC,queue_number FOR UPDATE"))
        {
            command.Parameters.AddWithValue("room", roomId); command.Parameters.AddWithValue("date", request.ServiceDate);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) current.Add(new(reader.GetGuid(0), reader.GetInt32(1)));
        }
        if (!current.SequenceEqual(request.Expected))
            return Results.Conflict(new ApiError("QUEUE_CHANGED", "접수나 대기 순서가 다른 화면에서 변경되었습니다. 최신 목록을 확인해 주세요."));
        var changed = !current.Select(x => x.VisitId).SequenceEqual(request.OrderedVisitIds);
        if (changed)
        {
            await using var command = Command("""
                UPDATE visits v SET queue_priority=@count-o.position+1,version=v.version+1
                FROM unnest(@ids::uuid[]) WITH ORDINALITY AS o(id,position) WHERE v.id=o.id
                """);
            command.Parameters.AddWithValue("count", (long)current.Count);
            command.Parameters.AddWithValue("ids", request.OrderedVisitIds.ToArray());
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var command = Command("INSERT INTO queue_order_requests(request_id,room_id,service_date,fingerprint) VALUES(@id,@room,@date,@fingerprint)"))
        {
            command.Parameters.AddWithValue("id", request.RequestId); command.Parameters.AddWithValue("room", roomId);
            command.Parameters.AddWithValue("date", request.ServiceDate); command.Parameters.AddWithValue("fingerprint", fingerprint);
            await command.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (changed)
        {
            await queue.InvalidateAsync(roomId, request.ServiceDate);
            var first = current.Single(x => x.VisitId == request.OrderedVisitIds[0]);
            await events.PublishOrderAsync(roomId, first.VisitId, first.Version + 1);
        }
        return Results.NoContent();
    }
}
