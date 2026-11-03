using MediWay.Domain.Reception;
using MediWay.Application.Reception;

var today=new DateOnly(2026,11,3);
var patient=Guid.NewGuid();
var count=0;
void Check(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine($"PASS: {name}"); count++; }
Visit Make(VisitStatus status=VisitStatus.Waiting,int version=1,DateOnly? date=null,Guid? patientId=null) =>
    Visit.Restore(Guid.NewGuid(),patientId??patient,Guid.NewGuid(),Guid.NewGuid(),date??today,1,status,version,DateTime.UtcNow);
TransitionDecision Act(Visit visit,VisitAction action,int version=1) => action switch
{
    VisitAction.Call=>visit.Call(version,today,false), VisitAction.StartConsultation=>visit.StartConsultation(version),
    VisitAction.Complete=>visit.Complete(version), _=>visit.Cancel(version)
};
foreach(var status in Enum.GetValues<VisitStatus>())
foreach(var action in Enum.GetValues<VisitAction>())
{
    var allowed=(status,action) is (VisitStatus.Waiting,VisitAction.Call) or (VisitStatus.Called,VisitAction.StartConsultation)
        or (VisitStatus.InService,VisitAction.Complete) or (VisitStatus.Waiting,VisitAction.Cancel) or (VisitStatus.Called,VisitAction.Cancel);
    var visit=Make(status); var result=Act(visit,action);
    Check(allowed ? result.Error is null && result.Event is not null && visit.Version==2
        : result.Error?.Code=="INVALID_TRANSITION" && result.Event is null && visit.Status==status && visit.Version==1,
        $"state matrix {status} / {action}");
}
var flow=Make();
Check(flow.Call(1,today,false).Event?.Status==VisitStatus.Called,"call produces domain event");
Check(flow.StartConsultation(2).Error is null && flow.Complete(3).Error is null && flow.Version==4,"full consultation lifecycle");
Check(flow.Complete(3).IsReplay && flow.Version==4,"completed response-loss retry does not mutate");
Check(flow.Cancel(4).Error?.Code=="INVALID_TRANSITION","completed visit cannot cancel");
var stale=Make();
Check(stale.Call(9,today,false).Error?.Code=="VERSION_CONFLICT" && stale.Version==1,"stale command leaves aggregate unchanged");
Check(stale.Call(0,today,false).Error?.Code=="INVALID_REQUEST","invalid expected version");
Check(stale.Call(1,today,true).Error?.Code=="ROOM_BUSY" && stale.Status==VisitStatus.Waiting,"busy room rejects call");
Check(stale.Call(1,today.AddDays(1),false).Error?.Code=="PAST_VISIT","previous-day waiting cannot be called");
var replay=Make(VisitStatus.Called,2);
Check(replay.Call(1,today.AddDays(1),true) is { IsReplay:true,Event:null,Error:null },"call replay survives midnight and occupied room");
Check(replay.Call(2,today,false).Error?.Code=="INVALID_TRANSITION","same state with new version is not a replay");
var max=Make(version:int.MaxValue);
try { max.Call(int.MaxValue,today,false); throw new Exception("Expected overflow"); }
catch(OverflowException) { Check(max.Status==VisitStatus.Waiting && max.Version==int.MaxValue,"overflow cannot partially mutate status"); }
foreach(var status in Enum.GetValues<VisitStatus>())
{
    var visit=Make(status);
    Check(RegistrationPolicy.ReuseExisting(new(patient,today),visit)==(status is VisitStatus.Waiting or VisitStatus.Called or VisitStatus.InService),$"hospital registration policy {status}");
}
Check(!RegistrationPolicy.ReuseExisting(new(Guid.NewGuid(),today),Make()),"different patient is independent");
Check(!RegistrationPolicy.ReuseExisting(new(patient,today.AddDays(1)),Make()),"different service day is independent");
Check(RegistrationPolicy.ValidateDate(today.AddDays(-1),today)?.Code=="SERVICE_DATE_CHANGED","offline old-day request rejected");
Check(RegistrationPolicy.ValidateDate(null,today) is null && RegistrationPolicy.ValidateDate(today,today) is null,"legacy and same-day registration accepted");
var fresh=Visit.Register(Guid.NewGuid(),patient,Guid.NewGuid(),Guid.NewGuid(),today,42,DateTime.UtcNow);
Check(fresh.Status==VisitStatus.Waiting && fresh.Version==1 && fresh.QueueNumber==42,"registration factory establishes initial invariants");
try { Visit.Register(Guid.Empty,patient,Guid.NewGuid(),Guid.NewGuid(),today,1,DateTime.UtcNow); throw new Exception("Expected invalid identity"); }
catch(ArgumentException) { Check(true,"empty aggregate identity rejected"); }
try { Visit.Register(Guid.NewGuid(),patient,Guid.NewGuid(),Guid.NewGuid(),today,0,DateTime.UtcNow); throw new Exception("Expected invalid number"); }
catch(ArgumentOutOfRangeException) { Check(true,"invalid queue number rejected"); }

var store=new MemoryTransitionStore(Make(),today);
var notifications=new Notifications(()=>store.Committed);
var service=new TransitionVisitService(store,notifications);
var id=store.Visit.Id;
var success=await service.ExecuteAsync(new(id,1,VisitAction.Call),default);
Check(success.Error is null && notifications.Invalidations==1 && notifications.Events==1,"application notifies after committed transition");
await service.ExecuteAsync(new(id,1,VisitAction.Call),default);
Check(notifications.Invalidations==2 && notifications.Events==1,"application replay invalidates without duplicate announcement");
await service.ExecuteAsync(new(id,1,VisitAction.Cancel),default);
Check(notifications.Invalidations==2 && notifications.Events==1,"rejected transition sends no notifications");
var calls=store.Calls;
Check((await service.ExecuteAsync(new(Guid.Empty,1,VisitAction.Call),default)).Error?.Code=="INVALID_REQUEST" && store.Calls==calls,"invalid command never opens transaction");
store.Fail=true;
try { await service.ExecuteAsync(new(id,2,VisitAction.StartConsultation),default); throw new Exception("Expected persistence failure"); }
catch(IOException) { Check(notifications.Invalidations==2 && notifications.Events==1,"failed persistence sends no notification"); }
var missing=new TransitionVisitService(new MissingStore(),notifications);
Check((await missing.ExecuteAsync(new(Guid.NewGuid(),1,VisitAction.Call),default)).Error?.Code=="VISIT_NOT_FOUND","missing visit remains explicit error");

var registrationStore=new MemoryRegistrationStore(fresh);
var registrationNotifications=new Notifications(()=>registrationStore.Committed);
var registration=new RegisterVisitService(registrationStore,registrationNotifications);
var request=new RegisterVisitCommand(patient,fresh.RoomId,Guid.NewGuid(),today);
await registration.ExecuteAsync(request,default);
Check(registrationNotifications.Registrations==1 && registrationNotifications.Invalidations==1,"registration application announces new committed visit");
var originalFingerprint=registrationStore.Fingerprint;
registrationStore.Existing=true;
await registration.ExecuteAsync(request,default);
Check(registrationNotifications.Registrations==1 && registrationNotifications.Invalidations==2 && originalFingerprint==registrationStore.Fingerprint,"registration replay has stable fingerprint and no new announcement");
await registration.ExecuteAsync(request with {ExpectedServiceDate=today.AddDays(1)},default);
Check(originalFingerprint!=registrationStore.Fingerprint,"registration fingerprint binds service date");
await registration.ExecuteAsync(request with {ExpectedServiceDate=null},default);
Check(registrationStore.Fingerprint==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{patient:D}|{fresh.RoomId:D}"))),"legacy fingerprint compatibility");
calls=registrationStore.Calls;
Check((await registration.ExecuteAsync(request with {PatientId=Guid.Empty},default)).Error?.Code=="INVALID_REQUEST" && registrationStore.Calls==calls,"invalid registration bypasses persistence");
registrationStore.Error=new("SERVICE_DATE_CHANGED","date changed");
var notificationsBefore=registrationNotifications.Invalidations;
await registration.ExecuteAsync(request,default);
Check(registrationNotifications.Invalidations==notificationsBefore,"registration failure sends no cache or live notification");

var domainReferences=typeof(Visit).Assembly.GetReferencedAssemblies().Select(x=>x.Name??"").ToArray();
Check(!domainReferences.Any(x=>x.StartsWith("MediWay.") || x.StartsWith("Npgsql") || x.Contains("Redis") || x.Contains("AspNet") || x.Contains("PresentationFramework")),"domain assembly has no application, framework or infrastructure dependency");
var applicationReferences=typeof(TransitionVisitService).Assembly.GetReferencedAssemblies().Select(x=>x.Name??"").ToArray();
Check(applicationReferences.Contains("MediWay.Domain") && !applicationReferences.Any(x=>x is "MediWay.Api" or "MediWay.Infrastructure" or "MediWay.Contracts" || x.Contains("Npgsql") || x.Contains("Redis")),"application depends inward on domain only");
Console.WriteLine($"DDD CHECKS PASSED: {count}");

sealed class MemoryTransitionStore(Visit visit,DateOnly today) : IVisitTransitionStore
{
    public Visit Visit=>visit;
    public bool Committed {get;private set;}
    public bool Fail {get;set;}
    public int Calls {get;private set;}
    public Task<TransitionResult> ExecuteAsync(Guid id,Func<Visit,DateOnly,bool,TransitionDecision> decide,CancellationToken ct)
    {
        Calls++; Committed=false;
        if(Fail) throw new IOException("Simulated database failure");
        var result=decide(visit,today,visit.Status is VisitStatus.Called or VisitStatus.InService);
        Committed=result.Error is null;
        return Task.FromResult(new TransitionResult(visit,result.Error,result.IsReplay,result.Event));
    }
}
sealed class MissingStore : IVisitTransitionStore
{
    public Task<TransitionResult> ExecuteAsync(Guid id,Func<Visit,DateOnly,bool,TransitionDecision> decide,CancellationToken ct) =>
        Task.FromResult(new TransitionResult(null,new("VISIT_NOT_FOUND","missing")));
}
sealed class MemoryRegistrationStore(Visit visit) : IRegistrationStore
{
    public bool Committed {get;private set;}
    public bool Existing {get;set;}
    public RuleViolation? Error {get;set;}
    public int Calls {get;private set;}
    public string? Fingerprint {get;private set;}
    public Task<RegistrationResult> RegisterAsync(RegisterVisitCommand request,string fingerprint,CancellationToken ct)
    {
        Calls++; Fingerprint=fingerprint; Committed=Error is null;
        return Task.FromResult(new RegistrationResult(Error is null ? visit : null,Existing,Error));
    }
}
sealed class Notifications(Func<bool> committed) : IVisitNotifications
{
    public int Invalidations {get;private set;}
    public int Events {get;private set;}
    public int Registrations {get;private set;}
    public Task InvalidateAsync(Guid roomId,DateOnly date) { EnsureCommit(); Invalidations++; return Task.CompletedTask; }
    public Task PublishAsync(VisitStatusChanged changed) { EnsureCommit(); Events++; return Task.CompletedTask; }
    public Task PublishRegisteredAsync(Visit visit) { EnsureCommit(); Registrations++; return Task.CompletedTask; }
    private void EnsureCommit() { if(!committed()) throw new Exception("Notification before commit"); }
}