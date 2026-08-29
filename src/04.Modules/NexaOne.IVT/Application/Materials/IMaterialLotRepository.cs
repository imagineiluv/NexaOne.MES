using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Application.Materials;

public interface IMaterialLotRepository
{
    Task<MaterialLotState?> GetLotAsync(string lotId, CancellationToken ct = default);
    Task<MaterialLotTransaction?> GetByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<MaterialLotTransaction?> GetBySourceEventAsync(
        string sourceSystem, string sourceEventId, CancellationToken ct = default);
    Task<bool> HasFeedSessionReservationAsync(string lotId, CancellationToken ct = default);
    Task<bool> TryReceiveAsync(MaterialLotTransaction record, CancellationToken ct = default);
    Task<bool> TryApplyAsync(MaterialLotTransaction record, CancellationToken ct = default);
}
