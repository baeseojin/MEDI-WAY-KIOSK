using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MediWay.Contracts;
using MediWay.Display.Services;
using MediWay.Display.ViewModels;

internal static class QueueOrderIntegration
{
    public static async Task RunAsync(string address)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        var r1 = Guid.Parse("20000000-0000-0000-0000-000000000001"); var r2 = Guid.Parse("20000000-0000-0000-0000-000000000002");
        async Task<VisitResponse> Register(int n, Guid room)
        {
            using var response = await http.PostAsJsonAsync("api/visits", new CreateVisitRequest(Guid.Parse($"30000000-0000-0000-0000-{n:D12}"), room, Guid.NewGuid()));
            if (response.StatusCode != HttpStatusCode.Created) throw new Exception(await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<CreateVisitResponse>())!.Visit;
        }
        Task<QueueResponse?> Read(bool refresh = true) => http.GetFromJsonAsync<QueueResponse>($"api/rooms/{r1}/queue?refresh={refresh.ToString().ToLowerInvariant()}");
        static QueueOrderRequest Request(QueueResponse queue, IEnumerable<Guid> ids) => new(Guid.NewGuid(), queue.ServiceDate, queue.Items.Select(x => new QueueOrderEntry(x.VisitId, x.Version)).ToArray(), ids.ToArray());
        async Task<int> Order(QueueOrderRequest request, Guid? room = null)
        {
            using var response = await http.PostAsJsonAsync($"api/rooms/{room ?? r1}/queue/order", request);
            return (int)response.StatusCode;
        }
        async Task<(int Status, VisitResponse? Visit)> Change(QueueItem item, string action)
        {
            using var response = await http.PostAsJsonAsync($"api/visits/{item.VisitId}/{action}", new TransitionRequest(item.Version));
            return ((int)response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<VisitResponse>() : null);
        }
        var a = await Register(1, r1); var b = await Register(2, r1); var c = await Register(3, r1); await Register(4, r2);
        await using var connection = new QueueConnection(new Uri(address));
        var events = new ConcurrentQueue<QueueChanged>(); connection.Changed += events.Enqueue;
        await connection.EnsureConnectedAsync([r1], default);
        await using var display = new DisplayViewModel(new DisplayApi(http), new QueueConnection(new Uri(address)), a => a(), TimeSpan.FromMilliseconds(100));
        display.Start(); await Until(() => display.IsFresh && display.Rooms.Single(r => r.RoomId == r1).WaitingCount == 3);
        var initial = (await Read())!;
        Check(initial.Items.Select(x => x.QueueNumber).SequenceEqual([1, 2, 3]), "Default queue preserves original number order");
        var request = Request(initial, [c.Id, a.Id, b.Id]);
        Check(await Order(request) == 204, "Priority move commits successfully");
        var priority = (await Read(false))!;
        Check(priority.Items.Select(x => x.QueueNumber).SequenceEqual([3, 1, 2]) && priority.Items.All(x => x.Version == 2), "Queue order changes without renumbering and increments optimistic versions");
        await Until(() => events.Any(e => e.Status == "QueueReordered") && display.Rooms.Single(r => r.RoomId == r1).Waiting[0].VisitId == c.Id);
        Check(events.All(e => e.RoomId == r1) && display.Rooms.Single(r => r.RoomId == r2).WaitingCount == 1, "Room-scoped SignalR updates DID order without changing other room");
        var eventCount = events.Count;
        Check(await Order(request) == 204, "Lost-response retry is idempotent");
        await Task.Delay(100);
        var replay = (await Read())!;
        Check(replay.Items.SequenceEqual(priority.Items) && events.Count == eventCount, "Replay changes neither visit versions nor notification count");
        Check(await Order(request with { OrderedVisitIds = [a.Id, b.Id, c.Id] }) == 409, "Request ID cannot be reused with a different payload");
        Check(await Order(Request(priority, priority.Items.Select(x => x.VisitId))) == 204, "Already identical order is a valid no-op");
        Check((await Read())!.Items.SequenceEqual(priority.Items), "No-op does not increment versions");
        Check(await Order(request with { RequestId = Guid.NewGuid() }) == 409, "Old queue snapshot cannot overwrite a newer order");
        Check(await Order(request with { RequestId = Guid.NewGuid(), OrderedVisitIds = [a.Id, a.Id, c.Id] }) == 400, "Duplicate or incomplete ordered IDs rejected");
        Check(await Order(Request(priority, priority.Items.Select(x => x.VisitId)) with { ServiceDate = priority.ServiceDate.AddDays(-1) }) == 409, "Previous-day queue cannot be reordered");
        Check(await Order(Request(priority, priority.Items.Select(x => x.VisitId)), Guid.NewGuid()) == 404, "Unknown room rejected");
        var other = (await http.GetFromJsonAsync<QueueResponse>($"api/rooms/{r2}/queue?refresh=true"))!;
        var cross = Request(priority, priority.Items.Select(x => x.VisitId));
        cross = cross with { Expected = [new(other.Items[0].VisitId, other.Items[0].Version)], OrderedVisitIds = [other.Items[0].VisitId] };
        Check(await Order(cross) == 409, "Another room's waiting visit cannot enter this queue");
        var first = Request(priority, priority.Items.Reverse().Select(x => x.VisitId));
        var second = Request(priority, [a.Id, b.Id, c.Id]);
        var races = await Task.WhenAll(Order(first), Order(second));
        Check(races.Count(x => x == 204) == 1 && races.Count(x => x == 409) == 1, "Concurrent staff reorder has exactly one winner");
        var beforeAdd = (await Read())!;
        var add = Register(5, r1); var reorder = Order(Request(beforeAdd, beforeAdd.Items.Reverse().Select(x => x.VisitId)));
        var newVisit = await add; var reorderStatus = await reorder;
        var added = (await Read())!;
        Check(reorderStatus is 204 or 409 && added.Items.Count == 4 && added.Items.Last().VisitId == newVisit.Id, "Concurrent registration is preserved at tail of saved queue");
        Check(await Order(Request(beforeAdd, beforeAdd.Items.Select(x => x.VisitId))) == 409, "New registration invalidates old full-list snapshot");
        Check((await Change(initial.Items[0], "call")).Status == 409, "Old call button version cannot bypass reordered queue state");
        var head = added.Items[0]; var called = await Change(head, "call");
        Check(called.Status == 200 && called.Visit!.QueueNumber == head.QueueNumber, "Fresh selected waiting visit can be called with original number");
        Check(await Order(Request(added, added.Items.Reverse().Select(x => x.VisitId))) == 409, "Called visit is excluded from reorder transaction");
        var waiting = (await Read())!;
        Check(await Order(Request(waiting, waiting.Items.Reverse().Select(x => x.VisitId))) == 204, "Remaining waiting queue can change while room is busy");
        var current = (await http.GetFromJsonAsync<RoomCurrentResponse>($"api/rooms/{r1}/current"))!.Visit!;
        Check(current.Id == head.VisitId && current.Version == called.Visit!.Version, "Reorder never changes active Called visit");
        var last = (await Read())!;
        var cancel = Change(last.Items[0], "cancel"); var reorderCancel = Order(Request(last, last.Items.Reverse().Select(x => x.VisitId)));
        var cancelResult = await cancel; var orderResult = await reorderCancel;
        Check((cancelResult.Status == 200 && orderResult == 409) || (cancelResult.Status == 409 && orderResult == 204), "Concurrent cancellation and reorder share same room lock");
        var snapshot = (await http.GetFromJsonAsync<DisplaySnapshot>("api/display/snapshot"))!;
        var final = (await Read())!;
        Check(snapshot.Rooms.Single(r => r.RoomId == r1).Waiting.Select(x => x.VisitId).SequenceEqual(final.Items.Select(x => x.VisitId)), "DID and staff APIs return identical persisted waiting order");
        Console.WriteLine("ALL STEP 14 API AND REALTIME TESTS PASSED");
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    private static async Task Until(Func<bool> value)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!value()) await Task.Delay(20, timeout.Token);
    }
}
