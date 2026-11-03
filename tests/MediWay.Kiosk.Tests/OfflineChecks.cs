using System.IO;
using System.Net;
using System.Net.Http;
using MediWay.Kiosk.Services;
using MediWay.Kiosk.ViewModels;

internal static class OfflineChecks
{
    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mediway-offline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var time = DateTimeOffset.UtcNow; var api = new FakeApi();
            var store = new OfflineRegistrationStore(folder, "http://localhost:5080/");
            using (var vm = new KioskViewModel(api, () => time, offlineStore: store))
            {
                Check(!vm.CanEdit && !vm.HomeCommand.CanExecute(null), "Startup blocks new requests until local recovery state is read");
                await vm.InitializeCommand.ExecuteAsync(); vm.PatientNumber = "0001"; await vm.LookupCommand.ExecuteAsync();
                api.RegisterFailure = new HttpRequestException(); await vm.RegisterCommand.ExecuteAsync();
                Check(vm.IsConfirm && vm.Result is null && vm.TicketNumber == "" && vm.LocalStatus.Contains("1건") && !vm.HomeCommand.CanExecute(null), "Network failure keeps durable unconfirmed request without fabricating queue number");
                var payload = await File.ReadAllTextAsync(Path.Combine(folder, "pending-registration.json"));
                Check(!payload.Contains(api.Patient.DisplayName) && !payload.Contains(api.Patient.PatientNumber) && payload.Contains(api.Requests[0].RequestId.ToString()), "Local payload stores identity key but excludes patient name and number");
                await vm.ReconcileCommand.ExecuteAsync(); Check(api.Requests.Count == 1, "Automatic reconciliation respects five-second retry interval");
                time = time.AddSeconds(5); await vm.ReconcileCommand.ExecuteAsync();
                Check(api.Requests.Count == 2 && api.Requests[0] == api.Requests[1], "Automatic retry preserves original request including service date");
            }
            var original = api.Requests[0]; api.RegisterFailure = null;
            using (var restored = new KioskViewModel(api, () => time, offlineStore: new OfflineRegistrationStore(folder, "http://localhost:5080/")))
            {
                await restored.InitializeCommand.ExecuteAsync();
                Check(restored.IsConfirm && restored.MaskedName == "" && !restored.HomeCommand.CanExecute(null), "Recreated kiosk restores request without restoring patient name");
                await restored.ReconcileCommand.ExecuteAsync();
                Check(restored.IsResult && api.Requests.Last() == original && !File.Exists(Path.Combine(folder, "pending-registration.json")), "Recovery confirms same request and removes durable pending file only after response");
            }
            var flaky = new FlakyStore { FailSave = true }; var diskApi = new FakeApi();
            using (var vm = new KioskViewModel(diskApi, offlineStore: flaky))
            {
                await vm.InitializeCommand.ExecuteAsync(); vm.PatientNumber = "0001"; await vm.LookupCommand.ExecuteAsync(); await vm.RegisterCommand.ExecuteAsync();
                Check(diskApi.Requests.Count == 0 && !vm.ReconcileCommand.CanExecute(null) && !vm.HomeCommand.CanExecute(null), "Local write failure prevents any HTTP registration and preserves request for manual retry");
                flaky.FailSave = false; flaky.FailClear = true; await vm.RegisterCommand.ExecuteAsync();
                var saved = flaky.Record!.Request;
                Check(vm.Result is null && vm.IsConfirm && flaky.Record is not null, "Failed local acknowledgement cleanup keeps request unresolved");
                flaky.FailClear = false; await vm.RegisterCommand.ExecuteAsync();
                Check(vm.IsResult && diskApi.Requests.All(r => r == saved), "Cleanup retry reuses original request instead of issuing another identity");
            }
            using (var owner = new OfflineRegistrationStore(folder, "http://localhost:5080/"))
            {
                await owner.LoadAsync(default);
                using var second = new OfflineRegistrationStore(folder, "http://localhost:5080/");
                var blocked = false; try { await second.LoadAsync(default); } catch (IOException) { blocked = true; }
                Check(blocked, "Two kiosk instances cannot own the same recovery directory");
                await owner.SaveAsync(new(original, api.Rooms, time), default);
            }
            using (var wrongServer = new OfflineRegistrationStore(folder, "http://localhost:5999/"))
            {
                var blocked = false; try { await wrongServer.LoadAsync(default); } catch (InvalidDataException) { blocked = true; }
                Check(blocked && File.Exists(Path.Combine(folder, "pending-registration.json")), "Different server binding refuses pending request without deleting it");
            }
            var oldDateApi = new FakeApi { RegisterFailure = new ApiFailure(HttpStatusCode.Conflict, "진료일이 변경되었습니다.") };
            using (var restored = new KioskViewModel(oldDateApi, () => time.AddDays(1), offlineStore: new OfflineRegistrationStore(folder, "http://localhost:5080/")))
            {
                await restored.InitializeCommand.ExecuteAsync(); await restored.ReconcileCommand.ExecuteAsync();
                Check(oldDateApi.Requests.Single() == original && restored.IsInput && restored.Result is null, "Expired-day rejection releases restored request without creating new identity or queue number");
            }
            await File.WriteAllTextAsync(Path.Combine(folder, "pending-registration.json"), "broken json");
            using (var broken = new KioskViewModel(new FakeApi(), offlineStore: new OfflineRegistrationStore(folder, "http://localhost:5080/")))
            {
                await broken.InitializeCommand.ExecuteAsync();
                Check(!broken.CanEdit && broken.NeedsLocalInitialization && await File.ReadAllTextAsync(Path.Combine(folder, "pending-registration.json")) == "broken json", "Corrupt recovery record blocks new registration and is retained for inspection");
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    private sealed class FlakyStore : IOfflineRegistrationStore
    {
        public OfflineRegistration? Record; public bool FailSave, FailClear;
        public Task<OfflineRegistration?> LoadAsync(CancellationToken ct) => Task.FromResult(Record);
        public Task SaveAsync(OfflineRegistration record, CancellationToken ct) { if (FailSave) throw new IOException(); Record = record; return Task.CompletedTask; }
        public Task ClearAsync(Guid id, CancellationToken ct) { if (FailClear) throw new IOException(); Record = null; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
