using System.Security.Cryptography;
using System.Text;
using MediWay.Domain.Reception;
namespace MediWay.Application.Reception;

public sealed record RegisterVisitCommand(Guid PatientId, Guid RoomId, Guid RequestId, DateOnly? ExpectedServiceDate = null);
public sealed record RegistrationResult(Visit? Visit, bool IsExisting = false, RuleViolation? Error = null);
public interface IRegistrationStore
{
    // Request ledger replay precedes date validation. Patient/day uniqueness and numbering share one transaction.
    Task<RegistrationResult> RegisterAsync(RegisterVisitCommand request, string fingerprint, CancellationToken ct);
}
public sealed class RegisterVisitService(IRegistrationStore store, IVisitNotifications notifications)
{
    public async Task<RegistrationResult> ExecuteAsync(RegisterVisitCommand request, CancellationToken ct)
    {
        if (request.PatientId == Guid.Empty || request.RoomId == Guid.Empty || request.RequestId == Guid.Empty)
            return new(null,Error:new("INVALID_REQUEST","환자·진료실·요청 ID가 필요합니다."));
        var payload=$"{request.PatientId:D}|{request.RoomId:D}" + (request.ExpectedServiceDate is { } expected ? $"|{expected:yyyy-MM-dd}" : "");
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var result=await store.RegisterAsync(request,fingerprint,ct);
        if (result.Error is null && result.Visit is { } visit)
        {
            await notifications.InvalidateAsync(visit.RoomId,visit.ServiceDate);
            if (!result.IsExisting) await notifications.PublishRegisteredAsync(visit);
        }
        return result;
    }
}