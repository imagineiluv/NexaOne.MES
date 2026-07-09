namespace NexaOne.ServiceContracts.Pom;

/// <summary>MRP 실행 결과 요약 — 실행 이력(MRP_RUN)의 미러. 제안 상세는 명명 쿼리
/// (POM.MrpPlannedOrderList)로 조회한다.</summary>
public sealed record MrpRunResult(
    string RunId,
    string Status,              // Success | Failed
    int DemandCount,
    int PlannedOrderCount,
    string? Message);

/// <summary>얇은 브리지(ADR-008) — MRP v1 소요량 전개 실행 트리거. plugin(POM)의 IMrpPlanner를 호스트가
/// GetBean→캐스트로 Default-ALC DI에 등록해 얇은 컨트롤러(POST /api/v1/pom/mrp/run)가 호출한다.
/// 실행은 append-only(MRP_RUN + MRP_PLANNED_ORDER 런별 보존) — 원자료(수주/재고/오더)는 건드리지 않는다.</summary>
public interface IMrpBridge
{
    Task<MrpRunResult> RunAsync(string executedBy, CancellationToken ct = default);
}
