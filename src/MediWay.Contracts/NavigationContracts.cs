namespace MediWay.Contracts;

public sealed record MapNode(Guid Id, string Name, string Kind, double X, double Y);
public sealed record MapLink(Guid Id, Guid FromNodeId, Guid ToNodeId, double DistanceMeters, double Weight, bool IsClosed, bool IsBidirectional);
public sealed record MapZone(string Name, string Kind, double X, double Y, double Width, double Height);
public sealed record FloorMap(Guid Id, string Code, string Name, double Width, double Height,
    IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapLink> Links, IReadOnlyList<MapZone> Zones);
public sealed record NavigationRoute(FloorMap Map, Guid StartNodeId, Guid DestinationNodeId, string DestinationName,
    double DistanceMeters, double TotalCost, IReadOnlyList<MapNode> Points)
{
    public string Algorithm { get; init; } = "AStar";
    public IReadOnlyList<NavigationRoute> Legs { get; init; } = [];
    public string TransferNotice { get; init; } = "";
}
