using MediWay.Contracts;
using MediWay.Display.Services;
using MediWay.Display.ViewModels;

internal static class RotationChecks
{
    public static async Task RunAsync()
    {
        var time=DateTimeOffset.UtcNow;var api=new FloorsApi();var live=new FakeConnection();
        var voice=new Voice();var announcer=new CallAnnouncer(voice);
        await using var vm=new DisplayViewModel(api,live,a=>a(),TimeSpan.FromHours(1),announcer,()=>time);
        vm.Start();await Until(()=>vm.IsFresh);
        Check(vm.VisibleRooms.Select(r=>r.RoomId).SequenceEqual(api.Ids.Take(2))&&vm.PageText.Contains("1층"),"DID first page shows only first-floor rooms 1 and 2");
        Check(live.Subscriptions.Length==4&&vm.Rooms.Count==4,"All four rooms stay subscribed even when two are hidden");
        api.CalledIndex=2;live.Emit();await Until(()=>vm.Rooms[2].Number=="303"&&voice.Count==1);
        Check(vm.VisibleRooms[0].RoomId==api.Ids[0],"Hidden floor call updates and speaks without interrupting current page");
        time=time.AddSeconds(9.9);vm.TickPage();Check(vm.VisibleRooms[0].RoomId==api.Ids[0],"First page remains before ten seconds despite live refresh");
        time=time.AddMilliseconds(100);vm.TickPage();
        Check(vm.VisibleRooms.Select(r=>r.RoomId).SequenceEqual(api.Ids.Skip(2))&&vm.VisibleRooms[0].Number=="303"&&vm.PageText.Contains("2층"),"At ten seconds DID switches to latest surgery rooms 3 and 4");
        time=time.AddSeconds(10);vm.TickPage();
        Check(vm.VisibleRooms[0].RoomId==api.Ids[0]&&voice.Count==1,"Twenty-second boundary returns to first floor without repeating speech");
        api.Fail=true;vm.RequestRefresh();await Until(()=>!vm.IsFresh);time=time.AddSeconds(10);vm.TickPage();
        Check(vm.VisibleRooms[0].RoomId==api.Ids[2]&&!vm.IsFresh,"Stale pages continue cycling with disconnected indicator");
        await vm.DisposeAsync();var page=vm.VisibleRooms[0].RoomId;time=time.AddSeconds(10);vm.TickPage();
        Check(vm.VisibleRooms[0].RoomId==page&&voice.Disposed,"Closing DID stops page rotation and disposes voice");
    }
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS: "+label);}
    private static async Task Until(Func<bool> value){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!value())await Task.Delay(10,timeout.Token);}
    private sealed class FloorsApi:IDisplayApi
    {
        public Guid[] Ids=Enumerable.Range(1,4).Select(n=>Guid.Parse($"20000000-0000-0000-0000-{n:D12}")).ToArray();
        public int CalledIndex=-1;public bool Fail;
        public Task<DisplaySnapshot> SnapshotAsync(CancellationToken ct)
        {
            if(Fail)throw new System.Net.Http.HttpRequestException();
            return Task.FromResult(new DisplaySnapshot(DateTime.UtcNow,DateOnly.FromDateTime(DateTime.Today),Ids.Select((id,i)=>new DisplayRoomSnapshot(id,$"{(i<2?"내과":"외과")} {i+1}진료실",i<2?"내과":"외과",CalledIndex==i?new DisplayCurrent(id,303,"Called",2,DateOnly.FromDateTime(DateTime.Today)):null,[],i<2?"F1":"F2",i<2?"1층":"2층")).ToArray()));
        }
    }
    private sealed class Voice:IAnnouncementVoice
    {
        public bool IsAvailable=>true;public string Description=>"테스트";public int Count;public bool Disposed;
        public Task SpeakAsync(string text,CancellationToken ct){Interlocked.Increment(ref Count);return Task.CompletedTask;}
        public void Dispose()=>Disposed=true;
    }
}
