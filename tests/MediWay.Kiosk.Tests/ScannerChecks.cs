using System.Net;
using MediWay.Kiosk.Services;
using MediWay.Kiosk.ViewModels;

internal static class ScannerChecks
{
    public static async Task RunAsync()
    {
        foreach (var raw in new[] { "P0001", "0001", " p0001\r\n", "MEDIWAY:P0001", "mediway:0001" })
            Check(PatientBarcode.TryParse(raw, out var number) && number == "P0001", "Barcode normalizes: " + raw.Trim());
        foreach (var raw in new[] { "", "P", "https://hospital/mobile/#t=abc", "P０００１", "P0001\nP0002", "MEDIWAY:../0001", new string('1', 33) })
            Check(!PatientBarcode.TryParse(raw, out _), "Invalid barcode rejected without navigation");
        var time = DateTimeOffset.UtcNow;
        var api = new FakeApi();
        using var vm = new KioskViewModel(api, () => time);
        vm.BarcodeInput = "https://example.invalid"; await vm.ScanCommand.ExecuteAsync();
        Check(api.LookupCalls == 0 && vm.IsInput && vm.BarcodeInput == "" && vm.Message.Contains("형식"), "Invalid scan never calls API and clears raw payload");
        api.LookupGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BarcodeInput = "MEDIWAY:P0001";
        var pending = vm.ScanCommand.ExecuteAsync();
        await vm.ScanCommand.ExecuteAsync(); await vm.DemoScanCommand.ExecuteAsync(); await vm.LookupCommand.ExecuteAsync();
        Check(api.LookupCalls == 1 && vm.IsBusy && !vm.HomeCommand.CanExecute(null), "Scanner, demo and manual lookup share busy lock");
        api.LookupGate.SetResult(api.Patient); await pending; api.LookupGate = null;
        Check(vm.IsConfirm && api.LastNumber == "P0001" && api.Requests.Count == 0 && vm.BarcodeInput == "", "Scan reaches masked confirmation without automatic registration");
        await vm.ScanCommand.ExecuteAsync();
        Check(api.LookupCalls == 1, "Scanning cannot replace confirmed patient");
        vm.HomeCommand.Execute(null); await vm.DemoScanCommand.ExecuteAsync();
        Check(vm.IsInput && api.LookupCalls == 1 && vm.Message.Contains("연속"), "Rapid duplicate scan after home is throttled");
        time = time.AddSeconds(2); await vm.DemoScanCommand.ExecuteAsync();
        Check(vm.IsConfirm && api.LookupCalls == 2 && vm.PatientNumber == "P0001", "Demo scanner works after two-second cooldown");
        vm.HomeCommand.Execute(null); time = time.AddSeconds(2);
        api.LookupFailure = new ApiFailure(HttpStatusCode.NotFound, "등록된 환자번호가 없습니다.");
        vm.BarcodeInput = "P9999"; await vm.ScanCommand.ExecuteAsync();
        Check(vm.IsInput && vm.Message.Contains("없습니다") && vm.BarcodeInput == "", "Unknown scanned patient stays retryable on input screen");
        api.LookupFailure = null; time = time.AddSeconds(2); await vm.DemoScanCommand.ExecuteAsync();
        await vm.RegisterCommand.ExecuteAsync(); var visit = vm.Result!.Visit.Id;
        await vm.DemoScanCommand.ExecuteAsync();
        Check(vm.Result.Visit.Id == visit && api.Requests.Count == 1, "Result screen blocks scanner and preserves saved registration");
        vm.HomeCommand.Execute(null); vm.BarcodeInput = "P0002"; time = time.AddSeconds(60); vm.Tick();
        Check(vm.IsInput && vm.BarcodeInput == "", "Idle reset removes unsubmitted barcode");
        vm.Dispose(); await vm.DemoScanCommand.ExecuteAsync();
        Check(!vm.ScanCommand.CanExecute(null) && !vm.DemoScanCommand.CanExecute(null), "Disposed kiosk cannot accept scanner input");
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}
