using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.Lots;

public interface ILotRepository
{
    Task<Lot?> GetByIdAsync(string lotId, CancellationToken ct = default);
    Task<IReadOnlyList<Lot>> GetByPlantAsync(string plantId, string? state = null, CancellationToken ct = default);
    Task<IReadOnlyList<Lot>> GetByWorkOrderAsync(string workOrderId, CancellationToken ct = default);
    Task AddAsync(Lot lot, CancellationToken ct = default);
    Task UpdateAsync(Lot lot, CancellationToken ct = default);
}

public interface ILotHistoryRepository
{
    Task AddAsync(LotHistory history, CancellationToken ct = default);
    Task<IReadOnlyList<LotHistory>> GetByLotAsync(string plantId, string lotId, CancellationToken ct = default);
    /// <summary>생산 추적 보고서 (설계 19.4.8) — 필터 조합 조회, maxRows 상한.</summary>
    Task<IReadOnlyList<LotHistory>> SearchAsync(
        string plantId, string? lotId, string? equipmentId, string? processId,
        DateTime? from, DateTime? to, int maxRows, CancellationToken ct = default);
}

public interface ILotMixingRelationRepository
{
    Task AddAsync(LotMixingRelation relation, CancellationToken ct = default);
    Task<IReadOnlyList<LotMixingRelation>> GetByOutputLotAsync(string plantId, string outputLotId, CancellationToken ct = default);
}
