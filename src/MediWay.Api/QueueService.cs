using System.Text.Json;
using MediWay.Contracts;
using Npgsql;
using StackExchange.Redis;

namespace MediWay.Api;

public sealed class QueueService(NpgsqlDataSource db, IConnectionMultiplexer redis,
    IConfiguration configuration, ILogger<QueueService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeZoneInfo HospitalZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
    private readonly string prefix = configuration["QueueCache:KeyPrefix"] ?? "mediway";

    private string Key(Guid roomId, DateOnly date) => $"{prefix}:queue:{date:yyyy-MM-dd}:{roomId:D}";

    public async Task<(QueueResponse? Queue, string Source)> GetAsync(Guid roomId, bool refresh, CancellationToken ct)
    {
        // One date per request, shared by SQL and Redis key. No terminal-provided dates.
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HospitalZone));
        var key = Key(roomId, date);
        if (!refresh)
        {
            try
            {
                var json = await redis.GetDatabase().StringGetAsync(key).WaitAsync(CacheTimeout, ct);
                if (json.HasValue)
                {
                    var cached = JsonSerializer.Deserialize<QueueResponse>((string)json!);
                    if (cached is not null && cached.RoomId == roomId && cached.ServiceDate == date
                        && cached.Items is not null
                        && cached.Items.All(x => x is not null && x.Status == "Waiting" && x.QueueNumber > 0 && x.Version > 0))
                        return (cached, "Redis");
                    logger.LogWarning("Ignoring invalid queue cache payload");
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException or NotSupportedException)
            {
                logger.LogWarning("Queue cache read failed: {ExceptionType}", ex.GetType().Name);
            }
        }

        await using var connection = await db.OpenConnectionAsync(ct);
        await using (var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM rooms WHERE id=@room)", connection))
        {
            exists.Parameters.AddWithValue("room", roomId);
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!) return (null, "PostgreSQL");
        }
        var items = new List<QueueItem>();
        await using (var command = new NpgsqlCommand("""
            SELECT id,queue_number,status,version,created_at FROM visits
            WHERE room_id=@room AND service_date=@date AND status='Waiting'
            ORDER BY queue_priority DESC,queue_number
            """, connection))
        {
            command.Parameters.AddWithValue("room", roomId);
            command.Parameters.AddWithValue("date", date);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                items.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetDateTime(4)));
        }
        var response = new QueueResponse(roomId, date, items);
        ct.ThrowIfCancellationRequested();
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(response), CacheTtl)
                .WaitAsync(CacheTimeout, ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("Queue cache write failed: {ExceptionType}", ex.GetType().Name);
        }
        return (response, "PostgreSQL");
    }

    public async Task InvalidateAsync(Guid roomId, DateOnly date)
    {
        // The DB is already committed. Cache failure or caller disconnect must not undo registration.
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(Key(roomId, date)).WaitAsync(CacheTimeout);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("Queue cache invalidation failed: {ExceptionType}", ex.GetType().Name);
        }
    }
}

public static class QueueEndpoints
{
    public static void MapQueueEndpoints(this WebApplication app) =>
        app.MapGet("/api/rooms/{roomId:guid}/queue", async (
            Guid roomId, bool? refresh, QueueService queue, HttpContext context, CancellationToken ct) =>
        {
            var result = await queue.GetAsync(roomId, refresh ?? false, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Queue-Source"] = result.Source;
            return result.Queue is null
                ? Results.NotFound(new ApiError("ROOM_NOT_FOUND", "진료실을 찾을 수 없습니다."))
                : Results.Ok(result.Queue);
        });
}
