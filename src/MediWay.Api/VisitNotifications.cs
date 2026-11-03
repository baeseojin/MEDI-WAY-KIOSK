using MediWay.Application.Reception;
using MediWay.Domain.Reception;
using MediWay.Contracts;
namespace MediWay.Api;

// Adapter at the API composition boundary. Domain/application never reference Redis or SignalR.
public sealed class VisitNotifications(QueueService queue, QueueEvents events) : IVisitNotifications
{
    public Task InvalidateAsync(Guid roomId, DateOnly date) => queue.InvalidateAsync(roomId,date);
    public Task PublishRegisteredAsync(Visit visit) => events.PublishAsync(StaffEndpoints.ToResponse(visit));
    public Task PublishAsync(VisitStatusChanged changed) => events.PublishStatusChangedAsync(changed.VisitId,changed.RoomId,changed.Version,changed.Status.ToString());
}