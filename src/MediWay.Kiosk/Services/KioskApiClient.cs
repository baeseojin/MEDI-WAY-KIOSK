using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MediWay.Contracts;

namespace MediWay.Kiosk.Services;

public interface IKioskApiClient
{
    Task<PatientResponse> FindPatientAsync(string number, CancellationToken ct);
    Task<IReadOnlyList<RoomResponse>> GetRoomsAsync(CancellationToken ct);
    Task<CreateVisitResponse> RegisterAsync(CreateVisitRequest request, CancellationToken ct);
    Task<NavigationRoute> RouteAsync(Guid roomId, CancellationToken ct);
    Task<MobileGuideTicket> CreateMobileGuideAsync(Guid visitId, CancellationToken ct);
}

public sealed class ApiFailure(HttpStatusCode status, string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
    public bool MayHaveCommitted => (int)status >= 500 || status == HttpStatusCode.RequestTimeout;
}

public sealed class KioskApiClient(HttpClient http, Guid? kioskNodeId = null) : IKioskApiClient
{
    public async Task<MobileGuideTicket> CreateMobileGuideAsync(Guid visitId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/visits/{visitId}/mobile-guide",
            new CreateMobileGuideRequest(kioskNodeId ?? Guid.Parse("61000000-0000-0000-0000-000000000002")), ct);
        return await ReadAsync<MobileGuideTicket>(response, ct);
    }
    public Task<NavigationRoute> RouteAsync(Guid roomId, CancellationToken ct) =>
        GetAsync<NavigationRoute>($"api/navigation/rooms/{roomId}/route?startNodeId={kioskNodeId ?? Guid.Parse("61000000-0000-0000-0000-000000000002")}", ct);
    public Task<PatientResponse> FindPatientAsync(string number, CancellationToken ct) =>
        GetAsync<PatientResponse>($"api/patients/by-number/{Uri.EscapeDataString(number)}", ct);

    public async Task<IReadOnlyList<RoomResponse>> GetRoomsAsync(CancellationToken ct) =>
        await GetAsync<RoomResponse[]>("api/rooms", ct);

    public async Task<CreateVisitResponse> RegisterAsync(CreateVisitRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/visits", request, ct);
        return await ReadAsync<CreateVisitResponse>(response, ct);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            try { error = await response.Content.ReadFromJsonAsync<ApiError>(ct); }
            catch (JsonException) { }
            throw new ApiFailure(response.StatusCode, error?.Message ?? "요청을 처리하지 못했습니다. 잠시 후 다시 시도해 주세요.", error?.Code);
        }
        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new JsonException("Empty API response");
    }
}
