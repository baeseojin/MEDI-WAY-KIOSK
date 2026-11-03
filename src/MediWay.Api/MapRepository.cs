using MediWay.Contracts;
using Npgsql;

namespace MediWay.Api;

internal static class MapRepository
{
    internal static async Task<FloorMap> LoadMap(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid floor, CancellationToken ct)
    {
        string code, name; double width, height;
        await using (var command = new NpgsqlCommand("SELECT code,name,width,height FROM map_floors WHERE id=@floor", connection, transaction))
        {
            command.Parameters.AddWithValue("floor", floor);
            await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            code = reader.GetString(0); name = reader.GetString(1); width = reader.GetDouble(2); height = reader.GetDouble(3);
        }
        var nodes = new List<MapNode>(); var links = new List<MapLink>(); var zones = new List<MapZone>();
        await using (var command = new NpgsqlCommand("SELECT id,name,kind,x,y FROM map_nodes WHERE floor_id=@floor ORDER BY id", connection, transaction))
        {
            command.Parameters.AddWithValue("floor", floor);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) nodes.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4)));
        }
        await using (var command = new NpgsqlCommand("SELECT id,from_node_id,to_node_id,distance_meters,weight,is_closed,is_bidirectional FROM map_links WHERE floor_id=@floor ORDER BY id", connection, transaction))
        {
            command.Parameters.AddWithValue("floor", floor);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) links.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetBoolean(5), reader.GetBoolean(6)));
        }
        await using (var command = new NpgsqlCommand("SELECT name,kind,x,y,width,height FROM map_zones WHERE floor_id=@floor ORDER BY id", connection, transaction))
        {
            command.Parameters.AddWithValue("floor", floor);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) zones.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5)));
        }
        return new(floor, code, name, width, height, nodes, links, zones);
    }
}
