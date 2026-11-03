using System.Data;
using System.IO;
using MediWay.Contracts;
using Npgsql;

namespace MediWay.Api;

public sealed class NavigationFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public ApiError Error { get; } = new(code, message);
}

public sealed class NavigationService(NpgsqlDataSource db)
{
    public async Task<NavigationRoute> RouteAsync(Guid start, Guid? destination, Guid? room, CancellationToken ct)
    {
        if (start == Guid.Empty || destination == Guid.Empty || room == Guid.Empty)
            throw new NavigationFailure(400, "INVALID_REQUEST", "유효한 출발지와 목적지가 필요합니다.");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var name = "";
        if (room is not null)
        {
            await using var command = new NpgsqlCommand("SELECT d.node_id,r.name FROM room_map_destinations d JOIN rooms r ON r.id=d.room_id WHERE d.room_id=@room", connection, transaction);
            command.Parameters.AddWithValue("room", room.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new NavigationFailure(404, "DESTINATION_NOT_FOUND", "진료실 길 안내가 등록되어 있지 않습니다.");
            destination = reader.GetGuid(0); name = reader.GetString(1);
        }
        Guid floor;
        await using (var command = new NpgsqlCommand("SELECT floor_id FROM map_nodes WHERE id=@start", connection, transaction))
        {
            command.Parameters.AddWithValue("start", start);
            if (await command.ExecuteScalarAsync(ct) is not Guid id) throw new NavigationFailure(404, "START_NOT_FOUND", "키오스크 위치를 찾을 수 없습니다.");
            floor = id;
        }
        Guid destinationFloor;
        await using (var command = new NpgsqlCommand("SELECT floor_id,name FROM map_nodes WHERE id=@destination", connection, transaction))
        {
            command.Parameters.AddWithValue("destination", destination!.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new NavigationFailure(404, "DESTINATION_NOT_FOUND", "목적지를 찾을 수 없습니다.");
            destinationFloor = reader.GetGuid(0);
            if (name.Length == 0) name = reader.GetString(1);
        }
        if (destinationFloor != floor)
        {
            NavigationRoute cross;
            try { cross = await CrossFloor(connection, transaction, floor, destinationFloor, start, destination.Value, name, ct); }
            catch (InvalidDataException) { throw new NavigationFailure(503,"MAP_DATA_INVALID","지도 정보를 확인할 수 없습니다. 안내 데스크에 문의해 주세요."); }
            await transaction.CommitAsync(ct); return cross;
        }
        var map = await MapRepository.LoadMap(connection, transaction, floor, ct);
        NavigationRoute? route;
        try { route = AStarRoutePlanner.Find(map, start, destination.Value, name); }
        catch (InvalidDataException) { throw new NavigationFailure(503, "MAP_DATA_INVALID", "지도 정보를 확인할 수 없습니다. 안내 데스크에 문의해 주세요."); }
        await transaction.CommitAsync(ct);
        return route ?? throw new NavigationFailure(409, "ROUTE_NOT_FOUND", "이동 가능한 경로가 없습니다. 안내 데스크에 문의해 주세요.");
    }
    private static async Task<NavigationRoute> CrossFloor(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid fromFloor,Guid toFloor,Guid start,Guid destination,string name,CancellationToken ct)
    {
        var connections=new List<(Guid From,Guid To,double Distance,double Weight,bool Closed)>();
        await using(var command=new NpgsqlCommand("""
            SELECT a.id,b.id,c.distance_meters,c.weight,c.is_closed,a.floor_id
            FROM map_floor_connections c JOIN map_nodes a ON a.id=c.from_node_id JOIN map_nodes b ON b.id=c.to_node_id
            WHERE a.kind='Elevator' AND b.kind='Elevator' AND
            ((a.floor_id=@from AND b.floor_id=@to) OR (c.is_bidirectional AND a.floor_id=@to AND b.floor_id=@from))
            ORDER BY c.id
            """,connection,transaction))
        {
            command.Parameters.AddWithValue("from",fromFloor);command.Parameters.AddWithValue("to",toFloor);
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var forward=reader.GetGuid(5)==fromFloor;
                connections.Add((reader.GetGuid(forward?0:1),reader.GetGuid(forward?1:0),reader.GetDouble(2),reader.GetDouble(3),reader.GetBoolean(4)));
            }
        }
        if(connections.Count==0)throw new NavigationFailure(409,"DIFFERENT_FLOOR","연결된 층간 이동 경로가 없습니다. 안내 데스크에 문의해 주세요.");
        var firstMap=await MapRepository.LoadMap(connection,transaction,fromFloor,ct);
        var secondMap=await MapRepository.LoadMap(connection,transaction,toFloor,ct);
        NavigationRoute? best=null;
        foreach(var transfer in connections.Where(x=>!x.Closed))
        {
            var first=AStarRoutePlanner.Find(firstMap,start,transfer.From,"엘리베이터");
            var second=AStarRoutePlanner.Find(secondMap,transfer.To,destination,name);
            if(first is null||second is null)continue;
            var cost=first.TotalCost+transfer.Weight+second.TotalCost;
            var distance=first.DistanceMeters+transfer.Distance+second.DistanceMeters;
            if(!double.IsFinite(cost)||!double.IsFinite(distance))throw new InvalidDataException();
            if(best is null||cost<best.TotalCost)best=first with {DestinationNodeId=destination,DestinationName=name,TotalCost=cost,DistanceMeters=distance,Legs=[first,second],
                TransferNotice=$"엘리베이터로 {firstMap.Name}에서 {secondMap.Name}으로 이동한 뒤 다음 층 지도를 확인해 주세요."};
        }
        return best??throw new NavigationFailure(409,"ROUTE_NOT_FOUND","이동 가능한 층간 경로가 없습니다. 안내 데스크에 문의해 주세요.");
    }
}
