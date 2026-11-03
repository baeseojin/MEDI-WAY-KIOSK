namespace MediWay.Contracts;

public sealed record CreateMobileGuideRequest(Guid StartNodeId);
public sealed record MobileGuideTicket(string Url, DateTimeOffset ExpiresAt, string QrPngBase64, bool IsLocalOnly);
public sealed record MobileGuideResponse(NavigationRoute Route, DateTimeOffset ExpiresAt, int QueueNumber, string VisitStatus);
