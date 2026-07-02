namespace NexaOne.ServiceContracts.Pom;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단). Status/State enum은 string으로 평탄화.
// 생성/전이가 반환하는 단일 애그리거트 스냅샷 — 컨트롤러는 이 형태로 응답한다.

public record ProductionPlanDto(
    string PlanId, string PlanName, string PlantId, string ProductId, decimal PlannedQty,
    DateTime PlannedStartDate, DateTime PlannedEndDate, string Status, string? Remark);

public record ProductionOrderDto(
    string OrderId, string PlanId, string EquipmentId, string ProductId, decimal OrderQty,
    decimal? ActualQty, DateTime ScheduledStart, DateTime ScheduledEnd,
    DateTime? ActualStart, DateTime? ActualEnd, string Status);

public record LotDto(
    string LotId, string PlantId, string? WorkOrderId, string ProductId, decimal Qty, decimal DefectQty,
    string State, string ProcessState, IReadOnlyList<string> RouteSteps, int CurrentStepIndex,
    string CurrentProcessId, string? EquipmentId, string? RecipeDefId, int? RecipeDefVersion,
    string? CarrierId, bool IsHold);

// TrackOut 불량 입력 — LotTrackingService.DefectEntry의 계약 미러(ALC 경계 횡단용 평탄 DTO).
public record LotDefectInput(string DefectCode, decimal DefectQty);

// Mixing 투입 입력 — LotTrackingService.MixingInput의 계약 미러. 투입 Lot에서 소비할 수량.
public record MixingInputDto(string LotId, decimal InQty);
