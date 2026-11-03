using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MediWay.Contracts;
using MediWay.Kiosk.Services;
using MediWay.Kiosk.ViewModels;

if(args.FirstOrDefault()=="--offline-integration")
{
    await OfflineIntegration.RunAsync(args[1],args[2]);
    return;
}

static void Check(bool value, string message)
{
    if (!value) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

var time = DateTimeOffset.UtcNow;
var api = new FakeApi();
using var vm = new KioskViewModel(api, () => time);
vm.PatientNumber = "0001";
await vm.LookupCommand.ExecuteAsync();
Check(vm.IsConfirm && api.LastNumber == "P0001" && vm.MaskedName == "김*민", "Patient lookup, number normalization, masked display");
Check(vm.SelectedRoom == api.Rooms[0], "Default room selection");
api.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var inFlight = vm.RegisterCommand.ExecuteAsync();
await vm.RegisterCommand.ExecuteAsync();
Check(api.Requests.Count == 1 && !vm.HomeCommand.CanExecute(null) && !vm.CanSelectRoom, "Repeated touches cannot register twice or change room during request");
time = time.AddMinutes(2); vm.Tick();
Check(vm.IsConfirm && vm.IsBusy, "No automatic reset during pending network call");
api.Gate.SetException(new TaskCanceledException());
await inFlight;
Check(vm.IsConfirm && !vm.CanSelectRoom && !vm.HomeCommand.CanExecute(null), "Unknown result keeps request and locks navigation");
var originalId = api.Requests[0].RequestId;
time = time.AddMinutes(2); vm.Tick();
Check(vm.IsConfirm, "Unknown result does not expire into a new session");
api.Gate = null;
api.Existing = true;
await vm.RegisterCommand.ExecuteAsync();
Check(api.Requests.Count == 2 && api.Requests[1].RequestId == originalId && vm.IsResult && vm.Result!.IsExisting, "Retry preserves request ID and shows existing ticket");
Check(vm.TicketNumber == "007" && vm.ResultRoom == "내과 1진료실", "Ticket and original room displayed");
time = time.AddSeconds(19); vm.Tick(); Check(vm.IsResult, "Result remains visible before 20 seconds");
time = time.AddSeconds(1); vm.Tick();
Check(vm.IsInput && vm.PatientNumber == "" && vm.MaskedName == "" && vm.Result is null && vm.Rooms.Count == 0, "20-second return clears patient and ticket data");
vm.PatientNumber = "P0002";
await vm.LookupCommand.ExecuteAsync();
time = time.AddSeconds(59); vm.Tick(); Check(vm.IsConfirm, "Confirmation remains until idle limit");
time = time.AddSeconds(1); vm.Tick(); Check(vm.IsInput && vm.PatientNumber == "", "60-second idle return clears confirmation");
api.LookupFailure = new ApiFailure(HttpStatusCode.NotFound, "등록된 환자번호가 없습니다.");
vm.PatientNumber = "9999"; await vm.LookupCommand.ExecuteAsync();
Check(vm.IsInput && vm.Message.Contains("없습니다") && vm.PatientNumber == "9999", "Unknown patient is shown without leaving input");
api.LookupFailure = new HttpRequestException(); await vm.LookupCommand.ExecuteAsync();
Check(vm.IsInput && vm.Message.Contains("연결"), "Connection failure remains retryable");
api.LookupFailure = null; await vm.LookupCommand.ExecuteAsync();
api.RegisterFailure = new ApiFailure(HttpStatusCode.NotFound, "진료실이 없습니다.");
await vm.RegisterCommand.ExecuteAsync();
Check(vm.HomeCommand.CanExecute(null) && vm.CanSelectRoom, "Definitive API rejection unlocks navigation");
api.RegisterFailure = null; vm.SelectedRoom = api.Rooms[1];
await vm.RegisterCommand.ExecuteAsync();
var registrationCount = api.Requests.Count;
var receiptId = vm.Result!.Visit.Id;
await vm.GuideCommand.ExecuteAsync();
Check(vm.IsGuide && vm.Route is not null && api.LastRouteRoom == api.Rooms[0].Id, "Guide targets server-assigned room instead of selected duplicate room");
Check(api.Requests.Count == registrationCount, "Opening guide does not create another registration");
vm.ReceiptCommand.Execute(null);
Check(vm.IsResult && vm.Route is null && vm.Result.Visit.Id == receiptId, "Returning from map preserves receipt and clears route");
api.RouteFailure = new ApiFailure(HttpStatusCode.Conflict, "이동 가능한 경로가 없습니다.");
await vm.GuideCommand.ExecuteAsync();
Check(vm.IsResult && vm.Result.Visit.Id == receiptId && vm.Route is null && vm.Message.Contains("접수는 유지"), "Route failure preserves registration and does not display stale map");
api.RouteFailure = null; await vm.GuideCommand.ExecuteAsync();
time = time.AddSeconds(59); vm.Tick(); Check(vm.IsGuide, "Guide remains visible before idle deadline");
time = time.AddSeconds(1); vm.Tick(); Check(vm.IsInput && vm.Route is null && vm.Result is null, "Guide idle timeout clears map and patient session");
vm.PatientNumber="0001";await vm.LookupCommand.ExecuteAsync();await vm.RegisterCommand.ExecuteAsync();
var qrReceipt=vm.Result!.Visit.Id;api.MobileFailure=new HttpRequestException();
await vm.MobileCommand.ExecuteAsync();
Check(vm.IsResult&&vm.Result.Visit.Id==qrReceipt&&vm.MobileGuide is null,"QR failure preserves registration and receipt");
api.MobileFailure=null;api.MobileExpiry=time.AddHours(12);await vm.MobileCommand.ExecuteAsync();
Check(vm.IsQr&&vm.MobileGuide is not null&&api.LastMobileVisit==qrReceipt,"QR issuance targets the actual saved visit");
var mobileCalls=api.MobileCalls;await vm.MobileCommand.ExecuteAsync();
Check(api.MobileCalls==mobileCalls,"Reopening valid QR does not issue another token or extend expiry");
vm.ReceiptCommand.Execute(null);Check(vm.IsResult&&vm.Result.Visit.Id==qrReceipt,"QR return keeps receipt");
await vm.MobileCommand.ExecuteAsync();Check(vm.IsQr&&api.MobileCalls==mobileCalls,"QR stays reusable within the same kiosk session");
time=time.AddSeconds(59);vm.Tick();Check(vm.IsQr,"QR screen stays until idle deadline");
time=time.AddSeconds(1);vm.Tick();Check(vm.IsInput&&vm.MobileGuide is null&&vm.Result is null,"QR idle reset clears token image and patient session");
vm.HomeCommand.Execute(null); vm.PatientNumber = "0001"; await vm.LookupCommand.ExecuteAsync();
api.RegisterFailure = null; api.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var closing = vm.RegisterCommand.ExecuteAsync();
vm.Dispose(); api.Gate.SetCanceled(); await closing;
Check(!vm.IsResult, "Closing cancels operation and prevents late result navigation");

// Exercise the production HTTP adapter with a controllable transport.
CreateVisitRequest? sent = null;
using var http = new HttpClient(new StubHandler(async (request, ct) =>
{
    sent = await request.Content!.ReadFromJsonAsync<CreateVisitRequest>(ct);
    return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(api.Response()) };
}))
{ BaseAddress = new Uri("http://localhost/") };
var adapter = new KioskApiClient(http);
var payload = new CreateVisitRequest(api.Patient.Id, api.Rooms[0].Id, Guid.NewGuid());
var result = await adapter.RegisterAsync(payload, default);
Check(sent == payload && result.Visit.QueueNumber == 7, "HTTP adapter serializes request IDs and reads shared response DTO");
AccessibilityChecks.Run();
await TicketChecks.RunAsync();
await ScannerChecks.RunAsync();
await OfflineChecks.RunAsync();
await ConnectionChecks.RunAsync();
Console.WriteLine("ALL KIOSK TESTS PASSED");

sealed class FakeApi : IKioskApiClient
{
    public Exception? MobileFailure;public Guid? LastMobileVisit;public int MobileCalls;public DateTimeOffset MobileExpiry=DateTimeOffset.UtcNow.AddHours(12);
    public Task<MobileGuideTicket> CreateMobileGuideAsync(Guid visitId,CancellationToken ct)
    {
        MobileCalls++;LastMobileVisit=visitId;
        return MobileFailure is null?Task.FromResult(new MobileGuideTicket("http://localhost/mobile/#t="+new string('a',64),MobileExpiry,"AA==",true)):Task.FromException<MobileGuideTicket>(MobileFailure);
    }
    public Guid? LastRouteRoom;
    public Exception? RouteFailure;
    public Task<NavigationRoute> RouteAsync(Guid roomId, CancellationToken ct)
    {
        LastRouteRoom = roomId;
        if (RouteFailure is not null) return Task.FromException<NavigationRoute>(RouteFailure);
        var first = new MapNode(Guid.NewGuid(), "키오스크", "Kiosk", 10, 10);
        var last = new MapNode(Guid.NewGuid(), "진료실", "Room", 90, 90);
        return Task.FromResult(new NavigationRoute(new(Guid.NewGuid(), "F1", "1층", 100, 100, [first, last], [], []), first.Id, last.Id, Rooms[0].Name, 10, 10, [first, last]));
    }
    public PatientResponse Patient = new(Guid.NewGuid(), "P0001", "김수민");
    public RoomResponse[] Rooms = [new(Guid.NewGuid(), Guid.NewGuid(), "내과", "IM01", "내과 1진료실"), new(Guid.NewGuid(), Guid.NewGuid(), "내과", "IM02", "내과 2진료실")];
    public string? LastNumber;
    public int LookupCalls;
    public TaskCompletionSource<PatientResponse>? LookupGate;
    public Exception? LookupFailure;
    public Exception? RegisterFailure;
    public bool Existing;
    public List<CreateVisitRequest> Requests = [];
    public TaskCompletionSource<CreateVisitResponse>? Gate;
    public Task<PatientResponse> FindPatientAsync(string number, CancellationToken ct)
    {
        LastNumber = number;
        LookupCalls++;
        return LookupFailure is null ? LookupGate?.Task ?? Task.FromResult(Patient) : Task.FromException<PatientResponse>(LookupFailure);
    }
    public Task<IReadOnlyList<RoomResponse>> GetRoomsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RoomResponse>>(Rooms);
    public Task<CreateVisitResponse> RegisterAsync(CreateVisitRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (RegisterFailure is not null) return Task.FromException<CreateVisitResponse>(RegisterFailure);
        return Gate?.Task ?? Task.FromResult(Response());
    }
    public CreateVisitResponse Response() => new(new(Guid.NewGuid(), Patient.Id, Rooms[0].DepartmentId, Rooms[0].Id,
        DateOnly.FromDateTime(DateTime.Today), 7, "Waiting", 1, DateTime.UtcNow), Existing);
}
sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
