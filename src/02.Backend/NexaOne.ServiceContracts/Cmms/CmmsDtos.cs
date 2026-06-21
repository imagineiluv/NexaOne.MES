namespace NexaOne.ServiceContracts.Cmms;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단). Status enum은 string으로 평탄화.
// 생성 연산이 반환하는 단일 애그리거트 스냅샷 — 컨트롤러는 이 형태로 응답한다.

public record WorkOrderDto(
    string WoId, string? PlanId, string EquipmentId, string WoType, string Description,
    string AssigneeId, DateTime IssuedAt, DateTime? StartedAt, DateTime? CompletedAt,
    string Status, string? FailureCodeId, string? Remark);

public record MaintenancePlanDto(
    string PlanId, string PlanName, string EquipmentId, string PlanType, string CycleType,
    DateTime ScheduledDate, decimal EstimatedDurationHours, string AssigneeId, string Status);

public record SparePartDto(
    string PartId, string PartName, string PartNumber, string Description, string UnitOfMeasure,
    decimal CurrentStock, decimal MinStock, decimal MaxStock, string Location,
    string? EquipmentClassId, bool IsLowStock);
