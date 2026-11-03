using System.Data;
using MediWay.Contracts;
using Npgsql;

namespace MediWay.Api;

public static class NavigationEndpoints
{
    public static void MapNavigationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/maps/{code}", async (string code, NpgsqlDataSource db, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await using var connection = await db.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            Guid floor;
            await using (var command = new NpgsqlCommand("SELECT id FROM map_floors WHERE code=@code", connection, transaction))
            {
                command.Parameters.AddWithValue("code", code.Trim().ToUpperInvariant());
                if (await command.ExecuteScalarAsync(ct) is not Guid id) return Results.NotFound(new ApiError("MAP_NOT_FOUND", "지도를 찾을 수 없습니다."));
                floor = id;
            }
            var map = await MapRepository.LoadMap(connection, transaction, floor, ct);
            await transaction.CommitAsync(ct); return Results.Ok(map);
        });
        app.MapGet("/api/navigation/routes", async (Guid startNodeId, Guid destinationNodeId, NavigationService service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.RouteAsync(startNodeId, destinationNodeId, null, ct));
        });
        app.MapGet("/api/navigation/rooms/{roomId:guid}/route", async (Guid roomId, Guid startNodeId, NavigationService service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.RouteAsync(startNodeId, null, roomId, ct));
        });
    }
}
