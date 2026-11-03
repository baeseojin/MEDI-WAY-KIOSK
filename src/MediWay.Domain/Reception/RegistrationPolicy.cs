namespace MediWay.Domain.Reception;

public readonly record struct PatientDay(Guid PatientId, DateOnly ServiceDate)
{
    public bool Contains(Visit visit) => visit.PatientId == PatientId && visit.ServiceDate == ServiceDate;
}

// Hospital-wide rule; the patient/day database lock and unique index enforce it across concurrent requests.
public static class RegistrationPolicy
{
    public static bool IsActive(VisitStatus status) => status is VisitStatus.Waiting or VisitStatus.Called or VisitStatus.InService;
    public static bool ReuseExisting(PatientDay patientDay, Visit visit) => patientDay.Contains(visit) && IsActive(visit.Status);
    public static RuleViolation? ValidateDate(DateOnly? expected, DateOnly today) => expected is { } day && day != today
        ? new("SERVICE_DATE_CHANGED", "진료일이 변경되었습니다. 환자번호를 다시 조회한 후 접수해 주세요.") : null;
}