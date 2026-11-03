using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using MediWay.Contracts;

namespace MediWay.Kiosk.Services;

public sealed class ServerConnectionMonitor(HttpClient http, Action<Action> dispatch, TimeSpan? interval = null) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private volatile bool disposed;
    public string StatusText { get; private set; } = "서버 연결 확인 중";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Start() { if (!disposed && worker is null) worker = Task.Run(RunAsync); }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                string status;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var response = await http.GetAsync("health/ready", timeout.Token);
                    var health = await response.Content.ReadFromJsonAsync<HealthResponse>(timeout.Token);
                    status = response.IsSuccessStatusCode && health?.PostgreSql.Status == "Up"
                        ? health.Redis.Status == "Up" ? "서버 연결됨" : "서버 연결됨 · 모바일 QR 일시 제한"
                        : "서버 저장소 연결 확인 필요";
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception) { status = "서버 연결 안 됨 · 연결을 확인해 주세요"; }
                dispatch(() => { if (!disposed) { StatusText = status; PropertyChanged?.Invoke(this, new(nameof(StatusText))); } });
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        if (worker is not null) await worker;
        lifetime.Dispose();
    }
}
