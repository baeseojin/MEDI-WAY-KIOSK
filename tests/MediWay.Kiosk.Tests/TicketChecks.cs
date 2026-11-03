using System.IO;
using System.Text;
using MediWay.Contracts;
using MediWay.Kiosk.Services;
using MediWay.Kiosk.ViewModels;

internal static class TicketChecks
{
    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mediway-ticket-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var api = new FakeApi();
            var writer = new WaitingTicketWriter(Path.Combine(folder, "tickets"));
            var visit = api.Response().Visit with { CreatedAt = new DateTime(2026, 11, 2, 5, 30, 0, DateTimeKind.Utc), ServiceDate = new DateOnly(2026, 11, 2) };
            var path = await writer.SaveAsync(visit, "내과 1진료실\n테스트", default);
            var bytes = await File.ReadAllBytesAsync(path);
            var text = await File.ReadAllTextAsync(path);
            Check(bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) && text.Contains("대기번호: 007") && text.Contains("2026-11-02 14:30:00"), "Ticket is Korean UTF-8 text with server number and Korea time");
            Check(!text.Contains(api.Patient.DisplayName) && !text.Contains(api.Patient.PatientNumber) && !text.Contains(visit.PatientId.ToString()), "Ticket excludes patient name, number and identity");
            Check(text.Contains("진료실: 내과 1진료실 테스트"), "Room text cannot inject additional ticket lines");
            var samplePath = Environment.GetEnvironmentVariable("MEDIWAY_TICKET_SAMPLE_PATH");
            if (!string.IsNullOrWhiteSpace(samplePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(samplePath))!);
                File.Copy(path, samplePath, overwrite: true);
            }
            var again = await writer.SaveAsync(visit, "내과 1진료실\n테스트", default);
            Check(again == path && Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1 && text == await File.ReadAllTextAsync(path), "Repeated saving replaces same visit file without duplicate tickets or temporary files");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await writer.SaveAsync(visit, "수정되면 안 됨", cancelled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(text == await File.ReadAllTextAsync(path) && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Cancelled save preserves previous complete ticket and cleans partial file");

            var slow = new ControlledWriter();
            using var vm = new KioskViewModel(api, ticketWriter: slow);
            vm.PatientNumber = "0001"; await vm.LookupCommand.ExecuteAsync();
            vm.SelectedRoom = api.Rooms[1];
            var registering = vm.RegisterCommand.ExecuteAsync();
            Check(vm.IsResult && vm.IsBusy && !vm.SaveTicketCommand.CanExecute(null) && !vm.HomeCommand.CanExecute(null), "Automatic file save locks repeat actions until write completes");
            slow.Gate.SetException(new IOException("disk full")); await registering;
            var id = vm.Result!.Visit.Id; var requests = api.Requests.Count;
            Check(vm.IsResult && !vm.IsBusy && vm.TicketSaveMessage.Contains("접수는 완료") && vm.SaveTicketCommand.CanExecute(null), "Disk failure preserves committed receipt and offers local retry");
            Check(slow.Room == api.Rooms[0].Name, "File uses server-assigned room instead of last selected room");
            slow.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var saving = vm.SaveTicketCommand.ExecuteAsync();
            await vm.SaveTicketCommand.ExecuteAsync();
            Check(slow.Calls == 2, "Repeated save taps cannot start concurrent writes");
            slow.Gate.SetResult(path); await saving;
            Check(vm.TicketFilePath == path && vm.Result.Visit.Id == id && api.Requests.Count == requests && vm.RemainingSeconds == 60, "Retry saves locally without another registration and extends result viewing time");
            vm.HomeCommand.Execute(null);
            Check(vm.TicketFilePath == "" && vm.TicketSaveMessage == "" && File.Exists(path), "Session reset clears displayed path but retains saved ticket");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }

    private sealed class ControlledWriter : IWaitingTicketWriter
    {
        public TaskCompletionSource<string> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls; public string? Room;
        public Task<string> SaveAsync(VisitResponse visit, string roomName, CancellationToken ct)
        { Calls++; Room = roomName; return Gate.Task; }
    }
}
