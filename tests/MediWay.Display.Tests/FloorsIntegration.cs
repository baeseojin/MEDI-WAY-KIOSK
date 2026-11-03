using System.Net.Http;
using System.Net.Http.Json;
using MediWay.Contracts;
using MediWay.Display.Services;
using MediWay.Display.ViewModels;

internal static class FloorsIntegration
{
    public static async Task RunAsync(string address)
    {
        using var http=new HttpClient{BaseAddress=new Uri(address)};
        var rooms=(await http.GetFromJsonAsync<RoomResponse[]>("api/rooms"))!;
        Check(rooms.Length==4&&rooms.Take(2).All(r=>r.DepartmentName=="내과"&&r.FloorCode=="F1")&&rooms.Skip(2).All(r=>r.DepartmentName=="외과"&&r.FloorCode=="F2"),"Room API exposes internal medicine floor 1 and surgery floor 2");
        async Task<CreateVisitResponse> Register(RoomResponse room,int patient=1)
        {
            using var response=await http.PostAsJsonAsync("api/visits",new CreateVisitRequest(Guid.Parse($"30000000-0000-0000-0000-{patient:D12}"),room.Id,Guid.NewGuid()));
            response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<CreateVisitResponse>())!;
        }
        var internalVisit=await Register(rooms[0]);var crossDepartment=await Register(rooms[2]);
        Check(crossDepartment.IsExisting&&crossDepartment.Visit.Id==internalVisit.Visit.Id&&crossDepartment.Visit.RoomId==rooms[0].Id,"Surgery request cannot duplicate an active internal medicine registration");
        var emptySurgery=(await http.GetFromJsonAsync<QueueResponse>($"api/rooms/{rooms[2].Id}/queue?refresh=true"))!;
        Check(emptySurgery.Items.Count==0,"Rejected cross-department duplicate creates no surgery queue entry");
        var surgery=await Register(rooms[2],2);
        Check(internalVisit.Visit.QueueNumber==1&&surgery.Visit.QueueNumber==1&&!surgery.IsExisting,"Different patients retain independent department queue numbering");
        var duplicate=await Register(rooms[3],2);
        Check(duplicate.IsExisting&&duplicate.Visit.Id==surgery.Visit.Id&&duplicate.Visit.RoomId==rooms[2].Id,"Surgery duplicate preserves original room 3 instead of moving to room 4");
        var time=DateTimeOffset.UtcNow;
        await using var display=new DisplayViewModel(new DisplayApi(http),new QueueConnection(new Uri(address)),a=>a(),TimeSpan.FromHours(1),clock:()=>time);
        display.Start();await Until(()=>display.IsFresh&&display.Rooms.Count==4);
        Check(display.VisibleRooms.Select(r=>r.RoomId).SequenceEqual(rooms.Take(2).Select(r=>r.Id)),"Real DID snapshot initially displays internal medicine pair");
        using(var call=await http.PostAsJsonAsync($"api/visits/{surgery.Visit.Id}/call",new TransitionRequest(1)))call.EnsureSuccessStatusCode();
        var reverseDuplicate=await Register(rooms[0],2);
        Check(reverseDuplicate.IsExisting&&reverseDuplicate.Visit.Id==surgery.Visit.Id&&reverseDuplicate.Visit.Status=="Called","Called surgery visit also blocks new internal medicine registration");
        await Until(()=>display.Rooms.Single(r=>r.RoomId==rooms[2].Id).Number=="001");
        Check(display.VisibleRooms[0].RoomId==rooms[0].Id,"Hidden surgery call arrives through real SignalR without changing page");
        time=time.AddSeconds(10);display.TickPage();
        Check(display.VisibleRooms[0].RoomId==rooms[2].Id&&display.VisibleRooms[0].Number=="001"&&display.VisibleRooms[1].RoomId==rooms[3].Id,"Ten-second switch shows latest called surgery room and room 4");
        var start=Guid.Parse("61000000-0000-0000-0000-000000000002");
        using var issue=await http.PostAsJsonAsync($"api/visits/{surgery.Visit.Id}/mobile-guide",new CreateMobileGuideRequest(start));issue.EnsureSuccessStatusCode();
        var ticket=(await issue.Content.ReadFromJsonAsync<MobileGuideTicket>())!;
        using var request=new HttpRequestMessage(HttpMethod.Get,"api/mobile-guide");request.Headers.Authorization=new("Bearer",new Uri(ticket.Url).Fragment[3..]);
        using var guide=await http.SendAsync(request);guide.EnsureSuccessStatusCode();
        var mobile=(await guide.Content.ReadFromJsonAsync<MobileGuideResponse>())!;
        Check(mobile.Route.Legs.Count==2&&mobile.Route.Legs[1].Map.Code=="F2"&&mobile.Route.DestinationName==rooms[2].Name,"Mobile QR for surgery returns both floor maps and actual room 3 destination");
        time=time.AddSeconds(10);display.TickPage();
        Check(display.VisibleRooms[0].RoomId==rooms[0].Id,"Real DID returns to first-floor pair after twenty seconds");
        var race=await Task.WhenAll(Register(rooms[1],3),Register(rooms[3],3));
        Check(race[0].Visit.Id==race[1].Visit.Id&&race.Count(x=>x.IsExisting)==1,"Concurrent cross-department requests create exactly one active visit");
        using(var startService=await http.PostAsJsonAsync($"api/visits/{surgery.Visit.Id}/start",new TransitionRequest(2)))startService.EnsureSuccessStatusCode();
        var inServiceDuplicate=await Register(rooms[0],2);
        Check(inServiceDuplicate.IsExisting&&inServiceDuplicate.Visit.Id==surgery.Visit.Id&&inServiceDuplicate.Visit.Status=="InService","In-service surgery visit blocks duplicate registration across departments");
        Console.WriteLine("ALL TWO-FLOOR INTEGRATION TESTS PASSED");
    }
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS: "+label);}
    private static async Task Until(Func<bool> value){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!value())await Task.Delay(25,timeout.Token);}
}
