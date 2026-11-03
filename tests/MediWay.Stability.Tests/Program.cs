using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MediWay.Contracts;
using MediWay.Kiosk.Services;
using MediWay.Kiosk.ViewModels;
using MediWay.Staff.Services;
using MediWay.Staff.ViewModels;
using MediWay.Display.Services;
using MediWay.Display.ViewModels;

if(args.Length!=3 || !int.TryParse(args[1],out var seconds) || seconds<60 || seconds>86400)
    throw new ArgumentException("Usage: <isolated-api-base-url> <duration-seconds 60..86400> <output-folder>");
var address=new Uri(args[0]);
if(!address.IsLoopback) throw new ArgumentException("Stability test permits loopback API only.");
var output=Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
using var http=new HttpClient {BaseAddress=address,Timeout=TimeSpan.FromSeconds(3)};
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(seconds+120));
var clock=Stopwatch.StartNew(); var displayGate=new object();
var api=new CountedDisplayApi(http); var wire=new CountedConnection(address);
var voice=new SilentVoice();
var display=new DisplayViewModel(api,wire,a=>{lock(displayGate)a();},TimeSpan.FromSeconds(1),new CallAnnouncer(voice));
var didRefs=new Queue<WeakReference>();
var retained=new Queue<WeakReference[]>(); var visits=new HashSet<Guid>();
var cycles=0; var probes=0; var samples=0; var sawOffline=false; var recovered=false;
var offlineAt=0d; var recoveredAfter=0d; var nextSample=0d;
long? warmManaged=null;
using var metrics=new StreamWriter(Path.Combine(output,"client-metrics.csv"));
metrics.WriteLine("elapsed_seconds,cycles,managed_bytes,private_bytes,working_set_bytes,handles,threads,realtime,display_fresh,active_connections");
using var sampleStop=new CancellationTokenSource();
display.Start(); display.Start();
var sampling=Task.Run(async ()=>
{
    while(!sampleStop.IsCancellationRequested)
    {
        bool fresh,realtime;
        lock(displayGate){fresh=display.IsFresh;realtime=display.IsRealtime;}
        if(!fresh && clock.Elapsed.TotalSeconds>10 && !sawOffline){sawOffline=true;offlineAt=clock.Elapsed.TotalSeconds;}
        if(sawOffline && fresh && realtime && !recovered){recovered=true;recoveredAfter=clock.Elapsed.TotalSeconds-offlineAt;}
        if(clock.Elapsed.TotalSeconds>=nextSample)
        {
            using var process=Process.GetCurrentProcess();process.Refresh();
            metrics.WriteLine(FormattableString.Invariant($"{clock.Elapsed.TotalSeconds:F2},{cycles},{GC.GetTotalMemory(false)},{process.PrivateMemorySize64},{process.WorkingSet64},{process.HandleCount},{process.Threads.Count},{realtime},{fresh},{CountedConnection.Active}"));
            metrics.Flush();samples++;nextSample=clock.Elapsed.TotalSeconds+5;
        }
        try{await Task.Delay(250,sampleStop.Token);}catch(OperationCanceledException){break;}
    }
});
try
{
    await Until(()=>{lock(displayGate)return display.IsFresh&&display.IsRealtime;},"initial DID connection");
    while(clock.Elapsed.TotalSeconds<seconds)
    {
        var refs=await CycleAsync(http,address,output,cycles,visits,deadline.Token);
        retained.Enqueue(refs);while(retained.Count>30)retained.Dequeue();cycles++;
        if(cycles%10==0)
        {
            didRefs.Enqueue(await ProbeDisplayAsync(http,address,deadline.Token));while(didRefs.Count>30)didRefs.Dequeue();probes++;
            Console.WriteLine($"PROGRESS seconds={clock.Elapsed.TotalSeconds:F1} cycles={cycles} probes={probes}");
        }
        if(warmManaged is null && clock.Elapsed.TotalSeconds>=Math.Min(60,seconds/3)) warmManaged=CollectRetainedBytes();
        await Task.Delay(500,deadline.Token);
    }
    await Until(()=>{lock(displayGate)return display.IsFresh&&display.IsRealtime;},"final DID recovery");
}
finally
{
    sampleStop.Cancel();await sampling;
    await display.DisposeAsync();await display.DisposeAsync();
}
await Task.Delay(300,deadline.Token);
var callsAfterDispose=api.Calls;
await Task.Delay(1300,deadline.Token);
Require(api.Calls==callsAfterDispose,"disposed persistent DID stops polling");
Require(wire.Subscribers==0 && CountedConnection.Active==0,"all realtime connections and subscriptions released");
Require(voice.Disposed,"announcement worker disposes voice");
Require(api.MaxActive==1,"DID snapshot requests remain serialized");
var after=CollectRetainedBytes();
// Exclude the most recent cycle because async/JIT frames may retain their last awaited value.
var watched=retained.Take(Math.Max(0,retained.Count-1)).SelectMany(x=>x).ToArray();
var alive=watched.Count(x=>x.IsAlive);
Require(watched.Length>0 && alive==0,"older disposed kiosk/staff/health-monitor objects are collectible");
Require(didRefs.All(x=>!x.IsAlive),"disposed DID viewmodels are collectible");
Require(cycles>=10,"at least ten complete visit lifecycles");
Require(sawOffline && recovered,"DID observes deliberate API outage and reconnects");
var report=new {DurationSeconds=clock.Elapsed.TotalSeconds,Cycles=cycles,UniqueVisits=visits.Count,DisplayLifecycleProbes=probes,
    Samples=samples,ObservedOutage=sawOffline,Recovered=recovered,RecoveryFromFirstFailureSeconds=recoveredAfter,
    WarmManagedBytes=warmManaged,FinalManagedBytes=after,ManagedGrowthBytes=warmManaged is {} warm ? after-warm : (long?)null,
    WatchedDisposedObjects=watched.Length,AliveDisposedObjects=alive,WatchedDisposedDisplays=didRefs.Count,AliveDisposedDisplays=didRefs.Count(x=>x.IsAlive),RealtimeActiveAfterDispose=CountedConnection.Active,
    SnapshotCalls=api.Calls,SnapshotMaxConcurrent=api.MaxActive,SilentVoiceCalls=voice.Calls,
    Scope="Production ViewModels/HTTP/SignalR/JSON recovery/ticket files; no native WPF rendering or physical audio soak"};
await File.WriteAllTextAsync(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(report));
Console.WriteLine("ALL STABILITY CHECKS PASSED");

static void Require(bool value,string name){if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
[MethodImpl(MethodImplOptions.NoInlining)]
static long CollectRetainedBytes(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();return GC.GetTotalMemory(true);}
static async Task Until(Func<bool> predicate,string name,CancellationToken ct=default)
{
    var watch=Stopwatch.StartNew();
    while(!predicate()){if(watch.Elapsed>TimeSpan.FromSeconds(45))throw new TimeoutException(name);await Task.Delay(100,ct);}
}
static async Task Retry(Func<Task> action,Func<bool> done,string name,CancellationToken ct)
{
    var watch=Stopwatch.StartNew();
    do{await action();if(done())return;if(watch.Elapsed>TimeSpan.FromSeconds(45))throw new TimeoutException(name);await Task.Delay(300,ct);}while(true);
}
static async Task<WeakReference[]> CycleAsync(HttpClient http,Uri address,string output,int sequence,HashSet<Guid> visits,CancellationToken ct)
{
    using var offline=new OfflineRegistrationStore(Path.Combine(output,"Offline"),address.ToString());
    using var kiosk=new KioskViewModel(new KioskApiClient(http),ticketWriter:new WaitingTicketWriter(Path.Combine(output,"Tickets")),offlineStore:offline);
    var monitor=new ServerConnectionMonitor(http,a=>a(),TimeSpan.FromMilliseconds(500));monitor.Start();monitor.Start();
    using var staff=new StaffViewModel(new StaffApiClient(http));
    try
    {
        await kiosk.InitializeCommand.ExecuteAsync();
        kiosk.PatientNumber="0001";
        await Retry(()=>kiosk.LookupCommand.ExecuteAsync(),()=>kiosk.IsConfirm,"patient lookup",ct);
        var code=sequence%2==0?"IM01":"SU03";
        kiosk.SelectedRoom=kiosk.Rooms.Single(x=>x.Code==code);
        await Retry(()=>kiosk.RegisterCommand.ExecuteAsync(),()=>kiosk.IsResult,"idempotent registration recovery",ct);
        var visit=kiosk.Result!.Visit;
        Require(!kiosk.Result.IsExisting && visits.Add(visit.Id),"new lifecycle receives unique committed visit");
        Require(File.Exists(kiosk.TicketFilePath),"confirmed visit writes local ticket");
        await Retry(async ()=>
        {
            await staff.RefreshCommand.ExecuteAsync();
            if(staff.Rooms.Count==0)return;
            staff.SelectedRoom=staff.Rooms.Single(x=>x.Id==visit.RoomId);
            await staff.RefreshCommand.ExecuteAsync();
        },()=>staff.IsConnected && staff.SelectedRoom?.Id==visit.RoomId,"staff room refresh",ct);
        await Retry(async ()=>
        {
            if(staff.NeedsRetry){await staff.RetryCommand.ExecuteAsync();return;}
            await staff.RefreshCommand.ExecuteAsync();
            staff.SelectedWaiting=staff.Waiting.SingleOrDefault(x=>x.VisitId==visit.Id);
            await staff.CallCommand.ExecuteAsync();
        },()=>staff.Current?.Id==visit.Id && staff.Current.Status=="Called","call recovery",ct);
        await Retry(async ()=>{if(staff.NeedsRetry)await staff.RetryCommand.ExecuteAsync();else await staff.StartCommand.ExecuteAsync();},
            ()=>staff.Current?.Status=="InService","start recovery",ct);
        await Retry(async ()=>{if(staff.NeedsRetry)await staff.RetryCommand.ExecuteAsync();else await staff.CompleteCommand.ExecuteAsync();},
            ()=>staff.Current is null&&!staff.NeedsRetry,"complete recovery",ct);
        VisitResponse? completed=null;
        await Retry(async ()=>{try{completed=await http.GetFromJsonAsync<VisitResponse>($"api/visits/{visit.Id}",ct);}catch(HttpRequestException){}catch(TaskCanceledException)when(!ct.IsCancellationRequested){}},
            ()=>completed is {Status:"Completed",Version:4},"final visit verification",ct);
        kiosk.HomeCommand.Execute(null);Require(kiosk.IsInput,"home clears completed kiosk session");
    }
    finally{await monitor.DisposeAsync();}
    return [new(kiosk),new(staff),new(monitor)];
}
static async Task<WeakReference> ProbeDisplayAsync(HttpClient http,Uri address,CancellationToken ct)
{
    var api=new CountedDisplayApi(http);var wire=new CountedConnection(address);var gate=new object();
    var vm=new DisplayViewModel(api,wire,a=>{lock(gate)a();},TimeSpan.FromMilliseconds(200));
    vm.Start();vm.Start();
    try{await Until(()=>{lock(gate)return vm.IsFresh&&vm.IsRealtime;},"lifecycle DID connect",ct);}
    finally{await vm.DisposeAsync();await vm.DisposeAsync();}
    var calls=api.Calls;await Task.Delay(300,ct);
    Require(wire.Subscribers==0 && api.Calls==calls,"disposed DID probe stops worker/timers and removes event handlers");
    return new(vm);
}

sealed class CountedDisplayApi(HttpClient http) : IDisplayApi
{
    private readonly DisplayApi api=new(http);private int active,calls,max;
    public int Calls=>Volatile.Read(ref calls);public int MaxActive=>Volatile.Read(ref max);
    public async Task<DisplaySnapshot> SnapshotAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref calls);var value=Interlocked.Increment(ref active);if(value>max)Interlocked.Exchange(ref max,value);
        try{return await api.SnapshotAsync(ct);}finally{Interlocked.Decrement(ref active);}
    }
}
sealed class CountedConnection(Uri address) : IQueueConnection
{
    private readonly QueueConnection inner=new(address);private bool disposed;private int subscribers;
    private static int active;
    public static int Active=>Volatile.Read(ref active);
    private readonly bool counted=Increment();
    private static bool Increment(){Interlocked.Increment(ref active);return true;}
    public int Subscribers=>subscribers;
    public bool IsConnected=>inner.IsConnected;
    public event Action<QueueChanged>? Changed {add{inner.Changed+=value;subscribers++;}remove{inner.Changed-=value;subscribers--;}}
    public event Action? ConnectionChanged {add{inner.ConnectionChanged+=value;subscribers++;}remove{inner.ConnectionChanged-=value;subscribers--;}}
    public Task EnsureConnectedAsync(Guid[] roomIds,CancellationToken ct)=>inner.EnsureConnectedAsync(roomIds,ct);
    public async ValueTask DisposeAsync(){if(disposed)return;disposed=true;await inner.DisposeAsync();if(counted)Interlocked.Decrement(ref active);}
}
sealed class SilentVoice : IAnnouncementVoice
{
    public bool IsAvailable=>true;public string Description=>"Stability test silent voice";
    public int Calls;public bool Disposed;
    public async Task SpeakAsync(string text,CancellationToken ct){Interlocked.Increment(ref Calls);await Task.Delay(10,ct);}
    public void Dispose()=>Disposed=true;
}