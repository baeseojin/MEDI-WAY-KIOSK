using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediWay.Contracts;
using Npgsql;
using QRCoder;
using StackExchange.Redis;

namespace MediWay.Api;

public sealed record MobileGuideSession(Guid VisitId, Guid RoomId, Guid StartNodeId, DateTimeOffset ExpiresAt);
public sealed class MobileGuideUnavailable : Exception { }

public sealed class MobileGuideStore(IConnectionMultiplexer redis, IConfiguration configuration, ILogger<MobileGuideStore> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly string prefix = configuration["QueueCache:KeyPrefix"] ?? "mediway";
    private string Key(string token) => $"{prefix}:mobile-guide:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant()}";
    public async Task SaveAsync(string token, MobileGuideSession session, CancellationToken ct)
    {
        try { await redis.GetDatabase().StringSetAsync(Key(token), JsonSerializer.Serialize(session), Lifetime).WaitAsync(TimeSpan.FromSeconds(2), ct); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { logger.LogWarning("Mobile guide store unavailable: {Type}", ex.GetType().Name); throw new MobileGuideUnavailable(); }
    }
    public async Task<MobileGuideSession?> ReadAsync(string token, CancellationToken ct)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(token)).WaitAsync(TimeSpan.FromSeconds(2), ct);
            if (!value.HasValue) return null;
            try { return JsonSerializer.Deserialize<MobileGuideSession>((string)value!); }
            catch (JsonException) { return null; }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { logger.LogWarning("Mobile guide lookup unavailable: {Type}", ex.GetType().Name); throw new MobileGuideUnavailable(); }
    }
    public async Task RemoveAsync(string token)
    {
        try { await redis.GetDatabase().KeyDeleteAsync(Key(token)).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { logger.LogWarning("Mobile guide cleanup failed: {Type}", ex.GetType().Name); }
    }
}

public static class MobileGuideEndpoints
{
    public static void MapMobileGuideEndpoints(this WebApplication app)
    {
        var configured = app.Configuration["MobileGuide:PublicBaseUrl"] ?? "http://localhost:5080/";
        if (!Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var publicBase) || publicBase.Scheme is not ("http" or "https")
            || publicBase.UserInfo.Length > 0 || publicBase.Query.Length > 0 || publicBase.Fragment.Length > 0 || configured.Length > 512)
            throw new InvalidOperationException("MobileGuide__PublicBaseUrl must be an absolute HTTP(S) base URL without credentials, query or fragment.");

        app.MapPost("/api/visits/{visitId:guid}/mobile-guide", async (Guid visitId, CreateMobileGuideRequest request, NpgsqlDataSource db, NavigationService navigation, MobileGuideStore store, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (visitId == Guid.Empty || request.StartNodeId == Guid.Empty) return Results.BadRequest(new ApiError("INVALID_REQUEST", "유효한 접수와 키오스크 위치가 필요합니다."));
            var visit = await Visit(db, visitId, ct);
            if (visit is null) return Results.NotFound(new ApiError("VISIT_NOT_FOUND", "접수를 찾을 수 없습니다."));
            if (visit.Value.Status is not ("Waiting" or "Called" or "InService"))
                return Results.Conflict(new ApiError("VISIT_NOT_ACTIVE", "완료·취소된 접수는 모바일 안내를 발급할 수 없습니다."));
            await navigation.RouteAsync(request.StartNodeId, null, visit.Value.Room, ct);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var expires = DateTimeOffset.UtcNow + MobileGuideStore.Lifetime;
            await store.SaveAsync(token, new(visitId, visit.Value.Room, request.StartNodeId, expires), ct);
            var url = new Uri(publicBase, "mobile/#t=" + token).AbsoluteUri;
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            using var png = new PngByteQRCode(data);
            return Results.Json(new MobileGuideTicket(url, expires, Convert.ToBase64String(png.GetGraphic(8)), publicBase.IsLoopback), statusCode: 201);
        });
        app.MapGet("/api/mobile-guide", async (NpgsqlDataSource db, NavigationService navigation, MobileGuideStore store, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
                return Results.Json(new ApiError("GUIDE_TOKEN_REQUIRED", "키오스크 QR을 다시 스캔해 주세요."), statusCode: 401);
            var session = await store.ReadAsync(token, ct);
            if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow || session.VisitId == Guid.Empty || session.RoomId == Guid.Empty || session.StartNodeId == Guid.Empty)
                return Results.Json(new ApiError("GUIDE_EXPIRED", "유효하지 않거나 만료된 안내입니다. 키오스크 QR을 다시 받아 주세요."), statusCode: 410);
            var visit = await Visit(db, session.VisitId, ct);
            if (visit is null || visit.Value.Room != session.RoomId || visit.Value.Status is not ("Waiting" or "Called" or "InService"))
            {
                await store.RemoveAsync(token);
                return Results.Json(new ApiError("GUIDE_ENDED", "접수가 완료·취소되어 안내가 종료되었습니다."), statusCode: 410);
            }
            var route = await navigation.RouteAsync(session.StartNodeId, null, session.RoomId, ct);
            return Results.Ok(new MobileGuideResponse(route, session.ExpiresAt, visit.Value.Number, visit.Value.Status));
        });
    }
    private static async Task<(Guid Room, string Status, int Number)?> Visit(NpgsqlDataSource db, Guid id, CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT room_id,status,queue_number FROM visits WHERE id=@id"); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2)) : null;
    }
}
