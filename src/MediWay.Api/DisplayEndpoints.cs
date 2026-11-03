using System.Data;
using MediWay.Contracts;
using Npgsql;

namespace MediWay.Api;

public static class DisplayEndpoints
{
    public static void MapDisplayEndpoints(this WebApplication app) => app.MapGet("/api/display/snapshot", ReadSnapshot);

    private static async Task<IResult> ReadSnapshot(NpgsqlDataSource db, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        DateTime asOf;
        DateOnly date;
        await using (var command = new NpgsqlCommand("SELECT now(), (now() AT TIME ZONE 'Asia/Seoul')::date", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            asOf = reader.GetDateTime(0); date = reader.GetFieldValue<DateOnly>(1);
        }
        var rooms = new List<(Guid Id, string Name, string Department,string? FloorCode,string? FloorName)>();
        await using (var command = new NpgsqlCommand("SELECT r.id,r.name,d.name,f.code,f.name FROM rooms r JOIN departments d ON d.id=r.department_id LEFT JOIN map_floors f ON f.id=r.floor_id ORDER BY f.code NULLS LAST,r.code", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) rooms.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4)));

        var waiting = rooms.ToDictionary(r => r.Id, _ => new List<QueueItem>());
        var current = new Dictionary<Guid, DisplayCurrent>();
        await using (var command = new NpgsqlCommand("""
            SELECT id,room_id,service_date,queue_number,status,version,created_at FROM visits
            WHERE (service_date=@date AND status='Waiting') OR status IN ('Called','InService')
            ORDER BY queue_priority DESC,queue_number
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("date", date);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var roomId = reader.GetGuid(1);
                var status = reader.GetString(4);
                if (status == "Waiting")
                    waiting[roomId].Add(new(reader.GetGuid(0), reader.GetInt32(3), status, reader.GetInt32(5), reader.GetDateTime(6)));
                else current[roomId] = new(reader.GetGuid(0), reader.GetInt32(3), status, reader.GetInt32(5), reader.GetFieldValue<DateOnly>(2));
            }
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new DisplaySnapshot(asOf, date,
            rooms.Select(r => new DisplayRoomSnapshot(r.Id, r.Name, r.Department, current.GetValueOrDefault(r.Id), waiting[r.Id],r.FloorCode,r.FloorName)).ToArray()));
    }
}
