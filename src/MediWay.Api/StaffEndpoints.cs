using MediWay.Application.Reception;
using MediWay.Domain.Reception;
using MediWay.Contracts;
using Npgsql;

namespace MediWay.Api;

public static class StaffEndpoints
{
    private const string Columns = "id,patient_id,department_id,room_id,service_date,queue_number,status,version,created_at";

    public static void MapStaffEndpoints(this WebApplication app)
    {
        app.MapGet("/api/rooms/{roomId:guid}/current", async (Guid roomId, NpgsqlDataSource db, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await using var exists = db.CreateCommand("SELECT EXISTS(SELECT 1 FROM rooms WHERE id=@room)");
            exists.Parameters.AddWithValue("room", roomId);
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
                return Results.NotFound(new ApiError("ROOM_NOT_FOUND", "진료실을 찾을 수 없습니다."));
            // Include previous-day active visits so they cannot silently disappear and block the room.
            await using var command = db.CreateCommand($"SELECT {Columns} FROM visits WHERE room_id=@room AND status IN ('Called','InService')");
            command.Parameters.AddWithValue("room", roomId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var current = await reader.ReadAsync(ct) ? Read(reader) : null;
            return Results.Ok(new RoomCurrentResponse(roomId, current));
        });
        app.MapPost("/api/visits/{id:guid}/call", (Guid id, TransitionRequest request, TransitionVisitService service, CancellationToken ct) => Change(id, request, VisitAction.Call, service, ct));
        app.MapPost("/api/visits/{id:guid}/start", (Guid id, TransitionRequest request, TransitionVisitService service, CancellationToken ct) => Change(id, request, VisitAction.StartConsultation, service, ct));
        app.MapPost("/api/visits/{id:guid}/complete", (Guid id, TransitionRequest request, TransitionVisitService service, CancellationToken ct) => Change(id, request, VisitAction.Complete, service, ct));
        app.MapPost("/api/visits/{id:guid}/cancel", (Guid id, TransitionRequest request, TransitionVisitService service, CancellationToken ct) => Change(id, request, VisitAction.Cancel, service, ct));
    }
    private static async Task<IResult> Change(Guid id, TransitionRequest request, VisitAction action, TransitionVisitService service, CancellationToken ct)
    {
        var result=await service.ExecuteAsync(new(id,request.ExpectedVersion,action),ct);
        if (result.Error is not { } error) return Results.Ok(ToResponse(result.Visit!));
        if (error.Code == "INVALID_REQUEST") return Results.BadRequest(new ApiError(error.Code,error.Message));
        if (error.Code == "VISIT_NOT_FOUND") return Results.NotFound(new ApiError(error.Code,error.Message));
        return Results.Conflict(new TransitionConflict(error.Code,error.Message,ToResponse(result.Visit!)));
    }
    internal static VisitResponse ToResponse(Visit visit) => new(visit.Id,visit.PatientId,visit.DepartmentId,visit.RoomId,
        visit.ServiceDate,visit.QueueNumber,visit.Status.ToString(),visit.Version,visit.CreatedAt);
    private static VisitResponse Read(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
        reader.GetFieldValue<DateOnly>(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7), reader.GetDateTime(8));
}