using MediWay.Contracts;
using MediWay.Api;
using Npgsql;
using StackExchange.Redis;
using System.IO;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
var postgresConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres 환경 변수를 설정하세요.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(postgresConnection));
var redisOptions = ConfigurationOptions.Parse(
    builder.Configuration.GetConnectionString("Redis") ?? "127.0.0.1:56379");
redisOptions.AbortOnConnectFail = false;
redisOptions.ConnectTimeout = 1000;
redisOptions.AsyncTimeout = 1000;
redisOptions.BacklogPolicy = BacklogPolicy.FailFast;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<QueueEvents>();
builder.Services.AddSingleton<NavigationService>();
builder.Services.AddSingleton<MobileGuideStore>();
builder.Services.AddScoped<MediWay.Application.Reception.IVisitTransitionStore, MediWay.Infrastructure.Reception.PostgresVisitTransitionStore>();
builder.Services.AddScoped<MediWay.Application.Reception.IVisitNotifications, VisitNotifications>();
builder.Services.AddScoped<MediWay.Application.Reception.TransitionVisitService>();
builder.Services.AddScoped<MediWay.Application.Reception.IRegistrationStore, MediWay.Infrastructure.Reception.PostgresRegistrationStore>();
builder.Services.AddScoped<MediWay.Application.Reception.RegisterVisitService>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mobile") || context.Request.Path.StartsWithSegments("/api/mobile-guide"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (NavigationFailure ex)
    {
        context.Response.StatusCode = ex.Status;
        await context.Response.WriteAsJsonAsync(ex.Error);
    }
    catch (MobileGuideUnavailable)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new ApiError("GUIDE_STORE_UNAVAILABLE", "모바일 안내에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요."));
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
    {
        app.Logger.LogWarning("Database request failed: {ExceptionType}", ex.GetType().Name);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new ApiError("DATABASE_UNAVAILABLE", "저장소 처리에 실패했습니다. 같은 요청 ID로 재시도하세요."));
    }
});
app.MapVisitEndpoints();
app.MapQueueEndpoints();
app.MapQueueOrderEndpoints();
app.MapStaffEndpoints();
app.MapDisplayEndpoints();
app.MapNavigationEndpoints();
app.MapMobileGuideEndpoints();
app.MapHub<QueueHub>("/hubs/queue");

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy", application = "MEDI-WAY" }));
app.MapGet("/health/ready", async (NpgsqlDataSource postgres, IConnectionMultiplexer redis,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var pgHealthy = false;
    var redisHealthy = false;
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        await using var command = postgres.CreateCommand("SELECT 1");
        pgHealthy = (int?)await command.ExecuteScalarAsync(timeout.Token) == 1;
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        logger.LogWarning("PostgreSQL readiness failed: {ExceptionType}", ex.GetType().Name);
    }
    try
    {
        await redis.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        redisHealthy = true;
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        logger.LogWarning("Redis readiness failed: {ExceptionType}", ex.GetType().Name);
    }
    var response = new HealthResponse(pgHealthy ? (redisHealthy ? "Healthy" : "Degraded") : "Unhealthy",
        new(pgHealthy ? "Up" : "Down"), new(redisHealthy ? "Up" : "Down"));
    return Results.Json(response, statusCode: pgHealthy ? 200 : 503);
});

app.Run();
