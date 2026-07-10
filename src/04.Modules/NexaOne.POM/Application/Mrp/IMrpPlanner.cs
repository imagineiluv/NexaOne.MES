using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.Mrp;

/// <summary>MRP v1 실행 포트(모듈 소유) — 원자료 적재→순수 계산(MrpCalculator)→결과 영속(MRP_RUN/
/// MRP_PLANNED_ORDER, append-only)을 한 번에 수행한다. 구현은 Infrastructure.MrpPlanningRepository.</summary>
public interface IMrpPlanner
{
    Task<MrpRunResult> RunAsync(string executedBy, MrpRunOptions? options = null, CancellationToken ct = default);

    /// <summary>Proposed 제안→실오더 전환(v2 1단) — 전 문장 단일 트랜잭션(MixingPersistAsync 패턴).</summary>
    Task<MrpConvertResult> ConvertAsync(string? runId, IReadOnlyList<string>? plannedOrderIds, string executedBy, CancellationToken ct = default);
}
