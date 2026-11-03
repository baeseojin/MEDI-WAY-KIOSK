using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using MediWay.Contracts;
using MediWay.Kiosk.Services;
using MediWay.Kiosk.ViewModels;

internal static class OfflineIntegration
{
    public static async Task RunAsync(string root, string address)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mediway-offline-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Process? server = null;
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(3) };
        var real = new KioskApiClient(http);
        async Task Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, http.BaseAddress!.Port); try { listener.Start(); } finally { listener.Stop(); }
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.Combine(root, "src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll")); info.ArgumentList.Add("--urls"); info.ArgumentList.Add(address);
            server = Process.Start(info)!; server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { }; server.BeginOutputReadLine(); server.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                if (server.HasExited) throw new Exception("Test API exited");
                try { using var response = await http.GetAsync("health/live", timeout.Token); if (response.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!timeout.IsCancellationRequested) { }
                await Task.Delay(100, timeout.Token);
            }
        }
        async Task Stop()
        {
            if (server is not null) { if (!server.HasExited) { server.Kill(); await server.WaitForExitAsync(); } server.Dispose(); server = null; }
        }
        KioskViewModel Create(IKioskApiClient api) => new(api, ticketWriter: new WaitingTicketWriter(Path.Combine(folder, "tickets")), offlineStore: new OfflineRegistrationStore(Path.Combine(folder, "outbox"), address));
        try
        {
            await Start();
            using (var first = Create(real))
            {
                await first.InitializeCommand.ExecuteAsync(); first.PatientNumber = "0001"; await first.LookupCommand.ExecuteAsync();
                Check(first.IsConfirm, "Real server patient lookup reaches confirmation");
                await Stop(); await first.RegisterCommand.ExecuteAsync();
                Check(first.IsConfirm && first.Result is null && first.TicketNumber == "" && File.Exists(Path.Combine(folder, "outbox/pending-registration.json")), "Stopped API leaves request on disk without issuing number");
                Check(!Directory.Exists(Path.Combine(folder, "tickets")), "Offline unconfirmed request never writes a waiting ticket");
            }
            await Start();
            using (var restored = Create(real))
            {
                await restored.InitializeCommand.ExecuteAsync(); await restored.ReconcileCommand.ExecuteAsync();
                Check(restored.IsResult && restored.Result!.Visit.QueueNumber == 1, "New kiosk instance reconciles persisted request after API restart");
                Check(File.Exists(restored.TicketFilePath) && !File.Exists(Path.Combine(folder, "outbox/pending-registration.json")), "Confirmed recovery writes ticket and clears pending file");
            }
            var lost = new LostResponse(real);
            using (var first = Create(lost))
            {
                await first.InitializeCommand.ExecuteAsync(); first.PatientNumber = "0002"; await first.LookupCommand.ExecuteAsync(); await first.RegisterCommand.ExecuteAsync();
                Check(lost.Committed is not null && first.Result is null && first.LocalStatus.Contains("1건"), "Dropped response after real DB commit remains unresolved locally");
            }
            using (var restored = Create(real))
            {
                await restored.InitializeCommand.ExecuteAsync(); await restored.ReconcileCommand.ExecuteAsync();
                Check(restored.IsResult && restored.Result!.IsExisting && restored.Result.Visit.Id == lost.Committed!.Visit.Id, "Restart recovery returns original committed visit instead of duplicating it");
            }
            var room = Guid.Parse("20000000-0000-0000-0000-000000000001");
            var queue = (await http.GetFromJsonAsync<QueueResponse>($"api/rooms/{room}/queue?refresh=true"))!;
            Check(queue.Items.Select(x => x.QueueNumber).SequenceEqual([1, 2]), "Both recovery cases leave exactly two unique queue numbers");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")).DateTime);
            var request = new CreateVisitRequest(Guid.Parse("30000000-0000-0000-0000-000000000003"), room, Guid.NewGuid(), today.AddDays(-1));
            using (var old = await http.PostAsJsonAsync("api/visits", request))
                Check(old.StatusCode == HttpStatusCode.Conflict && (await old.Content.ReadFromJsonAsync<ApiError>())!.Code == "SERVICE_DATE_CHANGED", "Unsent old-day request cannot silently register for today");
            request = request with { ExpectedServiceDate = today, RequestId = Guid.NewGuid() };
            using (var created = await http.PostAsJsonAsync("api/visits", request)) Check(created.StatusCode == HttpStatusCode.Created, "Date-bound request registers normally for current hospital day");
            using (var replay = await http.PostAsJsonAsync("api/visits", request)) Check(replay.StatusCode == HttpStatusCode.OK, "Date-bound request preserves existing idempotent replay");
            using (var changed = await http.PostAsJsonAsync("api/visits", request with { ExpectedServiceDate = today.AddDays(-1) }))
                Check(changed.StatusCode == HttpStatusCode.Conflict && (await changed.Content.ReadFromJsonAsync<ApiError>())!.Code == "REQUEST_KEY_CONFLICT", "Same request ID cannot change expected service date");
            Console.WriteLine("ALL STEP 15 OFFLINE INTEGRATION TESTS PASSED");
        }
        finally { await Stop(); Directory.Delete(folder, recursive: true); }
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    private sealed class LostResponse(IKioskApiClient inner) : IKioskApiClient
    {
        public CreateVisitResponse? Committed;
        public async Task<CreateVisitResponse> RegisterAsync(CreateVisitRequest request, CancellationToken ct)
        { Committed = await inner.RegisterAsync(request, ct); throw new HttpRequestException("Synthetic lost response after commit"); }
        public Task<PatientResponse> FindPatientAsync(string number, CancellationToken ct) => inner.FindPatientAsync(number, ct);
        public Task<IReadOnlyList<RoomResponse>> GetRoomsAsync(CancellationToken ct) => inner.GetRoomsAsync(ct);
        public Task<NavigationRoute> RouteAsync(Guid room, CancellationToken ct) => inner.RouteAsync(room, ct);
        public Task<MobileGuideTicket> CreateMobileGuideAsync(Guid visit, CancellationToken ct) => inner.CreateMobileGuideAsync(visit, ct);
    }
}
