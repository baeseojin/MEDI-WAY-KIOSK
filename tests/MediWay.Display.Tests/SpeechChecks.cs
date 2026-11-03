using System.Collections.Concurrent;
using MediWay.Contracts;
using MediWay.Display.Services;

internal static class SpeechChecks
{
    public static async Task RunAsync()
    {
        var room = Guid.NewGuid(); var id = Guid.NewGuid(); var day = new DateOnly(2026,11,2);
        var time = DateTimeOffset.UtcNow;
        DisplaySnapshot Snapshot(DisplayCurrent? call, Guid? otherRoom = null) => new(DateTime.UtcNow,day,
            [new(otherRoom ?? room,"내과 1진료실","내과",call,[])]);
        var call = new DisplayCurrent(id,101,"Called",2,day);
        var voice = new TestVoice();
        await using var announcer = new CallAnnouncer(voice,()=>time);
        announcer.Observe(Snapshot(call)); await Quiet();
        Check(voice.Texts.Count==0,"Startup Called snapshot is silent");
        announcer.Observe(Snapshot(null));
        var next=call with {VisitId=Guid.NewGuid(),QueueNumber=102};
        announcer.Observe(Snapshot(next)); await Until(()=>voice.Texts.Count==1);
        Check(voice.Texts.Single()=="102번 환자분, 내과 1진료실로 들어오세요.","New call speaks number and room without patient identity");
        announcer.Observe(Snapshot(next)); announcer.Observe(Snapshot(next)); await Quiet();
        Check(voice.Texts.Count==1,"Duplicate snapshots never repeat speech");
        announcer.Observe(Snapshot(next with {Status="InService",Version=3})); await Quiet();
        Check(voice.Texts.Count==1,"Starting treatment is not a voice call");
        announcer.Observe(Snapshot(null)); announcer.Observe(Snapshot(next)); await Quiet();
        Check(voice.Texts.Count==1,"Previously observed visit/version stays deduplicated after empty snapshot");
        announcer.Enabled=false;
        announcer.Observe(Snapshot(call with {VisitId=Guid.NewGuid()})); announcer.Enabled=true; announcer.Observe(Snapshot(call)); await Quiet();
        Check(voice.Texts.Count==1,"Muted and initial calls are not replayed when enabled");
        announcer.Suspend(); announcer.Observe(Snapshot(call with {VisitId=Guid.NewGuid()})); await Quiet();
        Check(voice.Texts.Count==1,"Recovery snapshot does not replay missed calls");
        voice.Hold=true;
        next=call with {VisitId=Guid.NewGuid()}; announcer.Observe(Snapshot(next)); await Until(()=>voice.Texts.Count==2);
        announcer.Observe(Snapshot(null)); await Until(()=>voice.Cancelled==1);
        Check(true,"Call completion or cancellation stops active voice");
        next=call with {VisitId=Guid.NewGuid()}; announcer.Observe(Snapshot(next)); await Until(()=>voice.Texts.Count==3);
        announcer.Enabled=false; await Until(()=>voice.Cancelled==2);
        Check(announcer.StatusText.Contains("꺼짐"),"Mute cancels active voice and updates status");
        announcer.Enabled=true;
        next=call with {VisitId=Guid.NewGuid()}; announcer.Observe(Snapshot(next)); await Until(()=>voice.Texts.Count==4);
        // A second room queues behind the first, then disappears before playback.
        var second=Guid.NewGuid(); var secondCall=call with {VisitId=Guid.NewGuid(),QueueNumber=999};
        announcer.Observe(new(DateTime.UtcNow,day,[new(room,"내과 1진료실","내과",next,[]),new(second,"내과 2진료실","내과",secondCall,[])]));
        announcer.Observe(Snapshot(null)); await Until(()=>voice.Cancelled==3); await Quiet();
        Check(!voice.Texts.Any(t=>t.StartsWith("999번")),"Queued call is discarded when latest snapshot no longer calls it");
        voice.Hold=false; voice.Fail=true;
        announcer.Test(); await Until(()=>announcer.StatusText.Contains("재생하지"));
        Check(true,"Audio failure becomes status instead of crashing display");
        voice.Fail=false; announcer.Test(); await Until(()=>announcer.StatusText==voice.Description);
        Check(voice.MaxActive==1,"Voice queue serializes rooms and recovers after failure");
        await announcer.DisposeAsync(); var count=voice.Texts.Count; announcer.Test(); announcer.Observe(Snapshot(next)); await Quiet();
        Check(voice.Disposed && voice.Texts.Count==count,"Dispose closes voice and rejects late calls");
        var unavailable=new TestVoice {IsAvailable=false};
        await using var disabled=new CallAnnouncer(unavailable);
        disabled.Observe(Snapshot(null)); disabled.Observe(Snapshot(next)); disabled.Test(); await Quiet();
        Check(unavailable.Texts.IsEmpty,"Missing voice keeps display silent without throwing");

        var heldVoice = new TestVoice { Hold = true };
        await using var bounded = new CallAnnouncer(heldVoice, () => time);
        bounded.Observe(Snapshot(null)); bounded.Observe(Snapshot(call)); await Until(() => heldVoice.Texts.Count == 1);
        var pendingRoom = Guid.NewGuid(); var pendingCall = call with { VisitId = Guid.NewGuid(), QueueNumber = 303 };
        bounded.Observe(new(DateTime.UtcNow,day,[new(room,"내과 1진료실","내과",call,[]),new(pendingRoom,"내과 2진료실","내과",pendingCall,[])]));
        time = time.AddSeconds(16);
        bounded.Observe(Snapshot(pendingCall,pendingRoom)); await Until(() => heldVoice.Cancelled == 1); await Quiet();
        Check(heldVoice.Texts.Count == 1,"Queued voice older than fifteen seconds is discarded");
        var fresh = call with { VisitId = Guid.NewGuid() }; bounded.Observe(Snapshot(fresh)); await Until(() => heldVoice.Texts.Count == 2);
        var rooms = Enumerable.Range(0,20).Select(n => new DisplayRoomSnapshot(Guid.NewGuid(),"가상 진료실","내과",call with {VisitId=Guid.NewGuid(),QueueNumber=400+n},[])).ToList();
        rooms.Add(new(room,"내과 1진료실","내과",fresh,[]));
        bounded.Observe(new(DateTime.UtcNow,day,rooms));
        Check(bounded.StatusText.Contains("가득"),"Bounded speech queue reports overflow instead of accumulating indefinitely");
        bounded.Enabled=false; await Until(()=>heldVoice.Cancelled==2); await Quiet();
        Check(heldVoice.Texts.Count==2,"Muting discards all queued voices");

        var connectedVoice = new TestVoice { Hold = true };
        var connectedAnnouncer = new CallAnnouncer(connectedVoice);
        var api = new FakeApi(room); var connection = new FakeConnection();
        await using var display = new MediWay.Display.ViewModels.DisplayViewModel(api,connection,a=>a(),TimeSpan.FromMilliseconds(100),connectedAnnouncer);
        display.Start(); await Until(()=>display.IsFresh);
        api.Current=call; connection.Emit(); await Until(()=>connectedVoice.Texts.Count==1);
        Check(display.Rooms[0].Number=="101","Display snapshot updates visual panel and voice together");
        api.Fail=true; display.RequestRefresh(); await Until(()=>!display.IsFresh && connectedVoice.Cancelled==1);
        Check(true,"HTTP snapshot failure stops active voice while preserving stale visual data");
        await display.DisposeAsync();
        Check(connectedVoice.Disposed,"Display shutdown owns and disposes speech service");
    }
    private static Task Quiet()=>Task.Delay(80);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(!predicate())await Task.Delay(10,timeout.Token);
    }
    private static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS: "+text);}
    private sealed class TestVoice : IAnnouncementVoice
    {
        public bool IsAvailable {get;set;}=true;
        public string Description=>"테스트 음성 준비됨";
        public ConcurrentQueue<string> Texts=new();
        public volatile bool Hold,Fail,Disposed;
        public int Cancelled,MaxActive,active;
        public async Task SpeakAsync(string text,CancellationToken ct)
        {
            var n=Interlocked.Increment(ref active);MaxActive=Math.Max(MaxActive,n);Texts.Enqueue(text);
            try {if(Fail)throw new InvalidOperationException();if(Hold)await Task.Delay(Timeout.Infinite,ct);}
            catch(OperationCanceledException){Interlocked.Increment(ref Cancelled);throw;}
            finally{Interlocked.Decrement(ref active);}
        }
        public void Dispose()=>Disposed=true;
    }
}
