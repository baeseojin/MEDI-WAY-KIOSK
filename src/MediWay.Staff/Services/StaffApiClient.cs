using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MediWay.Contracts;

namespace MediWay.Staff.Services;

public interface IStaffApiClient
{
    Task<IReadOnlyList<RoomResponse>> RoomsAsync(CancellationToken ct);
    Task<QueueResponse> QueueAsync(Guid roomId, CancellationToken ct);
    Task<RoomCurrentResponse> CurrentAsync(Guid roomId, CancellationToken ct);
    Task<VisitResponse> ChangeAsync(Guid visitId, string action, int version, CancellationToken ct);
    Task ReorderAsync(Guid roomId, QueueOrderRequest request, CancellationToken ct);
}
public sealed class StaffApiFailure(HttpStatusCode status, string message) : Exception(message)
{
    public bool IsUncertain => (int)status >= 500 || status == HttpStatusCode.RequestTimeout;
}
public sealed class StaffApiClient(HttpClient http) : IStaffApiClient
{
    public async Task ReorderAsync(Guid roomId, QueueOrderRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/rooms/{roomId}/queue/order", request, ct);
        if (response.IsSuccessStatusCode) return;
        await Read<object>(response, ct);
    }
    public async Task<IReadOnlyList<RoomResponse>> RoomsAsync(CancellationToken ct) => await Get<RoomResponse[]>("api/rooms", ct);
    public Task<QueueResponse> QueueAsync(Guid roomId, CancellationToken ct) => Get<QueueResponse>($"api/rooms/{roomId}/queue?refresh=true", ct);
    public Task<RoomCurrentResponse> CurrentAsync(Guid roomId, CancellationToken ct) => Get<RoomCurrentResponse>($"api/rooms/{roomId}/current", ct);
    public async Task<VisitResponse> ChangeAsync(Guid visitId, string action, int version, CancellationToken ct)
    {
        if (action is not ("call" or "start" or "complete" or "cancel")) throw new ArgumentException("Unsupported action");
        using var response = await http.PostAsJsonAsync($"api/visits/{visitId}/{action}", new TransitionRequest(version), ct);
        return await Read<VisitResponse>(response, ct);
    }
    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        return await Read<T>(response, ct);
    }
    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            try { error = await response.Content.ReadFromJsonAsync<ApiError>(ct); } catch (JsonException) { }
            throw new StaffApiFailure(response.StatusCode, error?.Message ?? "요청을 처리하지 못했습니다.");
        }
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new JsonException("Empty API response");
    }
}
