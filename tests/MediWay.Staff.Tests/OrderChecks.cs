using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MediWay.Contracts;
using MediWay.Staff.Services;
using MediWay.Staff.ViewModels;

internal static class OrderChecks
{
    public static async Task RunAsync()
    {
        var api = new OrderApi(); using var vm = new StaffViewModel(api); await vm.RefreshCommand.ExecuteAsync();
        Check(!vm.MoveUpCommand.CanExecute(null) && vm.MoveDownCommand.CanExecute(null) && !vm.PriorityCommand.CanExecute(null), "First waiting row exposes only valid movement actions");
        var original = vm.Waiting.Select(x => x.QueueNumber).ToArray();
        await vm.MoveDownCommand.ExecuteAsync();
        Check(vm.Waiting.Select(x => x.QueueNumber).SequenceEqual([2, 1, 3]) && vm.SelectedWaiting!.QueueNumber == 1, "Move down persists order and keeps selected visit");
        vm.SelectedWaiting = vm.Waiting.Last(); await vm.PriorityCommand.ExecuteAsync();
        Check(vm.Waiting.Select(x => x.QueueNumber).SequenceEqual([3, 2, 1]) && vm.Waiting.Select(x => x.QueueNumber).Order().SequenceEqual(original), "Emergency action moves selected visit to front without renumbering");
        await vm.MoveBeforeAsync(vm.Waiting.Last().VisitId, vm.Waiting.First().VisitId);
        Check(vm.Waiting.Select(x => x.QueueNumber).SequenceEqual([1, 3, 2]), "Drag operation inserts source immediately before target");
        var count = api.Requests.Count; await vm.MoveBeforeAsync(Guid.NewGuid(), vm.Waiting[0].VisitId); await vm.MoveBeforeAsync(vm.Waiting[0].VisitId, vm.Waiting[0].VisitId);
        Check(api.Requests.Count == count, "Invalid or identical drag target never sends request");
        api.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SelectedWaiting = vm.Waiting.Last(); var move = vm.PriorityCommand.ExecuteAsync();
        await vm.MoveUpCommand.ExecuteAsync(); await vm.MoveBeforeAsync(vm.Waiting[0].VisitId, vm.Waiting[1].VisitId);
        Check(vm.IsBusy && !vm.CanNavigate && api.Requests.Count == count + 1, "Pending reorder blocks timer refresh, selection changes and second drag");
        api.Gate.SetException(new HttpRequestException()); await move;
        var request = api.Requests.Last(); var applied = api.Applied;
        Check(vm.NeedsRetry && !vm.RefreshCommand.CanExecute(null) && !vm.CallCommand.CanExecute(null), "Unknown reorder result freezes new operations");
        api.Gate = null; await vm.RetryCommand.ExecuteAsync();
        Check(!vm.NeedsRetry && api.Requests.Last() == request && api.Applied == applied && vm.Waiting[0].QueueNumber == 2, "Retry preserves exact request ID, full snapshot and desired order without reapplying");
        api.Failure = new StaffApiFailure(HttpStatusCode.Conflict, "목록이 변경되었습니다."); vm.SelectedWaiting = vm.Waiting.Last(); await vm.PriorityCommand.ExecuteAsync();
        Check(!vm.NeedsRetry && vm.IsConnected && vm.Message.Contains("변경"), "Order conflict reloads queue and permits an informed new action");
        api.Failure = null; api.ReadFailure = true; await vm.RefreshCommand.ExecuteAsync();
        Check(!vm.PriorityCommand.CanExecute(null) && !vm.MoveUpCommand.CanExecute(null), "Stale queue disables priority and movement");
        vm.Dispose(); await vm.MoveBeforeAsync(vm.Waiting[0].VisitId, vm.Waiting[1].VisitId);
        Check(!vm.PriorityCommand.CanExecute(null), "Closed staff app rejects order operations");

        QueueOrderRequest? sent = null; string? path = null;
        using var http = new HttpClient(new OrderTransport(async (message, ct) => { path = message.RequestUri!.AbsolutePath; sent = await message.Content!.ReadFromJsonAsync<QueueOrderRequest>(ct); return new(HttpStatusCode.NoContent); })) { BaseAddress = new Uri("http://localhost/") };
        await new StaffApiClient(http).ReorderAsync(api.Room.Id, request, default);
        Check(path == $"/api/rooms/{api.Room.Id}/queue/order" && sent?.RequestId == request.RequestId && sent.OrderedVisitIds.SequenceEqual(request.OrderedVisitIds), "HTTP adapter serializes request identity and order and accepts 204");
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    private sealed class OrderApi : IStaffApiClient
    {
        public RoomResponse Room = new(Guid.NewGuid(), Guid.NewGuid(), "내과", "IM01", "내과 1진료실");
        private QueueItem[] items = Enumerable.Range(1, 3).Select(n => new QueueItem(Guid.NewGuid(), n, "Waiting", 1, DateTime.UtcNow)).ToArray();
        public List<QueueOrderRequest> Requests = []; private HashSet<Guid> seen = [];
        public TaskCompletionSource? Gate; public Exception? Failure; public bool ReadFailure; public int Applied;
        public Task<IReadOnlyList<RoomResponse>> RoomsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RoomResponse>>([Room]);
        public Task<QueueResponse> QueueAsync(Guid room, CancellationToken ct) => ReadFailure ? Task.FromException<QueueResponse>(new HttpRequestException()) : Task.FromResult(new QueueResponse(room, DateOnly.FromDateTime(DateTime.Today), items));
        public Task<RoomCurrentResponse> CurrentAsync(Guid room, CancellationToken ct) => Task.FromResult(new RoomCurrentResponse(room, null));
        public Task<VisitResponse> ChangeAsync(Guid visit, string action, int version, CancellationToken ct) => throw new NotSupportedException();
        public async Task ReorderAsync(Guid room, QueueOrderRequest request, CancellationToken ct)
        {
            Requests.Add(request); if (Failure is not null) throw Failure;
            if (seen.Add(request.RequestId)) { Applied++; items = request.OrderedVisitIds.Select(id => items.Single(x => x.VisitId == id) with { Version = items.Single(x => x.VisitId == id).Version + 1 }).ToArray(); }
            if (Gate is not null) await Gate.Task;
        }
    }
    private sealed class OrderTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
}
