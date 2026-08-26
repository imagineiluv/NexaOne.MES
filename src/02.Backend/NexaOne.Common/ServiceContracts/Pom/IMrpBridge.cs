namespace NexaOne.ServiceContracts.Pom;

/// <summary>MRP 실행 결과 요약 — 실행 이력(MRP_RUN)의 미러. 제안 상세는 명명 쿼리
/// (POM.MrpPlannedOrderList)로 조회한다.</summary>
public sealed record MrpRunResult(
    string RunId,
    string Status,              // Success | Failed
    int DemandCount,
    int PlannedOrderCount,
    string? Message);

/// <summary>생산 제안별 설비 배정. 공장·품목·수량·일자는 서버의 MRP 제안에서만 읽는다.</summary>
public sealed record MrpProductionAssignment(string PlannedOrderId, string EquipmentId);

/// <summary>계획오더 전환 결과. Purchase는 구매오더, Production은 생산계획과
/// 생산관리지시를 생성하며 공정 작업지시는 생성하지 않는다.</summary>
public sealed record MrpConvertResult(
    string RunId,
    int Converted,
    int PurchaseOrders,
    int ProductionOrders,
    string? Message);

/// <summary>얇은 브리지(ADR-008) — MRP 소요량 전개 실행/실오더 전환 트리거. plugin(POM)의 IMrpPlanner를
/// 호스트가 GetBean→캐스트로 Default-ALC DI에 등록해 얇은 컨트롤러(POST /api/v1/pom/mrp/{run,convert})가
/// 호출한다. 실행은 append-only(MRP_RUN + MRP_PLANNED_ORDER 런별 보존) — 원자료는 건드리지 않는다.
/// 전환은 실오더 INSERT + 제안 Converted 마킹 전 문장을 단일 트랜잭션으로 커밋한다(부분 커밋 불가).</summary>
/// <summary>실행 옵션(v2 3단 — 기간 버킷). null=총량 넷팅(v1 동작 그대로).
/// BucketDays=버킷 크기(일, 기본 7=주간), HorizonBuckets=버킷 수(기본 8). 제안은 품목×버킷 다행이
/// 되고 DUE_DATE=버킷 시작일이 버킷을 식별한다(스키마 무변경).</summary>
public sealed record MrpRunOptions(int BucketDays = 7, int HorizonBuckets = 8);

[NexaModuleBridge("Pom", "mrpBridge")]
public interface IMrpBridge : INexaModuleBridge
{
    Task<MrpRunResult> RunAsync(string executedBy, MrpRunOptions? options = null, CancellationToken ct = default);

    /// <summary>runId의 Proposed 제안을 실오더로 전환(runId null=최신 실행). plannedOrderIds 지정 시
    /// 해당 제안만(행 선택 전환 UX), null=전량. 멱등 — 재호출 시 대상 0건.</summary>
    Task<MrpConvertResult> ConvertAsync(
        string? runId, IReadOnlyList<string>? plannedOrderIds,
        IReadOnlyList<MrpProductionAssignment>? productionAssignments, string executedBy, CancellationToken ct = default);
}
