namespace MediWay.Contracts;

public sealed record DependencyStatus(string Status);
public sealed record HealthResponse(string Status, DependencyStatus PostgreSql, DependencyStatus Redis);
