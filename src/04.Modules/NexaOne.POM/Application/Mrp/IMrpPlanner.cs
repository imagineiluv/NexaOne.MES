using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.Mrp;

/// <summary>MRP v1 실행 포트(모듈 소유) — 원자료 적재→순수 계산(MrpCalculator)→결과 영속(MRP_RUN/
/// MRP_PLANNED_ORDER, append-only)을 한 번에 수행한다. 구현은 Infrastructure.MrpPlanningRepository.</summary>
public interface IMrpPlanner
{
    Task<MrpRunResult> RunAsync(string executedBy, CancellationToken ct = default);
}
