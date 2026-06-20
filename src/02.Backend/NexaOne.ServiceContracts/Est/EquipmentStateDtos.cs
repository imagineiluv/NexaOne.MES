namespace NexaOne.ServiceContracts.Est;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않기 위한 경량 DTO(ALC/버전 결합 차단). 엔티티 컬럼과 1:1.
public record EquipmentStateDto(
    string EquipmentId, string PlantId, string CurrentStateId, DateTime StateChangedAt, int StateVersion);

public record EquipmentStateMatrixDto(
    string Id, string PlantId, string FromStateId, string ToStateId,
    bool AllowFlag, string SetStateId, bool RequireReason, string ValidState);

public record EquipmentStateHistoryDto(
    string HistoryId, string EquipmentId, string FromState, string ToState, string SetState,
    DateTime ChangedAt, string ChangedBy, string Reason, string SourceType, long? DurationSeconds);
