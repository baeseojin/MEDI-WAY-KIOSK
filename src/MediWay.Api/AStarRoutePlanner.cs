using System.IO;
using MediWay.Contracts;

namespace MediWay.Api;

public static class AStarRoutePlanner
{
    public static NavigationRoute? Find(FloorMap map, Guid start, Guid destination, string destinationName)
    {
        var nodes = map.Nodes.ToDictionary(n => n.Id);
        if (!nodes.ContainsKey(start) || !nodes.ContainsKey(destination)) return null;
        if (!double.IsFinite(map.Width) || !double.IsFinite(map.Height) || map.Width <= 0 || map.Height <= 0
            || nodes.Values.Any(n => !double.IsFinite(n.X) || !double.IsFinite(n.Y) || n.X < 0 || n.Y < 0 || n.X > map.Width || n.Y > map.Height)
            || map.Zones.Any(z => !double.IsFinite(z.X) || !double.IsFinite(z.Y) || !double.IsFinite(z.Width) || !double.IsFinite(z.Height)
                || z.X < 0 || z.Y < 0 || z.Width <= 0 || z.Height <= 0 || z.X + z.Width > map.Width || z.Y + z.Height > map.Height))
            throw new InvalidDataException("Invalid map coordinates");
        var adjacent = nodes.Keys.ToDictionary(id => id, _ => new List<(Guid Next, MapLink Link)>());
        var heuristicScale = double.PositiveInfinity;
        foreach (var edge in map.Links.OrderBy(e => e.Id))
        {
            if (!nodes.ContainsKey(edge.FromNodeId) || !nodes.ContainsKey(edge.ToNodeId)
                || !double.IsFinite(edge.Weight) || edge.Weight <= 0 || !double.IsFinite(edge.DistanceMeters) || edge.DistanceMeters <= 0)
                throw new InvalidDataException("Invalid map link");
            if (edge.IsClosed) continue;
            var span = Euclidean(nodes[edge.FromNodeId], nodes[edge.ToNodeId]);
            if (span > 0) heuristicScale = Math.Min(heuristicScale, edge.Weight / span);
            adjacent[edge.FromNodeId].Add((edge.ToNodeId, edge));
            if (edge.IsBidirectional) adjacent[edge.ToNodeId].Add((edge.FromNodeId, edge));
        }
        var costs = nodes.Keys.ToDictionary(id => id, _ => double.PositiveInfinity);
        var previous = new Dictionary<Guid, (Guid Node, MapLink Link)>();
        // Convert drawing units to a lower bound on traversal cost. Raw Euclidean distance
        // could overestimate when link weights use a different scale, breaking A* optimality.
        if (!double.IsFinite(heuristicScale)) heuristicScale = 0;
        heuristicScale *= 1 - 1e-12; // small numerical margin, still admissible
        double Heuristic(Guid node) => heuristicScale * Euclidean(nodes[node], nodes[destination]);
        var queue = new PriorityQueue<(Guid Node, double G), (double F, Guid Id)>();
        costs[start] = 0; queue.Enqueue((start, 0), (Heuristic(start), start));
        while (queue.TryDequeue(out var entry, out _))
        {
            var current = entry.Node;
            if (entry.G > costs[current]) continue;
            if (current == destination) break;
            foreach (var (next, edge) in adjacent[current])
            {
                var nextCost = costs[current] + edge.Weight;
                if (!double.IsFinite(nextCost)) throw new InvalidDataException("Route cost overflow");
                if (nextCost >= costs[next]) continue;
                var estimatedTotal = nextCost + Heuristic(next);
                if (!double.IsFinite(estimatedTotal)) throw new InvalidDataException("Route estimate overflow");
                costs[next] = nextCost; previous[next] = (current, edge); queue.Enqueue((next, nextCost), (estimatedTotal, next));
            }
        }
        if (double.IsPositiveInfinity(costs[destination])) return null;
        var points = new List<MapNode> { nodes[destination] };
        var cursor = destination; double distance = 0;
        while (cursor != start)
        {
            var step = previous[cursor]; distance += step.Link.DistanceMeters; cursor = step.Node; points.Add(nodes[cursor]);
        }
        if (!double.IsFinite(distance)) throw new InvalidDataException("Route distance overflow");
        points.Reverse();
        return new(map, start, destination, destinationName, distance, costs[destination], points);
    }
    private static double Euclidean(MapNode a, MapNode b)
    {
        var dx = Math.Abs(a.X - b.X); var dy = Math.Abs(a.Y - b.Y); var max = Math.Max(dx, dy);
        if (max == 0) return 0;
        var distance = max * Math.Sqrt(Math.Pow(dx / max, 2) + Math.Pow(dy / max, 2));
        return double.IsFinite(distance) ? distance : throw new InvalidDataException("Coordinate distance overflow");
    }
}
