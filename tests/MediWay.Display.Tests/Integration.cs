using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MediWay.Contracts;
using MediWay.Display.Services;
using MediWay.Display.ViewModels;
using Microsoft.AspNetCore.SignalR;

static class Integration
{
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
    public static async Task Run(string root, string address)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
        Process? server = null;
        async Task StartServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, http.BaseAddress!.Port);
            try { probe.Start(); } finally { probe.Stop(); }
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, "src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll"));
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address);
            server = Process.Start(start)!;
            server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
            server.BeginOutputReadLine(); server.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                try { using var response = await http.GetAsync("health/live", timeout.Token); if (response.IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100, timeout.Token);
            }
        }
        async Task StopServer()
        {
            if (server is null) return;
            if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
            server.Dispose(); server = null;
        }
        async Task<CreateVisitResponse> Register(int patient, Guid room, Guid key)
        {
            using var response = await http.PostAsJsonAsync("api/visits", new CreateVisitRequest(
                Guid.Parse($"30000000-0000-0000-0000-{patient:D12}"), room, key));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CreateVisitResponse>())!;
        }
        async Task<VisitResponse> Change(Guid id, string action, int version)
        {
            using var response = await http.PostAsJsonAsync($"api/visits/{id}/{action}", new TransitionRequest(version));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<VisitResponse>())!;
        }
        try
        {
            await StartServer();
            var r1 = Guid.Parse("20000000-0000-0000-0000-000000000001");
            var r2 = Guid.Parse("20000000-0000-0000-0000-000000000002");
            await using var first = new QueueConnection(http.BaseAddress!);
            await using var second = new QueueConnection(http.BaseAddress!);
            var events1 = new ConcurrentQueue<QueueChanged>(); var events2 = new ConcurrentQueue<QueueChanged>();
            first.Changed += events1.Enqueue; second.Changed += events2.Enqueue;
            await first.EnsureConnectedAsync([r1], default); await second.EnsureConnectedAsync([r2], default);
            Check(first.IsConnected && second.IsConnected, "Real SignalR clients subscribe to separate room groups");
            var rejected = false;
            try { await second.EnsureConnectedAsync([Guid.NewGuid()], default); } catch (HubException) { rejected = true; }
            Check(rejected, "Unknown room subscription rejected");

            await using var vm = new DisplayViewModel(new DisplayApi(http), new QueueConnection(http.BaseAddress!), action => action(), TimeSpan.FromHours(1));
            vm.Start(); await Until(() => vm.IsFresh && vm.IsRealtime);
            Check(vm.Rooms.Count == 4, "Display loads consistent snapshot and joins live groups");
            var key = Guid.NewGuid();
            var visit = await Register(1, r1, key);
            await Until(() => events1.Count == 1 && vm.Rooms.Single(x => x.RoomId == r1).WaitingCount == 1);
            Check(events1.Single().Status == "Waiting" && events2.IsEmpty, "Registration notifies only its room and immediately refreshes waiting list");
            await Register(1, r1, key);
            var called = await Change(visit.Visit.Id, "call", 1);
            await Until(() => vm.Rooms.Single(x => x.RoomId == r1).Current?.Status == "Called" && events1.Count >= 2);
            Check(vm.Rooms.Single(x => x.RoomId == r1).WaitingCount == 0 && called.Version == 2, "Call event updates DID current number without polling");
            await Change(visit.Visit.Id, "call", 1);
            await Change(visit.Visit.Id, "start", 2);
            await Until(() => vm.Rooms.Single(x => x.RoomId == r1).Current?.Status == "InService" && events1.Count >= 3);
            Check(events1.Select(x => x.Status).SequenceEqual(new[] { "Waiting", "Called", "InService" }), "Idempotent registration and call retries do not republish events");
            await Change(visit.Visit.Id, "complete", 3);
            await Until(() => events1.Count == 4 && vm.Rooms.Single(x => x.RoomId == r1).Current is null);
            Check(events2.IsEmpty, "Completion clears DID and does not leak events to another room");
            var other = await Register(2, r2, Guid.NewGuid());
            await Change(other.Visit.Id, "cancel", 1);
            await Until(() => events2.Count == 2 && vm.Rooms.Single(x => x.RoomId == r2).WaitingCount == 0);
            Check(events2.Select(x => x.Status).SequenceEqual(new[] { "Waiting", "Cancelled" }), "Cancellation event refreshes other room correctly");
            Check(events1.All(x => x.RoomId == r1) && events1.Select(x => x.EventId).Distinct().Count() == events1.Count,
                "Events carry correct room IDs and unique event IDs");
            var snapshot = await http.GetFromJsonAsync<DisplaySnapshot>("api/display/snapshot");
            Check(!JsonSerializer.Serialize(snapshot).Contains("patient", StringComparison.OrdinalIgnoreCase), "DID snapshot excludes patient identity fields");
            Check(events1.First().VisitId == visit.Visit.Id && events1.Last().VisitVersion == 4, "Events reference committed visits and versions");

            await StopServer();
            await Until(() => !vm.IsFresh && !first.IsConnected);
            Check(!vm.IsRealtime, "Server shutdown marks display stale and realtime disconnected");
            await StartServer();
            var missed = await Register(3, r1, Guid.NewGuid());
            await Change(missed.Visit.Id, "call", 1);
            // Events occurred before rejoining; the next subscribe + snapshot must restore them.
            vm.RequestRefresh();
            await Until(() => vm.IsRealtime && vm.IsFresh && vm.Rooms.Single(x => x.RoomId == r1).Current?.VisitId == missed.Visit.Id);
            Check(vm.Rooms.Single(x => x.RoomId == r1).Current?.Status == "Called", "Reconnect resubscribes and restores changes missed while offline");
            var next = await Register(4, r1, Guid.NewGuid());
            await Until(() => vm.Rooms.Single(x => x.RoomId == r1).Waiting.Any(x => x.VisitId == next.Visit.Id));
            Check(true, "Events continue reaching DID after server restart");
            Console.WriteLine("ALL STEP 7 INTEGRATION TESTS PASSED");
        }
        finally { await StopServer(); }
    }
}
