namespace MediWay.Contracts;

public sealed record QueueChanged(Guid EventId, Guid RoomId, Guid VisitId, int VisitVersion, string Status, DateTime OccurredAt);
public sealed record DisplayCurrent(Guid VisitId, int QueueNumber, string Status, int Version, DateOnly ServiceDate);
public sealed record DisplayRoomSnapshot(Guid RoomId, string RoomName, string DepartmentName, DisplayCurrent? Current, IReadOnlyList<QueueItem> Waiting, string? FloorCode = null, string? FloorName = null);
public sealed record DisplaySnapshot(DateTime AsOf, DateOnly ServiceDate, IReadOnlyList<DisplayRoomSnapshot> Rooms);
