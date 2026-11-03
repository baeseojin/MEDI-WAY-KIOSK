using MediWay.Api;
using MediWay.Contracts;

static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
static Guid Id(int n) => new(n, 0, 0, new byte[8]);
var nodes = new MapNode[] { new(Id(1), "출발", "Kiosk", 0, 0), new(Id(2), "경유", "Corridor", 99, 0), new(Id(3), "도착", "Room", 1, 0) };
var links = new MapLink[] { new(Id(101), Id(1), Id(3), 1, 5, false, false), new(Id(102), Id(1), Id(2), 99, 1, false, false), new(Id(103), Id(2), Id(3), 98, 1, false, false) };
var map = new FloorMap(Id(500), "TEST", "테스트 지도", 100, 100, nodes, links, []);
var route = AStarRoutePlanner.Find(map, Id(1), Id(3), "도착")!;
Check(route.Algorithm == "AStar" && route.TotalCost == 2 && route.Points.Select(p => p.Id).SequenceEqual(new[] { Id(1), Id(2), Id(3) }), "Scaled A* heuristic avoids suboptimal direct route when weights differ from drawing distances");
Check(route.DistanceMeters == 197, "Physical distance is separate from optimized traversal cost");
var closed = map with { Links = links.Select(e => e.Id == Id(102) ? e with { IsClosed = true } : e).ToArray() };
Check(AStarRoutePlanner.Find(closed, Id(1), Id(3), "도착")!.TotalCost == 5, "Closed edge is excluded");
Check(AStarRoutePlanner.Find(map, Id(3), Id(1), "출발") is null, "One-way links are respected");
var both = map with { Links = links.Select(e => e with { IsBidirectional = true }).ToArray() };
Check(AStarRoutePlanner.Find(both, Id(3), Id(1), "출발")!.TotalCost == 2, "Bidirectional links can be traversed in reverse");
var same = AStarRoutePlanner.Find(map, Id(1), Id(1), "출발")!;
Check(same.TotalCost == 0 && same.DistanceMeters == 0 && same.Points.Count == 1, "Identical start and destination return a zero-length route");
Check(AStarRoutePlanner.Find(map, Guid.NewGuid(), Id(3), "도착") is null, "Unknown node has no route");
var disconnected = map with { Links = links.Select(e => e with { IsClosed = true }).ToArray() };
Check(AStarRoutePlanner.Find(disconnected, Id(1), Id(3), "도착") is null, "Unreachable destination returns no route");
var colocated = map with { Nodes = nodes.Select(n => n with { X = 0, Y = 0 }).ToArray() };
Check(AStarRoutePlanner.Find(colocated, Id(1), Id(3), "도착")!.TotalCost == 2, "Coincident drawing coordinates safely use a zero heuristic");
var rejected = false; try { AStarRoutePlanner.Find(map with { Links = [links[0] with { Weight = -1 }] }, Id(1), Id(3), "도착"); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "Negative edge weight rejected");
rejected = false; try { AStarRoutePlanner.Find(map with { Nodes = [nodes[0] with { X = double.NaN }, nodes[1], nodes[2]] }, Id(1), Id(3), "도착"); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "Nonfinite coordinates rejected");
// Independent all-pairs dynamic-programming oracle; no production heuristic or queue code is reused.
var random = new Random(804);
for (var graph = 0; graph < 20; graph++)
{
    const int count = 8;
    var vertices = Enumerable.Range(1, count).Select(i => new MapNode(Id(i), $"N{i}", "Corridor", random.Next(101), random.Next(101))).ToArray();
    var edges = new List<MapLink>(); var oracle = new double[count, count];
    for (var i = 0; i < count; i++) for (var j = 0; j < count; j++) oracle[i, j] = i == j ? 0 : double.PositiveInfinity;
    for (var i = 0; i < count; i++) for (var j = 0; j < count; j++)
    {
        if (i == j || random.NextDouble() > .25) continue;
        var weight = random.Next(1, 901) / 100.0; var closedEdge = random.NextDouble() < .2; var bidirectional = random.NextDouble() < .3;
        edges.Add(new(Id(edges.Count + 100), Id(i + 1), Id(j + 1), random.Next(1, 100), weight, closedEdge, bidirectional));
        if (closedEdge) continue;
        oracle[i, j] = Math.Min(oracle[i, j], weight); if (bidirectional) oracle[j, i] = Math.Min(oracle[j, i], weight);
    }
    for (var k = 0; k < count; k++) for (var i = 0; i < count; i++) for (var j = 0; j < count; j++) oracle[i, j] = Math.Min(oracle[i, j], oracle[i, k] + oracle[k, j]);
    var generated = new FloorMap(Id(500), "RANDOM", "검증", 100, 100, vertices, edges, []);
    for (var i = 0; i < count; i++) for (var j = 0; j < count; j++)
    {
        var actual = AStarRoutePlanner.Find(generated, Id(i + 1), Id(j + 1), "도착");
        if (double.IsPositiveInfinity(oracle[i, j])) { if (actual is not null) throw new Exception("Unexpected reachable route"); }
        else if (actual is null || Math.Abs(actual.TotalCost - oracle[i, j]) > 1e-9) throw new Exception($"A* cost mismatch: graph {graph}, {i}->{j}");
    }
}
Check(true, "A* costs match independent Floyd-Warshall oracle for 1,280 random-graph node pairs");
Console.WriteLine("ALL STEP 8 A-STAR TESTS PASSED");
