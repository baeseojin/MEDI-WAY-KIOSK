using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediWay.Contracts;
using MediWay.Kiosk.Controls;
using Npgsql;
using StackExchange.Redis;
using ZXing;

if (args.Length != 4) throw new ArgumentException("Run scripts/Test-Step9.ps1");
var address = args[0]; var port = args[1]; var container = args[2]; var prefix = args[3];
if (!container.StartsWith("mediway-mobiletest-") || !Guid.TryParseExact(container[19..], "N", out _)) throw new ArgumentException("Expected isolated Redis container");
using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
using var redis = await ConnectionMultiplexer.ConnectAsync($"127.0.0.1:{port}");
var cache = redis.GetDatabase();
await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var start = Guid.Parse("61000000-0000-0000-0000-000000000002");
var room1 = Guid.Parse("20000000-0000-0000-0000-000000000001");
var room2 = Guid.Parse("20000000-0000-0000-0000-000000000002");
static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
async Task<Reply> Request(HttpMethod method, string path, object? body = null, string? token = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = JsonContent.Create(body);
    if (token is not null) request.Headers.Authorization = new("Bearer", token);
    using var response = await http.SendAsync(request);
    var json = await response.Content.ReadAsStringAsync();
    using var document = JsonDocument.Parse(json);
    return new((int)response.StatusCode, document.RootElement.Clone(), response.Headers.CacheControl?.ToString() ?? "");
}
async Task<VisitResponse> Register(int n, Guid room)
{
    var response = await Request(HttpMethod.Post, "api/visits", new CreateVisitRequest(Guid.Parse($"30000000-0000-0000-0000-{n:D12}"), room, Guid.NewGuid()));
    Check(response.Status == 201, $"Create test visit {n}"); return response.Body.Deserialize<CreateVisitResponse>(options)!.Visit;
}
Task<Reply> Issue(Guid visit, Guid? origin = null) => Request(HttpMethod.Post, $"api/visits/{visit}/mobile-guide", new CreateMobileGuideRequest(origin ?? start));
static string Token(MobileGuideTicket ticket) => new Uri(ticket.Url).Fragment[3..];
string Key(string token) => $"{prefix}:mobile-guide:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant()}";
async Task Sql(string text) { await using var command = db.CreateCommand(text); await command.ExecuteNonQueryAsync(); }
async Task Docker(string action)
{
    var info = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(action); if (action == "stop") { info.ArgumentList.Add("--time"); info.ArgumentList.Add("1"); }
    info.ArgumentList.Add(container);
    using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); await output;
    if (process.ExitCode != 0) throw new Exception("Isolated Redis operation failed: " + await error);
}

var visit = await Register(1, room1);
var issued = await Issue(visit.Id);
Check(issued.Status == 201 && issued.Cache.Contains("no-store"), "QR issuance returns 201 and no-store");
var ticket = issued.Body.Deserialize<MobileGuideTicket>(options)!;
var token = Token(ticket); var key = Key(token);
Check(token.Length == 64 && token.All(char.IsAsciiHexDigit) && new Uri(ticket.Url).AbsolutePath == "/mobile/" && ticket.IsLocalOnly, "QR URL contains random fragment token and loopback notice");
using (var stream = new MemoryStream(Convert.FromBase64String(ticket.QrPngBase64)))
{
    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    var rgb = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0); var stride = rgb.PixelWidth * 3; var pixels = new byte[stride * rgb.PixelHeight]; rgb.CopyPixels(pixels, stride, 0);
    var decoded = new BarcodeReaderGeneric().Decode(new RGBLuminanceSource(pixels, rgb.PixelWidth, rgb.PixelHeight, RGBLuminanceSource.BitmapFormat.RGB24));
    Check(decoded?.Text == ticket.Url, "Generated PNG QR decodes to exact mobile URL");
    var image = new QrImageConverter().Convert(ticket.QrPngBase64, typeof(ImageSource), null!, CultureInfo.InvariantCulture) as BitmapImage;
    Check(image?.IsFrozen == true && image.PixelWidth > 100, "WPF QR converter creates a detached frozen image");
}
var stored = (string?)await cache.StringGetAsync(key);
Check(stored is not null && !stored.Contains(token) && !stored.Contains("Patient", StringComparison.OrdinalIgnoreCase), "Redis uses token hash key and stores no raw token or patient fields");
var ttl = await cache.KeyTimeToLiveAsync(key);
Check(ttl?.TotalSeconds is > 43150 and <= 43200 && Math.Abs((ticket.ExpiresAt - DateTimeOffset.UtcNow).TotalHours - 12) < .02, "Session TTL and declared expiry are 12 hours");
var mobile = await Request(HttpMethod.Get, "api/mobile-guide", token: token);
Check(mobile.Status == 200 && mobile.Cache.Contains("no-store"), "Valid token opens non-cached mobile route");
var guide = mobile.Body.Deserialize<MobileGuideResponse>(options)!;
Check(guide.Route.Algorithm == "AStar" && guide.QueueNumber == 1 && guide.Route.DestinationName == "내과 1진료실", "Mobile guide reuses A* and actual assigned room");
Check(!mobile.Body.GetRawText().Contains("patient", StringComparison.OrdinalIgnoreCase), "Mobile response excludes patient identity fields");
await Task.Delay(1100); await Request(HttpMethod.Get, "api/mobile-guide", token: token);
var ttlAfter = await cache.KeyTimeToLiveAsync(key); Check(ttlAfter < ttl, "Reading guide does not extend token TTL");
var tamper = await Request(HttpMethod.Get, $"api/mobile-guide?roomId={room2}", token: token);
Check(tamper.Body.Deserialize<MobileGuideResponse>(options)!.Route.DestinationName == "내과 1진료실", "Query parameters cannot change token destination");
using (var shell = await http.GetAsync(ticket.Url))
{
    var html = await shell.Content.ReadAsStringAsync();
    Check(shell.IsSuccessStatusCode && html.Contains("viewport") && !html.Contains(token) && !html.Contains("61000000"), "Mobile shell is responsive and contains no embedded token or map data");
    Check(shell.Headers.Contains("Content-Security-Policy") && shell.Headers.GetValues("Referrer-Policy").Single() == "no-referrer", "Mobile page sets CSP and no-referrer policy");
}
using (var script = await http.GetAsync("mobile/app.js")) Check(script.IsSuccessStatusCode, "Mobile script is served alongside API");
Check((await Request(HttpMethod.Get, "api/mobile-guide")).Status == 401, "Missing bearer token rejected");
Check((await Request(HttpMethod.Get, "api/mobile-guide", token: "invalid")).Status == 401, "Malformed bearer token rejected");
Check((await Request(HttpMethod.Get, "api/mobile-guide", token: new string('b', 64))).Status == 410, "Unknown token rejected");
await Sql("UPDATE map_links SET is_closed=true WHERE id='62000000-0000-0000-0000-000000000012'");
var detour = await Request(HttpMethod.Get, "api/mobile-guide", token: token);
Check(detour.Body.Deserialize<MobileGuideResponse>(options)!.Route.TotalCost == 64, "Mobile refresh recomputes A* after passage closure");
await Sql("UPDATE map_links SET is_closed=false");
await cache.KeyExpireAsync(key, TimeSpan.FromMilliseconds(200)); await Task.Delay(350);
Check((await Request(HttpMethod.Get, "api/mobile-guide", token: token)).Status == 410, "Redis expiry blocks previously valid token");
var deletedTicket = (await Issue(visit.Id)).Body.Deserialize<MobileGuideTicket>(options)!;
await cache.KeyDeleteAsync(Key(Token(deletedTicket)));
Check((await Request(HttpMethod.Get, "api/mobile-guide", token: Token(deletedTicket))).Status == 410, "Deleted token cannot fall back to DB");
var corrupt = (await Issue(visit.Id)).Body.Deserialize<MobileGuideTicket>(options)!;
await cache.StringSetAsync(Key(Token(corrupt)), "not-json", TimeSpan.FromHours(12));
Check((await Request(HttpMethod.Get, "api/mobile-guide", token: Token(corrupt))).Status == 410, "Corrupt session fails closed");
var endTicket = (await Issue(visit.Id)).Body.Deserialize<MobileGuideTicket>(options)!;
await Request(HttpMethod.Post, $"api/visits/{visit.Id}/call", new TransitionRequest(1));
await Request(HttpMethod.Post, $"api/visits/{visit.Id}/start", new TransitionRequest(2));
await Request(HttpMethod.Post, $"api/visits/{visit.Id}/complete", new TransitionRequest(3));
var ended = await Request(HttpMethod.Get, "api/mobile-guide", token: Token(endTicket));
Check(ended.Status == 410 && ended.Body.GetProperty("code").GetString() == "GUIDE_ENDED" && !await cache.KeyExistsAsync(Key(Token(endTicket))), "Completed visit ends guide and removes session on next lookup");
Check((await Issue(visit.Id)).Status == 409, "Completed visit cannot issue new QR");
var cancelledVisit = await Register(2, room2); var cancelTicket = (await Issue(cancelledVisit.Id)).Body.Deserialize<MobileGuideTicket>(options)!;
await Request(HttpMethod.Post, $"api/visits/{cancelledVisit.Id}/cancel", new TransitionRequest(1));
Check((await Request(HttpMethod.Get, "api/mobile-guide", token: Token(cancelTicket))).Status == 410, "Cancelled visit ends mobile guide");
Check((await Issue(Guid.NewGuid())).Status == 404, "Unknown visit cannot issue QR");
var active = await Register(3, room1);
Check((await Issue(active.Id, Guid.Empty)).Status == 400, "Empty kiosk location rejected");
Check((await Issue(active.Id, Guid.NewGuid())).Status == 404, "Unknown kiosk location rejected");
var outageTicket = (await Issue(active.Id)).Body.Deserialize<MobileGuideTicket>(options)!;
await Docker("stop");
var failedRead = await Request(HttpMethod.Get, "api/mobile-guide", token: Token(outageTicket));
Check(failedRead.Status == 503 && failedRead.Body.GetProperty("code").GetString() == "GUIDE_STORE_UNAVAILABLE", "Redis outage blocks mobile access instead of bypassing token check");
var failedIssue = await Issue(active.Id); Check(failedIssue.Status == 503 && !failedIssue.Body.TryGetProperty("url", out _), "Redis outage does not return unbacked QR URL");
await Register(4, room1); Check(true, "Registration still works while QR Redis store is unavailable");
await Docker("start");
var recovered = false;
for (var i = 0; i < 25; i++)
{
    var retry = await Issue(active.Id); if (retry.Status == 201) { recovered = true; break; }
    await Task.Delay(300);
}
Check(recovered, "QR issuance resumes after isolated Redis restart");
Console.WriteLine("ALL STEP 9 MOBILE GUIDE TESTS PASSED");
sealed record Reply(int Status, JsonElement Body, string Cache);
