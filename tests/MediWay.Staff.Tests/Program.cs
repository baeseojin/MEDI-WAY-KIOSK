using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MediWay.Contracts;
using MediWay.Staff.Services;
using MediWay.Staff.ViewModels;

static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
var api = new FakeStaff();
using var vm = new StaffViewModel(api);
await vm.RefreshCommand.ExecuteAsync();
Check(vm.IsConnected && vm.Waiting.Count == 1 && vm.SelectedWaiting is not null && vm.Current is null, "Initial refresh and waiting selection");
Check(vm.CallCommand.CanExecute(null) && !vm.StartCommand.CanExecute(null) && !vm.CompleteCommand.CanExecute(null), "Only valid state actions enabled");
api.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var calling = vm.CallCommand.ExecuteAsync();
await vm.CallCommand.ExecuteAsync();
Check(api.Actions.Count == 1 && !vm.CanNavigate && !vm.RefreshCommand.CanExecute(null), "Concurrent command execution and navigation blocked");
api.Gate.SetException(new TaskCanceledException()); await calling;
Check(vm.NeedsRetry && !vm.RefreshCommand.CanExecute(null) && !vm.CallCommand.CanExecute(null), "Uncertain result freezes new actions and preserves original request");
api.Gate = null; await vm.RetryCommand.ExecuteAsync();
Check(api.Actions.Count == 2 && api.Actions[0] == api.Actions[1] && !vm.NeedsRetry && vm.Current?.Status == "Called", "Retry uses same visit, action and expected version");
Check(vm.StartCommand.CanExecute(null) && !vm.CompleteCommand.CanExecute(null), "Called state permits start only");
await vm.StartCommand.ExecuteAsync(); Check(vm.Current?.Status == "InService" && vm.CompleteCommand.CanExecute(null) && !vm.CancelCurrentCommand.CanExecute(null), "InService permits completion and blocks cancellation");
await vm.CompleteCommand.ExecuteAsync(); Check(vm.Current is null && vm.IsConnected, "Completed visit leaves current panel");
api.AddWaiting(); await vm.RefreshCommand.ExecuteAsync();
await vm.CancelWaitingCommand.ExecuteAsync();
Check(vm.NeedsCancelConfirmation && !vm.CanNavigate && api.Actions.Count == 4, "Cancellation requires second confirmation and freezes selection");
await vm.DismissCancelCommand.ExecuteAsync(); Check(!vm.NeedsCancelConfirmation && vm.CanNavigate, "Cancellation confirmation can be dismissed without server call");
await vm.CancelWaitingCommand.ExecuteAsync(); await vm.ConfirmCancelCommand.ExecuteAsync();
Check(vm.Waiting.Count == 0 && !vm.NeedsCancelConfirmation && api.Actions.Last().Action == "cancel", "Confirmed cancellation refreshes queue");
api.AddWaiting(); await vm.RefreshCommand.ExecuteAsync();
api.Failure = new StaffApiFailure(HttpStatusCode.Conflict, "다른 화면에서 변경되었습니다.");
await vm.CallCommand.ExecuteAsync(); Check(!vm.NeedsRetry && vm.IsConnected && vm.Message.Contains("다른 화면"), "Definitive conflict refreshes state and reports conflict");
api.Failure = null; api.ReadFailure = new HttpRequestException(); await vm.RefreshCommand.ExecuteAsync();
Check(!vm.IsConnected && !vm.CallCommand.CanExecute(null) && vm.Waiting.Count == 1, "Read failure retains previous display but disables state changes");
api.ReadFailure = null; await vm.RefreshCommand.ExecuteAsync(); Check(vm.IsConnected && vm.CallCommand.CanExecute(null), "Manual refresh recovers after network failure");
vm.SelectedRoom = api.Rooms[1]; Check(!vm.IsConnected && vm.Current is null && vm.Waiting.Count == 0 && !vm.CallCommand.CanExecute(null), "Room switch clears old snapshot before refresh");
await vm.RefreshCommand.ExecuteAsync(); api.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var pending = vm.CallCommand.ExecuteAsync(); vm.Dispose(); api.Gate.SetCanceled(); await pending;
Check(vm.Current is null, "Closed viewmodel ignores late request result");

TransitionRequest? sent = null; string? path = null;
using var http = new HttpClient(new Stub(async (req, ct) => { path = req.RequestUri!.AbsolutePath; sent = await req.Content!.ReadFromJsonAsync<TransitionRequest>(ct); return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(api.Visit with { Status = "Called", Version = 2 }) }; })) { BaseAddress = new Uri("http://localhost/") };
var client = new StaffApiClient(http); await client.ChangeAsync(api.Visit.Id, "call", 1, default);
Check(path == $"/api/visits/{api.Visit.Id}/call" && sent?.ExpectedVersion == 1, "HTTP adapter sends action endpoint and expectedVersion");
await OrderChecks.RunAsync();
Console.WriteLine("ALL STAFF TESTS PASSED");

sealed class FakeStaff : IStaffApiClient
{
    public Task ReorderAsync(Guid roomId, QueueOrderRequest request, CancellationToken ct) => Task.CompletedTask;
    public RoomResponse[] Rooms = [new(Guid.NewGuid(), Guid.NewGuid(), "내과", "IM01", "내과 1진료실"), new(Guid.NewGuid(), Guid.NewGuid(), "내과", "IM02", "내과 2진료실")];
    public VisitResponse Visit = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), 1, "Waiting", 1, DateTime.UtcNow);
    public List<StaffAction> Actions = [];
    public TaskCompletionSource<VisitResponse>? Gate;
    public Exception? Failure; public Exception? ReadFailure;
    public void AddWaiting() => Visit = Visit with { Id = Guid.NewGuid(), Status = "Waiting", Version = 1 };
    public Task<IReadOnlyList<RoomResponse>> RoomsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RoomResponse>>(Rooms);
    public Task<QueueResponse> QueueAsync(Guid room, CancellationToken ct) => ReadFailure is not null ? Task.FromException<QueueResponse>(ReadFailure) : Task.FromResult(new QueueResponse(room, Visit.ServiceDate, Visit.Status == "Waiting" ? [new(Visit.Id, Visit.QueueNumber, Visit.Status, Visit.Version, Visit.CreatedAt)] : []));
    public Task<RoomCurrentResponse> CurrentAsync(Guid room, CancellationToken ct) => Task.FromResult(new RoomCurrentResponse(room, Visit.Status is "Called" or "InService" ? Visit : null));
    public async Task<VisitResponse> ChangeAsync(Guid id, string action, int version, CancellationToken ct)
    {
        Actions.Add(new(id, action, version));
        if (Failure is not null) throw Failure;
        if (Gate is not null) return await Gate.Task;
        Visit = Visit with { Status = action switch { "call" => "Called", "start" => "InService", "complete" => "Completed", _ => "Cancelled" }, Version = version + 1 };
        return Visit;
    }
}
sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
