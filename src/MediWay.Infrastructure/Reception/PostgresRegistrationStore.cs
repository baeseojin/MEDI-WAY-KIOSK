using MediWay.Application.Reception;
using MediWay.Domain.Reception;
using Npgsql;
namespace MediWay.Infrastructure.Reception;

public sealed class PostgresRegistrationStore(NpgsqlDataSource db) : IRegistrationStore
{
    private const string Columns="id,patient_id,department_id,room_id,service_date,queue_number,status,version,created_at";
    public async Task<RegistrationResult> RegisterAsync(RegisterVisitCommand request, string fingerprint, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        NpgsqlCommand Command(string sql)
        {
            // Keep every command in the same connection and transaction.
            var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("request", request.RequestId);
            command.Parameters.AddWithValue("patient", request.PatientId);
            command.Parameters.AddWithValue("room", request.RoomId);
            return command;
        }

        // Lock order: request key -> patient/day -> department/day -> room. Locks release on commit/rollback.
        await using (var command = Command("SELECT pg_advisory_xact_lock(hashtextextended('request:' || @request::text,0))"))
            await command.ExecuteNonQueryAsync(ct);

        async Task<(string Fingerprint, Guid VisitId)?> ExistingRequest()
        {
            await using var command = Command("SELECT request_fingerprint,visit_id FROM visit_requests WHERE request_id=@request");
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? (reader.GetString(0),reader.GetGuid(1)) : null;
        }
        async Task<Visit> GetVisit(Guid id)
        {
            await using var command = Command($"SELECT {Columns} FROM visits WHERE id=@visit");
            command.Parameters.AddWithValue("visit", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Request references missing visit.");
            return Read(reader);
        }
        if (await ExistingRequest() is { } previous)
        {
            if (previous.Fingerprint != fingerprint)
                return new(null,Error:new("REQUEST_KEY_CONFLICT", "같은 요청 ID를 다른 입력에 사용할 수 없습니다."));
            var existing = await GetVisit(previous.VisitId);
            await transaction.CommitAsync(ct);
            return new(existing,true);
        }

        Guid departmentId;
        await using (var command = Command("SELECT department_id FROM rooms WHERE id=@room"))
        {
            if (await command.ExecuteScalarAsync(ct) is not Guid department)
                return new(null,Error:new("ROOM_NOT_FOUND", "진료실을 찾을 수 없습니다."));
            departmentId = department;
        }
        await using (var command = Command("SELECT EXISTS(SELECT 1 FROM patients WHERE id=@patient)"))
            if (!(bool)(await command.ExecuteScalarAsync(ct))!)
                return new(null,Error:new("PATIENT_NOT_FOUND", "환자를 찾을 수 없습니다."));

        // Compute after opening the transaction; do not trust a terminal's local clock.
        DateOnly serviceDate;
        await using (var command = Command("SELECT (clock_timestamp() AT TIME ZONE 'Asia/Seoul')::date"))
            serviceDate = (DateOnly)(await command.ExecuteScalarAsync(ct))!;
        // Replay above stays valid across midnight; an unsent old-day request must not create a new-day visit.
        if (RegistrationPolicy.ValidateDate(request.ExpectedServiceDate, serviceDate) is { } dateError)
            return new(null,Error:dateError);
        // Different departments share the patient/day lock before department/day and room locks.
        await using (var patientLock = Command("SELECT pg_advisory_xact_lock(hashtextextended(@patientlock,0))"))
        {
            patientLock.Parameters.AddWithValue("patientlock", $"patient-day:{request.PatientId:D}:{serviceDate:yyyy-MM-dd}");
            await patientLock.ExecuteNonQueryAsync(ct);
        }
        await using (var command = Command("SELECT pg_advisory_xact_lock(hashtextextended(@lock,0))"))
        {
            command.Parameters.AddWithValue("lock", $"queue:{departmentId:D}:{serviceDate:yyyy-MM-dd}");
            await command.ExecuteNonQueryAsync(ct);
        }

        Visit? visit = null;
        await using (var command = Command($"SELECT {Columns} FROM visits WHERE patient_id=@patient AND service_date=@date AND status IN ('Waiting','Called','InService')"))
        {
            command.Parameters.AddWithValue("date", serviceDate);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var candidate=Read(reader);
                if (!RegistrationPolicy.ReuseExisting(new(request.PatientId,serviceDate),candidate))
                    throw new InvalidOperationException("Active visit query violated registration policy.");
                visit=candidate;
            }
        }
        var isExisting = visit is not null;
        if (visit is null)
        {
            // Serialize membership changes with staff state transitions and queue reordering.
            // Lock order is request -> patient/day -> department/day -> room.
            await using (var roomLock = Command("SELECT pg_advisory_xact_lock(hashtextextended(@roomlock,0))"))
            {
                roomLock.Parameters.AddWithValue("roomlock", $"room-state:{request.RoomId:D}");
                await roomLock.ExecuteNonQueryAsync(ct);
            }
            int number;
            DateTime createdAt;
            await using (var next=Command("SELECT COALESCE(MAX(queue_number),0)+1,clock_timestamp() FROM visits WHERE department_id=@department AND service_date=@date"))
            {
                next.Parameters.AddWithValue("department",departmentId);
                next.Parameters.AddWithValue("date",serviceDate);
                await using var reader=await next.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct); number=reader.GetInt32(0); createdAt=reader.GetDateTime(1);
            }
            visit=Visit.Register(Guid.NewGuid(),request.PatientId,departmentId,request.RoomId,serviceDate,number,createdAt);
            await using var command=Command("""
                INSERT INTO visits(id,patient_id,department_id,room_id,service_date,queue_number,status,version,created_at,request_id,request_fingerprint)
                VALUES(@id,@patient,@department,@room,@date,@number,@status,@version,@created,@request,@fingerprint)
                """);
            command.Parameters.AddWithValue("id",visit.Id);
            command.Parameters.AddWithValue("department",departmentId);
            command.Parameters.AddWithValue("date",serviceDate);
            command.Parameters.AddWithValue("number",visit.QueueNumber);
            command.Parameters.AddWithValue("status",visit.Status.ToString());
            command.Parameters.AddWithValue("version",visit.Version);
            command.Parameters.AddWithValue("created",visit.CreatedAt);
            command.Parameters.AddWithValue("fingerprint",fingerprint);
            await command.ExecuteNonQueryAsync(ct);
        }
        // Record duplicate-active requests too: retries must keep referring to this visit.
        await using (var command = Command("INSERT INTO visit_requests(request_id,request_fingerprint,visit_id) VALUES(@request,@fingerprint,@visit)"))
        {
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("visit", visit.Id);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);

        return new(visit,isExisting);
    }
    private static Visit Read(NpgsqlDataReader reader) => Visit.Restore(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetGuid(3),
        reader.GetFieldValue<DateOnly>(4),reader.GetInt32(5),Enum.Parse<VisitStatus>(reader.GetString(6)),reader.GetInt32(7),reader.GetDateTime(8));
}