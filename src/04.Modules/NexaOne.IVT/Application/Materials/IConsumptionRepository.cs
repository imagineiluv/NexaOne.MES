using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Application.Materials;

public interface IConsumptionRepository
{
    Task<MaterialLotBalance?> GetLotAsync(string materialLotId, CancellationToken ct = default);
    Task<ConsumptionRecord?> GetByIdAsync(string consumptionId, CancellationToken ct = default);
    Task<ConsumptionRecord?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<ConsumptionRecord?> GetBySourceEventAsync(
        string sourceSystem,
        string sourceEventId,
        CancellationToken ct = default);
    Task<bool> PersistAsync(ConsumptionRecord record, CancellationToken ct = default);
    Task<bool> PersistReversalAsync(
        ConsumptionRecord original,
        ConsumptionRecord reversal,
        string reason,
        CancellationToken ct = default);
}
