namespace NexaOne.ServiceContracts.Fdc;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단). bool/enum류는 평탄화한다.
// 비-실시간 설정 생성 op가 반환하는 단일 애그리거트 스냅샷 — 컨트롤러는 이 형태로 응답한다.
// 실시간 수집/평가/이력 행은 본 계약에 포함하지 않는다(워커 소유, ADR-006).

public record FdcParameterGroupDto(
    string GroupId, string GroupName, string EquipmentId, string? Description, int DisplayOrder, bool IsActive);

public record FdcAlarmConfigDto(
    string AlarmConfigId, string EquipmentId, string ParameterId, string AlarmLevel, string Operator,
    decimal ThresholdValue, bool IsActive);

public record FdcInterlockRuleDto(
    string RuleId, string RuleName, string EquipmentId, string ParameterId, string Operator,
    decimal ThresholdValue, string Action, int Priority, bool IsActive);
