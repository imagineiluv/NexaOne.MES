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

public record PomWorkOrderDto(
    string WorkOrderId, string ProductionOrderId, string PlantId, string WorkOrderName,
    string ProductId, decimal PlanQty, decimal StartQty, decimal CompleteQty, decimal ScrapQty,
    string Status, bool IsHold, string? ProcessId, string? EquipmentId, string? OwnerId,
    DateTime? PlanStartDate, DateTime? PlanEndDate, DateTime? StartedAt, DateTime? CompletedAt,
    string? RoutingId, int? RoutingStepNo, string? WorkCenterId, string? AreaId,
    string? WorkOrderType, string? SalesOrderId, string? Description, int VersionNo,
    string RoutingScope = "Unbound");

public record LotDto(
    string LotId, string PlantId, string? WorkOrderId, string ProductId, decimal Qty, decimal DefectQty,
    string State, string ProcessState, IReadOnlyList<string> RouteSteps, int CurrentStepIndex,
    string CurrentProcessId, string? EquipmentId, string? RecipeDefId, int? RecipeDefVersion,
    string? CarrierId, bool IsHold, int VersionNo = 1,
    string ControlMode = "Strict", int? ReturnStepIndex = null, string? ReturnProcessId = null,
    bool IsInRework = false, int? NextStepIndex = null, string? NextProcessId = null);

/// <summary>
/// LOT의 현재 공정과 다음 합법 공정, 재작업 복귀점 및 예외 요청을 한 번에 제공하는 작업자 화면 계약입니다.
/// 인덱스는 기존 <c>Lot.CurrentStepIndex</c>와 동일한 0-based 값입니다.
/// </summary>
public record LotRoutingContextDto(
    LotDto Lot,
    string ControlMode,
    int CurrentStepIndex,
    string CurrentProcessId,
    int? NextStepIndex,
    string? NextProcessId,
    int? ReturnStepIndex,
    string? ReturnProcessId,
    bool IsInRework,
    IReadOnlyList<RouteExceptionDto> Exceptions);

/// <summary>라우팅 정책의 서버 판정입니다. UI의 버튼 상태는 이 판정을 안내하되 최종 강제는 항상 서버가 수행합니다.</summary>
public record RoutingPolicyDecisionDto(
    string Kind,
    string Code,
    string Message,
    string ControlMode,
    string DeviationType,
    int FromStepIndex,
    int ToStepIndex,
    bool RequiresReason,
    string? ExceptionId,
    bool IsAllowed);

/// <summary>Flexible 승인 또는 NoControl 즉시 적용에 사용된 라우팅 예외의 전체 감사 계약입니다.</summary>
public record RouteExceptionDto(
    string ExceptionId,
    string LotId,
    string PlantId,
    string DeviationType,
    int FromStepIndex,
    int ToStepIndex,
    string FromProcessId,
    string ToProcessId,
    int BoundLotVersion,
    string Reason,
    string Status,
    string RequestedBy,
    DateTime RequestedAt,
    DateTime ExpiresAt,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewReason,
    string? AppliedBy,
    DateTime? AppliedAt,
    string? AppliedExecutionId,
    string ClientChannel,
    string? DeviceId,
    string? ReviewClientChannel = null,
    string? ReviewDeviceId = null);

// TrackOut 불량 입력 — LotTrackingService.DefectEntry의 계약 미러(ALC 경계 횡단용 평탄 DTO).
public record LotDefectInput(string DefectCode, decimal DefectQty);

// Mixing 투입 입력 — LotTrackingService.MixingInput의 계약 미러. 투입 Lot에서 소비할 수량.
public record MixingInputDto(string LotId, decimal InQty);
