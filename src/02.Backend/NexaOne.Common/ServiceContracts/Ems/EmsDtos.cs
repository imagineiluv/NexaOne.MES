namespace NexaOne.ServiceContracts.Ems;

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

/// <summary>
/// EMS 쓰기 명령의 인증·재시도·추적 문맥. ActorId는 서버가 ClaimsPrincipal에서 채우며 요청 body actor를
/// 신뢰하지 않는다.
/// </summary>
public sealed record EmsCommandContextDto(
    string ActorId,
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null);

/// <summary>재고 증감과 원장에 함께 보존할 예비부품 추적 문맥.</summary>
public sealed record SparePartAdjustmentDto(
    decimal Delta,
    EmsCommandContextDto Command,
    string? TransactionType = null,
    string? WorkOrderId = null,
    string? EquipmentId = null,
    string? FromLocation = null,
    string? ToLocation = null,
    string? Remark = null,
    string? BomItemId = null);
