using MediWay.Domain.Reception;

namespace MediWay.Application.Reception;

public enum VisitAction { Call, StartConsultation, Complete, Cancel }
public sealed record TransitionVisitCommand(Guid VisitId, int ExpectedVersion, VisitAction Action);
public sealed record TransitionResult(Visit? Visit, RuleViolation? Error = null, bool IsReplay = false, VisitStatusChanged? Event = null);

public interface IVisitTransitionStore
{
    // Implementations must hold room + visit locks through decision and persistence, and commit before returning.
    Task<TransitionResult> ExecuteAsync(Guid id, Func<Visit, DateOnly, bool, TransitionDecision> decide, CancellationToken ct);
}
public interface IVisitNotifications
{
    Task InvalidateAsync(Guid roomId, DateOnly date);
    Task PublishAsync(VisitStatusChanged changed);
    Task PublishRegisteredAsync(Visit visit);
}

public sealed class TransitionVisitService(IVisitTransitionStore store, IVisitNotifications notifications)
{
    public async Task<TransitionResult> ExecuteAsync(TransitionVisitCommand command, CancellationToken ct)
    {
        if (command.VisitId == Guid.Empty || command.ExpectedVersion <= 0 || !Enum.IsDefined(command.Action))
            return new(null, new("INVALID_REQUEST", "유효한 접수 ID와 현재 버전이 필요합니다."));
        var result = await store.ExecuteAsync(command.VisitId, (visit, today, busy) => command.Action switch
        {
            VisitAction.Call => visit.Call(command.ExpectedVersion, today, busy),
            VisitAction.StartConsultation => visit.StartConsultation(command.ExpectedVersion),
            VisitAction.Complete => visit.Complete(command.ExpectedVersion),
            VisitAction.Cancel => visit.Cancel(command.ExpectedVersion),
            _ => throw new InvalidOperationException()
        }, ct);
        if (result.Error is null && result.Visit is { } updated)
        {
            // Nothing is sent on rejected commands; replays invalidate but never announce twice.
            await notifications.InvalidateAsync(updated.RoomId, updated.ServiceDate);
            if (result.Event is { } changed) await notifications.PublishAsync(changed);
        }
        return result;
    }
}