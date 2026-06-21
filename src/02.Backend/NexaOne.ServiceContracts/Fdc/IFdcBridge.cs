using NexaOne.Common;

namespace NexaOne.ServiceContracts.Fdc;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — FDC 비-실시간 설정 관리 단일 애그리거트 쓰기
/// (파라미터그룹 + 임계치 알람설정 + 인터락규칙 생성). plugin(FDC)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. Result로 팩토리 검증(레벨/연산자/액션/한도)을 분기(Validation/Conflict/NotFound/Success)한다.
///
/// 워커 가드(ADR-006): 실시간 수집(FdcCollectorService/FdcCollectionWorker·OPC-UA 구독)·알람 평가(EvaluateAsync)·
/// 인터락 평가(EvaluateAsync)·발생/해제 이력 기록(RecordAsync/ClearActiveAsync/ResolveActiveAsync)·수집데이터 기록
/// (RecordDataAsync)은 하드웨어/라이브/배경 경로라 워커가 소유하므로 본 브리지는 절대 위임하지 않는다(REST 비노출).
/// 순수 설정·이력 조회는 게이트웨이(FDC.xml)로 노출한다.</summary>
public interface IFdcBridge
{
    // ── 파라미터그룹(FdcParameterGroup) 생성 — 단일 애그리거트, 비-실시간 설정 ──
    Task<Result<FdcParameterGroupDto>> CreateParameterGroupAsync(
        string groupId, string groupName, string equipmentId, string? description, int displayOrder, CancellationToken ct = default);

    // ── 임계치 알람설정(FdcAlarmConfig) 생성 — 단일 애그리거트, 비-실시간 설정 ──
    Task<Result<FdcAlarmConfigDto>> CreateAlarmConfigAsync(
        string alarmConfigId, string equipmentId, string parameterId, string alarmLevel, string @operator,
        decimal threshold, CancellationToken ct = default);

    // ── 인터락규칙(FdcInterlockRule) 생성 — 단일 애그리거트, 비-실시간 설정(평가/발동은 워커 소유) ──
    Task<Result<FdcInterlockRuleDto>> CreateInterlockRuleAsync(
        string ruleId, string ruleName, string equipmentId, string parameterId, string @operator,
        decimal threshold, string action, int priority, CancellationToken ct = default);
}
