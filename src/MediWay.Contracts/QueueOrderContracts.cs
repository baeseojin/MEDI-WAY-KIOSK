namespace MediWay.Contracts;

public sealed record QueueOrderEntry(Guid VisitId, int Version);
public sealed record QueueOrderRequest(Guid RequestId, DateOnly ServiceDate,
    IReadOnlyList<QueueOrderEntry> Expected, IReadOnlyList<Guid> OrderedVisitIds);
