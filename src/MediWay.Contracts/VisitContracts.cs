namespace MediWay.Contracts;

public sealed record PatientResponse(Guid Id, string PatientNumber, string DisplayName);
public sealed record RoomResponse(Guid Id, Guid DepartmentId, string DepartmentName, string Code, string Name, string? FloorCode = null, string? FloorName = null);
public sealed record CreateVisitRequest(Guid PatientId, Guid RoomId, Guid RequestId, DateOnly? ExpectedServiceDate = null);
public sealed record VisitResponse(Guid Id, Guid PatientId, Guid DepartmentId, Guid RoomId,
    DateOnly ServiceDate, int QueueNumber, string Status, int Version, DateTime CreatedAt);
public sealed record CreateVisitResponse(VisitResponse Visit, bool IsExisting);
public sealed record ApiError(string Code, string Message);
