using MediWay.Application.Reception;
using MediWay.Contracts;
using Npgsql;

namespace MediWay.Api;

public static class VisitEndpoints
{
    private const string Columns = "id,patient_id,department_id,room_id,service_date,queue_number,status,version,created_at";

    public static void MapVisitEndpoints(this WebApplication app)
    {
        app.MapGet("/api/patients/by-number/{number}", async (string number, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var command = db.CreateCommand("SELECT id,patient_number,display_name FROM patients WHERE patient_number=@number");
            command.Parameters.AddWithValue("number", number.Trim().ToUpperInvariant());
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? Results.Ok(new PatientResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2)))
                : Results.NotFound(new ApiError("PATIENT_NOT_FOUND", "등록된 환자번호가 없습니다."));
        });
        app.MapGet("/api/rooms", async (NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var command = db.CreateCommand("SELECT r.id,r.department_id,d.name,r.code,r.name,f.code,f.name FROM rooms r JOIN departments d ON d.id=r.department_id LEFT JOIN map_floors f ON f.id=r.floor_id ORDER BY f.code NULLS LAST,r.code");
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rooms = new List<RoomResponse>();
            while (await reader.ReadAsync(ct)) rooms.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6)));
            return Results.Ok(rooms);
        });
        app.MapGet("/api/visits/{id:guid}", async (Guid id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var command = db.CreateCommand($"SELECT {Columns} FROM visits WHERE id=@id");
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Results.Ok(ReadVisit(reader))
                : Results.NotFound(new ApiError("VISIT_NOT_FOUND", "접수를 찾을 수 없습니다."));
        });
        app.MapPost("/api/visits", CreateVisit);
    }

    private static async Task<IResult> CreateVisit(CreateVisitRequest request, RegisterVisitService service, CancellationToken ct)
    {
        var result=await service.ExecuteAsync(new(request.PatientId,request.RoomId,request.RequestId,request.ExpectedServiceDate),ct);
        if (result.Error is { } error)
        {
            var payload=new ApiError(error.Code,error.Message);
            return error.Code switch
            {
                "INVALID_REQUEST" => Results.BadRequest(payload),
                "PATIENT_NOT_FOUND" or "ROOM_NOT_FOUND" => Results.NotFound(payload),
                _ => Results.Conflict(payload)
            };
        }
        var response=new CreateVisitResponse(StaffEndpoints.ToResponse(result.Visit!),result.IsExisting);
        return result.IsExisting ? Results.Ok(response) : Results.Created($"/api/visits/{result.Visit!.Id}",response);
    }
    private static VisitResponse ReadVisit(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetGuid(3),reader.GetFieldValue<DateOnly>(4),
        reader.GetInt32(5),reader.GetString(6),reader.GetInt32(7),reader.GetDateTime(8));
}