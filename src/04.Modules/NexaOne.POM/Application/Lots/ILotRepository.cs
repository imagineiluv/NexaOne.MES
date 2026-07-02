using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.Lots;

public interface ILotRepository
{
    Task<Lot?> GetByIdAsync(string lotId, CancellationToken ct = default);
    Task<IReadOnlyList<Lot>> GetByPlantAsync(string plantId, string? state = null, CancellationToken ct = default);
    Task<IReadOnlyList<Lot>> GetByWorkOrderAsync(string workOrderId, CancellationToken ct = default);
    Task AddAsync(Lot lot, CancellationToken ct = default);
    Task UpdateAsync(Lot lot, CancellationToken ct = default);

    /// <summary>Mixing 결과 일괄 영속(DATA-3 원자화) — 투입 Lot 소비 UPDATE + 혼합관계/이력 INSERT + 출력 Lot
    /// INSERT/UPDATE 전 문장을 단일 트랜잭션(ExecuteManyAsync)으로 커밋한다. 어느 문장이 실패해도 전체 롤백되어
    /// 부분 커밋(투입만 소비되고 출력 미생성 등)이 불가능하다. outbox 활성 시 도메인 이벤트도 같은 트랜잭션에 기록.</summary>
    Task MixingPersistAsync(MixingPersistPlan plan, CancellationToken ct = default);
}

/// <summary>Mixing 영속 계획 — 서비스가 도메인 전이를 전부 in-memory로 끝낸 뒤 최종 상태만 담아 넘긴다.
/// Histories는 전이 시점 순서대로(투입 Consume → 출력 TrackIn → TrackOut → Finish) 캡처된 스냅샷.</summary>
public sealed record MixingPersistPlan(
    IReadOnlyList<Lot> ConsumedInputs,
    Lot Output,
    bool IsNewOutput,
    IReadOnlyList<LotHistory> Histories,
    IReadOnlyList<LotMixingRelation> Relations);

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
