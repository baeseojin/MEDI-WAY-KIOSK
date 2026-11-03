using MediWay.Application.Reception;
using MediWay.Domain.Reception;
using Npgsql;

namespace MediWay.Infrastructure.Reception;

public sealed class PostgresVisitTransitionStore(NpgsqlDataSource db) : IVisitTransitionStore
{
    private const string Columns="id,patient_id,department_id,room_id,service_date,queue_number,status,version,created_at";
    public async Task<TransitionResult> ExecuteAsync(Guid id, Func<Visit, DateOnly, bool, TransitionDecision> decide, CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        NpgsqlCommand Command(string sql) => new(sql,connection,transaction) { CommandTimeout=5 };
        TransitionResult Missing() => new(null,new("VISIT_NOT_FOUND","접수를 찾을 수 없습니다."));
        Guid roomId;
        await using (var command=Command("SELECT room_id FROM visits WHERE id=@id"))
        {
            command.Parameters.AddWithValue("id",id);
            if (await command.ExecuteScalarAsync(ct) is not Guid room) return Missing();
            roomId=room;
        }
        await using (var command=Command("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))"))
        {
            command.Parameters.AddWithValue("key",$"room-state:{roomId:D}");
            await command.ExecuteNonQueryAsync(ct);
        }
        Visit current;
        await using (var command=Command($"SELECT {Columns} FROM visits WHERE id=@id FOR UPDATE"))
        {
            command.Parameters.AddWithValue("id",id);
            await using var reader=await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Missing();
            current=Read(reader);
        }
        DateOnly today;
        await using (var command=Command("SELECT (clock_timestamp() AT TIME ZONE 'Asia/Seoul')::date"))
            today=(DateOnly)(await command.ExecuteScalarAsync(ct))!;
        bool busy;
        await using (var command=Command("SELECT EXISTS(SELECT 1 FROM visits WHERE room_id=@room AND status IN ('Called','InService'))"))
        {
            command.Parameters.AddWithValue("room",roomId);
            busy=(bool)(await command.ExecuteScalarAsync(ct))!;
        }
        var expectedVersion=current.Version;
        var decision=decide(current,today,busy);
        if (decision.Error is { } error) return new(current,error);
        if (decision.IsReplay)
        {
            await transaction.CommitAsync(ct);
            return new(current,IsReplay:true);
        }
        var timestamp=current.Status switch
        {
            VisitStatus.Called => "called_at", VisitStatus.InService => "started_at",
            VisitStatus.Completed => "completed_at", VisitStatus.Cancelled => "cancelled_at",
            _ => throw new InvalidOperationException("No persisted transition.")
        };
        await using (var command=Command($"UPDATE visits SET status=@status,version=@next,{timestamp}=clock_timestamp() WHERE id=@id AND version=@version"))
        {
            command.Parameters.AddWithValue("status",current.Status.ToString());
            command.Parameters.AddWithValue("next",current.Version);
            command.Parameters.AddWithValue("id",id);
            command.Parameters.AddWithValue("version",expectedVersion);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Locked visit update failed.");
        }
        await transaction.CommitAsync(ct);
        return new(current,Event:decision.Event);
    }
    private static Visit Read(NpgsqlDataReader reader) => Visit.Restore(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetGuid(3),
        reader.GetFieldValue<DateOnly>(4),reader.GetInt32(5),Enum.Parse<VisitStatus>(reader.GetString(6)),reader.GetInt32(7),reader.GetDateTime(8));
}