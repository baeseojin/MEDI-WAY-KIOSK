namespace MediWay.Contracts;

public sealed record QueueItem(Guid VisitId, int QueueNumber, string Status, int Version, DateTime CreatedAt);
public sealed record QueueResponse(Guid RoomId, DateOnly ServiceDate, IReadOnlyList<QueueItem> Items);
