namespace MediWay.Contracts;

public sealed record TransitionRequest(int ExpectedVersion);
public sealed record TransitionConflict(string Code, string Message, VisitResponse Current);
public sealed record RoomCurrentResponse(Guid RoomId, VisitResponse? Visit);
