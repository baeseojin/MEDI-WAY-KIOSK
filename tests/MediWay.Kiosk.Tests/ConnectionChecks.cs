using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MediWay.Contracts;
using MediWay.Kiosk.Services;

internal static class ConnectionChecks
{
    public static async Task RunAsync()
    {
        var transport=new Transport();
        using var http=new HttpClient(transport){BaseAddress=new Uri("http://localhost/")};
        await using var monitor=new ServerConnectionMonitor(http,a=>a(),TimeSpan.FromMilliseconds(40));
        monitor.Start();monitor.Start();await Until(()=>monitor.StatusText=="서버 연결됨");
        Check(transport.Path=="/health/ready","Connection monitor checks real dependency readiness endpoint");
        transport.Mode="RedisDown";await Until(()=>monitor.StatusText.Contains("QR"));
        Check(true,"Redis-only outage shows connected state with QR limitation");
        transport.Mode="DbDown";await Until(()=>monitor.StatusText.Contains("저장소"));
        Check(true,"Database outage shows storage connection problem");
        transport.Mode="Fail";await Until(()=>monitor.StatusText.Contains("안 됨"));
        Check(true,"Network failure updates visible disconnected status");
        transport.Mode="Healthy";await Until(()=>monitor.StatusText=="서버 연결됨");
        Check(transport.MaxActive==1,"Recovered monitor remains serialized without overlapping probes");
        await monitor.DisposeAsync();var calls=transport.Calls;await Task.Delay(100);
        Check(transport.Calls==calls,"Closed monitor stops polling and releases worker");
    }
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS: "+label);}
    private static async Task Until(Func<bool> predicate)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!predicate())await Task.Delay(10,timeout.Token);}
    private sealed class Transport:HttpMessageHandler
    {
        public volatile string Mode="Healthy";public string? Path;public int Calls,MaxActive,active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;var n=Interlocked.Increment(ref active);MaxActive=Math.Max(MaxActive,n);
            try
            {
                Path=request.RequestUri!.AbsolutePath;await Task.Delay(10,ct);
                var mode=Mode;if(mode=="Fail")throw new HttpRequestException();
                return new(mode=="DbDown"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=JsonContent.Create(new HealthResponse(mode,new(mode=="DbDown"?"Down":"Up"),new(mode=="RedisDown"?"Down":"Up")))};
            }
            finally{Interlocked.Decrement(ref active);}
        }
    }
}
