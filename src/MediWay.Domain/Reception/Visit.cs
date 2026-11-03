namespace MediWay.Domain.Reception;

public enum VisitStatus { Waiting, Called, InService, Completed, Cancelled }
public sealed record RuleViolation(string Code, string Message);
public sealed record VisitStatusChanged(Guid VisitId, Guid RoomId, DateOnly ServiceDate, VisitStatus Status, int Version);
public sealed record TransitionDecision(Visit Visit, RuleViolation? Error = null, bool IsReplay = false, VisitStatusChanged? Event = null);

// Aggregate root of the reception context. It has no dependency on HTTP, WPF, SQL or Redis.
public sealed class Visit
{
    public Guid Id { get; }
    public Guid PatientId { get; }
    public Guid DepartmentId { get; }
    public Guid RoomId { get; }
    public DateOnly ServiceDate { get; }
    public int QueueNumber { get; }
    public VisitStatus Status { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; }
    private Visit(Guid id, Guid patientId, Guid departmentId, Guid roomId, DateOnly serviceDate,
        int queueNumber, VisitStatus status, int version, DateTime createdAt)
    {
        if (id == Guid.Empty || patientId == Guid.Empty || departmentId == Guid.Empty || roomId == Guid.Empty)
            throw new ArgumentException("Visit identities must not be empty.");
        if (queueNumber <= 0 || version <= 0) throw new ArgumentOutOfRangeException(nameof(queueNumber));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Id=id; PatientId=patientId; DepartmentId=departmentId; RoomId=roomId; ServiceDate=serviceDate;
        QueueNumber=queueNumber; Status=status; Version=version; CreatedAt=createdAt;
    }
    public static Visit Register(Guid id, Guid patientId, Guid departmentId, Guid roomId, DateOnly date, int queueNumber, DateTime createdAt) =>
        new(id,patientId,departmentId,roomId,date,queueNumber,VisitStatus.Waiting,1,createdAt);
    public static Visit Restore(Guid id, Guid patientId, Guid departmentId, Guid roomId, DateOnly serviceDate,
        int queueNumber, VisitStatus status, int version, DateTime createdAt) =>
        new(id, patientId, departmentId, roomId, serviceDate, queueNumber, status, version, createdAt);
    public TransitionDecision Call(int expectedVersion, DateOnly today, bool roomBusy) => Change(VisitStatus.Called, expectedVersion, today, roomBusy);
    public TransitionDecision StartConsultation(int expectedVersion) => Change(VisitStatus.InService, expectedVersion, ServiceDate, false);
    public TransitionDecision Complete(int expectedVersion) => Change(VisitStatus.Completed, expectedVersion, ServiceDate, false);
    public TransitionDecision Cancel(int expectedVersion) => Change(VisitStatus.Cancelled, expectedVersion, ServiceDate, false);

    private TransitionDecision Change(VisitStatus target, int expectedVersion, DateOnly today, bool roomBusy)
    {
        TransitionDecision Reject(string code, string message) => new(this, new(code, message));
        if (expectedVersion <= 0) return Reject("INVALID_REQUEST", "유효한 접수 ID와 현재 버전이 필요합니다.");
        // Check response-loss retries before date/occupancy: a successful call is now itself occupying the room.
        if (Status == target && Version == (long)expectedVersion + 1) return new(this, IsReplay: true);
        if (Version != expectedVersion) return Reject("VERSION_CONFLICT", "다른 화면에서 접수가 변경되었습니다. 최신 목록을 확인해 주세요.");
        var allowed = target switch
        {
            VisitStatus.Called => Status == VisitStatus.Waiting,
            VisitStatus.InService => Status == VisitStatus.Called,
            VisitStatus.Completed => Status == VisitStatus.InService,
            VisitStatus.Cancelled => Status is VisitStatus.Waiting or VisitStatus.Called,
            _ => false
        };
        if (!allowed) return Reject("INVALID_TRANSITION", "현재 접수 상태에서는 이 작업을 수행할 수 없습니다.");
        if (target == VisitStatus.Called && today != ServiceDate) return Reject("PAST_VISIT", "오늘 접수만 새로 호출할 수 있습니다.");
        if (target == VisitStatus.Called && roomBusy) return Reject("ROOM_BUSY", "이미 호출했거나 진료 중인 접수가 있습니다.");
        var nextVersion = checked(Version + 1);
        Status=target; Version=nextVersion;
        return new(this, Event: new(Id, RoomId, ServiceDate, Status, Version));
    }
}