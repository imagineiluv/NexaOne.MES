using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.Mrp;

/// <summary>ADR-008 얇은 브리지 어댑터 — 호스트가 GetBean("Pom","mrpBridge")로 IMrpBridge 캐스트.
/// 모듈 소유 <see cref="IMrpPlanner"/>로 위임할 뿐, 계산/데이터 접근 로직은 모듈에 있다(OEE 브리지 선례).</summary>
public sealed class MrpBridge : IMrpBridge
{
    private readonly IMrpPlanner _planner;

    public MrpBridge(IMrpPlanner planner) => _planner = planner;

    public Task<MrpRunResult> RunAsync(string executedBy, MrpRunOptions? options = null, CancellationToken ct = default)
        => _planner.RunAsync(executedBy, options, ct);

    public Task<MrpConvertResult> ConvertAsync(
        string? runId, IReadOnlyList<string>? plannedOrderIds,
        IReadOnlyList<MrpProductionAssignment>? productionAssignments, string executedBy, CancellationToken ct = default)
        => _planner.ConvertAsync(runId, plannedOrderIds, productionAssignments, executedBy, ct);
}
