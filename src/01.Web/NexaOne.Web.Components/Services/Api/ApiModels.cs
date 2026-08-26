namespace NexaOne.Web.Services.Api;

// ── Auth ─────────────────────────────────────────────────────────────────────
public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string UserName,
    string PlantId,
    IReadOnlyList<string> Roles,
    bool RequirePasswordChange = false);

/// <summary>§20.10 — 로그인 시도 결과. 실패 시 서버 401 응답의 code(ACCOUNT_LOCKED 등)와
/// 메시지를 보존해 잠금 안내를 화면에 표시할 수 있게 한다.</summary>
public record LoginResult(LoginResponse? Response, string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>생산 제안을 실제 생산오더로 전환할 때 선택한 설비 배정.</summary>
public record MrpProductionAssignmentDto(string PlannedOrderId, string EquipmentId);

/// <summary>설비 배정 대화상자에 표시할 생산 제안.</summary>
public record MrpProductionProposalDto(
    string PlannedOrderId,
    string PlantId,
    string ItemId,
    decimal SuggestedQty,
    DateTime? ReleaseDate,
    DateTime? DueDate);

/// <summary>MRP 실오더 전환 결과 — 구매 제안은 구매오더, 생산 제안은 생산계획과 생산관리오더를 생성한다.</summary>
public record MrpConvertResultDto(string RunId, int Converted, int PurchaseOrders, int ProductionOrders, string? Message);

/// <summary>MRP 실행 결과(POST /api/v1/pom/mrp/run) — 실행 이력 요약. 제안 상세는 POM.MrpPlannedOrderList.</summary>
public record MrpRunResultDto(string RunId, string Status, int DemandCount, int PlannedOrderCount, string? Message);

/// <summary>제네릭 서버 페이징 결과(/query/{id}/paged) — 총건수 + 현재 페이지 행(하이브리드 페이징).</summary>
public record PagedQueryResult(int Total, List<Dictionary<string, object?>> Rows);

// ── MDM ──────────────────────────────────────────────────────────────────────
public record EquipmentDto(
    string Id,
    string EquipmentName,
    string? Description,
    string PlantId,
    string AreaId,
    string EquipmentType,
    string? ParentEquipmentId,
    string? Vendor,
    string? Model,
    string EquipmentClassId,
    string ValidState);

public record PlantDto(string Id, string PlantName, string Description, string Country, string TimeZone);
public record AreaDto(string Id, string AreaName, string Description, string PlantId);
public record ProductDto(string Id, string ProductName, string Description, string ProductType, string Unit, string ValidState);
public record CodeClassDto(string Id, string CodeClassName, string Description);
public record CodeDto(string Id, string CodeClassId, string CodeName, int SortOrder, string ValidState);

// ── EPT ──────────────────────────────────────────────────────────────────────
public record EquipmentStateMatrixDto(
    string PlantId, string FromStateId, string ToStateId,
    bool AllowFlag, string SetStateId, bool RequireReason);

public record EquipmentCurrentStateDto(
    string EquipmentId, string PlantId, string CurrentStateId,
    DateTime StateChangedAt, int StateVersion);

public record EquipmentStateHistoryDto(
    string Id, string EquipmentId, string FromState, string ToState, string SetState,
    DateTime ChangedAt, string ChangedBy, string Reason, string SourceType);

public record AlarmDto(
    string Id,
    string EquipmentId,
    string AlarmCode,
    string AlarmName,
    string AlarmLevel,
    DateTime OccurredAt,
    DateTime? ClearedAt,
    long? ElapsedSeconds);

// ── FDC ──────────────────────────────────────────────────────────────────────
public record InterlockRuleDto(
    string Id,
    string RuleName,
    string EquipmentId,
    string ParameterId,
    string Operator,
    decimal ThresholdValue,
    string Action,
    int Priority,
    bool IsActive);

public record FdcParameterDto(
    string Id,
    string ParameterName,
    string EquipmentId,
    string? GroupId,
    string Unit,
    decimal LowerLimit,
    decimal UpperLimit,
    decimal? LowerControlLimit,
    decimal? UpperControlLimit,
    int SamplingIntervalMs,
    bool IsActive);

public record FdcCollectDataDto(
    string Id,
    string EquipmentId,
    string ParameterId,
    decimal Value,
    DateTime CollectedAt,
    string Quality,
    decimal LowerLimit,
    decimal UpperLimit,
    bool IsOutOfSpec);

// POST collect-data 응답 래퍼 — 컨트롤러가 { CollectedData, Interlock } 형태로 반환한다(평면 아님)
public record FdcRecordResultDto(
    FdcCollectDataDto? CollectedData,
    FdcInterlockResultDto? Interlock);

public record FdcInterlockResultDto(
    bool IsTriggered,
    string? Action,
    string? Message,
    string? RuleId);

// Phase 4 후속 — Low-Code 화면 정의 저장소 레코드. 렌더 정의는 DefinitionJson, 진입 대상은
// TargetChannel(MES|MOBILE|POP) + 화면별 완전 경로 EntryPath가 소유한다. 3인자 생성자는 구 호출 호환용.
public record ScreenDefinitionRecordDto(
    string UiId,
    string Title,
    string DefinitionJson,
    string TargetChannel,
    string EntryPath)
{
    public ScreenDefinitionRecordDto(string uiId, string title, string definitionJson)
        : this(uiId, title, definitionJson, "MES", $"/meta/{uiId}") { }
}

// 명명 쿼리 카탈로그 항목(api/v1/sys/queries, SQL 비노출) — S/O 관리(메타 카탈로그)·디자이너 드롭다운 공용.
public record QueryCatalogItemDto(string Id, bool IsWrite, string? RequiredPermission);

public record FdcParameterGroupDto(
    string Id,
    string GroupName,
    string EquipmentId,
    string? Description,
    int DisplayOrder,
    bool IsActive);

public record FdcAlarmConfigDto(
    string Id,
    string EquipmentId,
    string ParameterId,
    string AlarmLevel,
    string Operator,
    decimal ThresholdValue,
    bool IsActive);

public record FdcAlarmHistoryDto(
    string Id,
    string AlarmConfigId,
    string EquipmentId,
    string ParameterId,
    string AlarmLevel,
    decimal TriggerValue,
    string Message,
    DateTime OccurredAt,
    DateTime? ClearedAt,
    bool IsCleared);

public record FdcInterlockHistoryDto(
    string Id,
    string RuleId,
    string EquipmentId,
    string ParameterId,
    decimal TriggerValue,
    string Action,
    string Message,
    DateTime TriggeredAt,
    DateTime? ResolvedAt,
    bool IsResolved);

// ── RMS ──────────────────────────────────────────────────────────────────────
public record RecipeDto(
    string Id,
    string RecipeName,
    string? Description,
    string EquipmentClassId,
    int Version,
    string ApprovalState,
    string? FirstApproverId,
    string? SecondApproverId,
    DateTime? ReleasedAt);

public record RecipeParamDto(
    string Id,
    string RecipeId,
    string ParamName,
    string ParamValue,
    string Unit,
    int SortOrder);

// ── QMS ──────────────────────────────────────────────────────────────────────
public record DefectClassDto(string Id, string DefectClassName, string Description, string Severity, bool IsActive);

public record InspectionSpecDto(
    string Id,
    string SpecName,
    string ProcessId,
    string ItemName,
    string MeasureType,
    decimal? NominalValue,
    decimal? TolerancePlus,
    decimal? ToleranceMinus,
    bool IsActive);

public record InspectionResultDto(
    string Id,
    string InspectionId,
    string SpecId,
    string LotId,
    string EquipmentId,
    decimal? MeasuredValue,
    string? AttributeResult,
    DateTime InspectedAt,
    string InspectorId,
    bool IsPass,
    string? Remark);

public record InspectionExecutionItemInputDto(
    string SpecId,
    decimal? MeasuredValue,
    string? AttributeResult,
    int SampleQuantity,
    int DefectQuantity,
    string? Remark = null);

public record RecordInspectionExecutionV2Request(
    string IdempotencyKey,
    string InspectionType,
    string LotId,
    string EquipmentId,
    int LotQuantity,
    int SampleQuantity,
    int DefectQuantity,
    IReadOnlyList<InspectionExecutionItemInputDto> Items,
    string? SamplingPlanRevisionId = null,
    string? ParentInspectionId = null,
    string RelationType = "Original",
    string? Remark = null);

public record InspectionExecutionItemDto(
    string ResultId, string SpecId, decimal? MeasuredValue, string? AttributeResult,
    int SampleQuantity, int DefectQuantity, bool IsPass, string? Remark);
public record InspectionSamplingPlanSnapshotDto(
    string PlanRevisionId, string PlanId, int RevisionNo, string Mode,
    int LotSizeMin, int? LotSizeMax, int? SampleSize,
    int AcceptanceNumber, int RejectionNumber, decimal Aql,
    string StandardName, string StandardVersion, DateTime EffectiveFrom);
public record InspectionExecutionHistoryDto(
    string EventId, string EventType, string IdempotencyKey, string RequestHash,
    string ActorId, DateTime OccurredAt, string RootInspectionId,
    string? ParentInspectionId, string? RelatedInspectionId, string? Reason);
public record InspectionAiEvidenceDto(
    AiInferenceDto Inference, IReadOnlyList<AiReviewDto> Reviews);
public record InspectionExecutionV2Dto(
    string InspectionId, string InspectionType, string RelationType,
    string RootInspectionId, string? ParentInspectionId,
    string LotId, string EquipmentId,
    int LotQuantity, int SampleQuantity, int DefectQuantity,
    string IdempotencyKey, string RequestHash,
    DateTime InspectedAt, string InspectorId,
    bool IsPass, bool IsCancelled, bool IsReplay, string? Remark,
    InspectionSamplingPlanSnapshotDto? SamplingPlan,
    IReadOnlyList<InspectionExecutionItemDto> Items,
    IReadOnlyList<InspectionExecutionHistoryDto> History,
    IReadOnlyList<InspectionAiEvidenceDto> AiEvidence);

/// <summary>v2 검사 REST 결과. 상태 코드를 보존해 409 멱등 충돌과 권한 오류를 UI에 전달합니다.</summary>
public sealed record InspectionExecutionApiResult(
    InspectionExecutionV2Dto? Execution,
    string? Error,
    int StatusCode)
{
    public bool Success => Execution is not null && StatusCode is >= 200 and < 300;
}

public record LotInspectionStatusDto(
    string LotId,
    bool HasResults,
    bool AllPassed,
    int ResultCount,
    int FailedCount,
    DateTime? LastInspectedAt);

public record SpcParamDto(
    string Id,
    string ParamName,
    string EquipmentId,
    string ProcessId,
    decimal Mean,
    decimal Ucl,
    decimal Lcl,
    decimal? Usl,
    decimal? Lsl,
    int SampleSize,
    bool IsActive);

public record DefectDto(
    string Id,
    string LotId,
    string EquipmentId,
    string DefectClassId,
    int DefectCount,
    decimal DefectRate,
    DateTime InspectedAt,
    string InspectorId,
    string? Remark,
    bool IsConfirmed,
    DateTime? ConfirmedAt);

public record SpcLimitRevisionDto(
    string Id, string ParamId, int RevisionNo, string ChartType,
    decimal CenterLine, decimal Ucl, decimal Lcl, DateTime EffectiveFrom, string Reason);
public record SpcRuleViolationDto(
    string Id, string ParamId, string LimitRevisionId, string ObservationId,
    string RuleCode, DateTime DetectedAt, string Evidence);
public record SpcSubgroupEvaluationDto(
    string SubgroupId, string ParamId, string LimitRevisionId, string ChartType,
    DateTime ObservedAt, IReadOnlyList<decimal> Values,
    IReadOnlyList<SpcRuleViolationDto> Violations, bool IsReplay);
public record SamplingPlanRevisionDto(
    string Id, string PlanId, int RevisionNo, string Mode, int LotSizeMin,
    int? LotSizeMax, int? SampleSize, int AcceptanceNumber, int RejectionNumber,
    decimal Aql, string StandardName, string StandardVersion, DateTime EffectiveFrom);
public record SamplingDecisionDto(
    string Disposition, int RequiredSampleSize, int InspectedQuantity,
    int DefectQuantity, string Reason);
public record SamplingEvaluationDto(SamplingPlanRevisionDto Plan, SamplingDecisionDto Decision);
public record AiModelVersionDto(
    string Id, string ModelId, int VersionNo, string ArtifactUri,
    string ArtifactSha256, decimal ConfidenceThreshold, DateTime EffectiveFrom);
public record AiInferenceDto(
    string Id, string IdempotencyKey, string ModelVersionId, string InspectionId,
    string ImageUri, string ImageSha256, string RawVerdict, decimal Confidence,
    decimal Threshold, DateTime InferredAt, bool RequiresReview);
public record AiReviewDto(
    string Id, string InferenceId, int ReviewSequence, string ReviewerId,
    string Verdict, string Reason, DateTime ReviewedAt);

// ── EMS ──────────────────────────────────────────────────────────────────────
public record MaintenancePlanDto(
    string Id,
    string PlanName,
    string EquipmentId,
    string PlanType,
    string CycleType,
    DateTime ScheduledDate,
    decimal EstimatedDurationHours,
    string AssigneeId,
    string Status);

public record SparePartDto(
    string Id,
    string PartName,
    string PartNumber,
    string Description,
    string UnitOfMeasure,
    decimal CurrentStock,
    decimal MinStock,
    decimal MaxStock,
    string Location,
    string? EquipmentClassId,
    bool IsLowStock);

public record WorkOrderDto(
    string Id,
    string? PlanId,
    string EquipmentId,
    string WoType,
    string? Description,
    string AssigneeId,
    DateTime IssuedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string Status,
    string? FailureCodeId,
    string? Remark);

// ── POM 작업지시 실행 ───────────────────────────────────────────────────────────────
// 서버 ServiceContracts의 경량 JSON 미러. RCL이 도메인 모듈 어셈블리를 직접 참조하지 않도록 하면서
// typed REST 응답과 VERSION_NO 동시성 계약을 유지한다.
public sealed record PomWorkOrderDto(
    string WorkOrderId,
    string ProductionOrderId,
    string PlantId,
    string WorkOrderName,
    string ProductId,
    decimal PlanQty,
    decimal StartQty,
    decimal CompleteQty,
    decimal ScrapQty,
    string Status,
    bool IsHold,
    string? ProcessId,
    string? EquipmentId,
    string? OwnerId,
    DateTime? PlanStartDate,
    DateTime? PlanEndDate,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? RoutingId,
    int? RoutingStepNo,
    string? WorkCenterId,
    string? AreaId,
    string? WorkOrderType,
    string? SalesOrderId,
    string? Description,
    int VersionNo,
    string RoutingScope = "Unbound");

/// <summary>
/// 생산관리오더 아래에 작업지시를 만드는 typed 요청입니다. RoutingScope는 미연결(Unbound),
/// 한 공정(Operation), 한 W/O 전체 제품 라우팅(SerialRoute)을 구분합니다.
/// </summary>
public sealed record PomWorkOrderCreateRequest(
    string WorkOrderId,
    string ProductionOrderId,
    string PlantId,
    string WorkOrderName,
    string ProductId,
    decimal PlanQty,
    DateTime? PlanStartDate,
    DateTime? PlanEndDate,
    string? ProcessId,
    string? EquipmentId,
    string? OwnerId,
    string? RoutingId,
    int? RoutingStepNo,
    string? WorkCenterId,
    string? AreaId,
    string? WorkOrderType,
    string? SalesOrderId,
    string? Description,
    string RoutingScope);

/// <summary>
/// 작업지시 상태전이 입력. GoodQty/DefectQty는 증분이 아니라 현재 누계 절대값이며
/// report/complete에서만 사용됩니다.
/// </summary>
public sealed record PomWorkOrderActionRequest(
    int ExpectedVersion,
    string IdempotencyKey,
    string ClientChannel,
    string? DeviceId = null,
    string? Remark = null,
    decimal? GoodQty = null,
    decimal? DefectQty = null);

/// <summary>작업지시 REST 결과. StatusCode를 보존해 409 동시성 충돌을 UI에서 식별합니다.</summary>
public sealed record PomWorkOrderActionResult(
    PomWorkOrderDto? WorkOrder,
    string? Error,
    int StatusCode)
{
    public bool Success => WorkOrder is not null && StatusCode is >= 200 and < 300;
}

// ── POM LOT 라우팅 실행 ────────────────────────────────────────────────────
// RCL이 POM 모듈을 직접 참조하지 않도록 ServiceContracts JSON과 동일한 경량 미러를 유지합니다.
public sealed record PomLotDto(
    string LotId,
    string PlantId,
    string? WorkOrderId,
    string ProductId,
    decimal Qty,
    decimal DefectQty,
    string State,
    string ProcessState,
    IReadOnlyList<string> RouteSteps,
    int CurrentStepIndex,
    string CurrentProcessId,
    string? EquipmentId,
    string? RecipeDefId,
    int? RecipeDefVersion,
    string? CarrierId,
    bool IsHold,
    int VersionNo,
    string ControlMode,
    int? ReturnStepIndex,
    string? ReturnProcessId,
    bool IsInRework,
    int? NextStepIndex,
    string? NextProcessId);

public sealed record PomLotRoutingContextDto(
    PomLotDto Lot,
    string ControlMode,
    int CurrentStepIndex,
    string CurrentProcessId,
    int? NextStepIndex,
    string? NextProcessId,
    int? ReturnStepIndex,
    string? ReturnProcessId,
    bool IsInRework,
    IReadOnlyList<PomRouteExceptionDto> Exceptions);

public sealed record PomRoutingPolicyDecisionDto(
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

public sealed record PomRouteExceptionDto(
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

public sealed record PomLotTrackInRequest(
    string PlantId,
    string EquipmentId,
    int ExpectedVersion,
    string IdempotencyKey,
    string? RecipeDefId = null,
    int? RecipeDefVersion = null,
    string ClientChannel = "MES",
    string? DeviceId = null);

public sealed record PomLotDefectInput(
    string DefectCode,
    decimal DefectQty);

public sealed record PomLotTrackOutRequest(
    string PlantId,
    string EquipmentId,
    decimal Qty,
    int ExpectedVersion,
    string IdempotencyKey,
    string? CarrierId = null,
    IReadOnlyList<PomLotDefectInput>? Defects = null,
    string ClientChannel = "MES",
    string? DeviceId = null);

public sealed record PomLotHoldRequest(
    int? ExpectedVersion = null,
    string? IdempotencyKey = null,
    string? Reason = null,
    string ClientChannel = "MES",
    string? DeviceId = null);

public sealed record PomEvaluateRoutingRequest(
    string PlantId,
    string DeviationType,
    int TargetStepIndex,
    string? Reason = null,
    string? ExceptionId = null);

public sealed record PomChangeRoutingControlModeRequest(
    string PlantId,
    string ControlMode,
    string Reason,
    int ExpectedVersion,
    string IdempotencyKey,
    string ClientChannel,
    string? DeviceId = null);

public sealed record PomApplyRouteDeviationRequest(
    string PlantId,
    string DeviationType,
    int TargetStepIndex,
    string Reason,
    int ExpectedVersion,
    string IdempotencyKey,
    string? ExceptionId,
    string ClientChannel,
    string? DeviceId = null);

public sealed record PomRequestRouteExceptionRequest(
    string PlantId,
    string DeviationType,
    int TargetStepIndex,
    string Reason,
    int ExpectedVersion,
    DateTime ExpiresAt,
    string ExceptionId,
    string ClientChannel,
    string? DeviceId = null);

public sealed record PomReviewRouteExceptionRequest(
    string? Reason = null,
    string ClientChannel = "MES",
    string? DeviceId = null);

/// <summary>라우팅 REST 호출은 409 차단 사유를 작업자 화면에 그대로 표시해야 하므로 상태 코드를 보존합니다.</summary>
public sealed record PomRoutingApiResult<T>(T? Value, string? Error, int StatusCode) where T : class
{
    public bool Success => Value is not null && StatusCode is >= 200 and < 300;
}

// ── PPM ──────────────────────────────────────────────────────────────────────
public record ProductionPlanDto(
    string Id,
    string PlanName,
    string PlantId,
    string ProductId,
    decimal PlannedQty,
    DateTime PlannedStartDate,
    DateTime PlannedEndDate,
    string Status,
    string? Remark);

public record ProductionOrderDto(
    string Id,
    string PlanId,
    string EquipmentId,
    string ProductId,
    decimal OrderQty,
    decimal? ActualQty,
    DateTime ScheduledStart,
    DateTime ScheduledEnd,
    DateTime? ActualStart,
    DateTime? ActualEnd,
    string Status);

// ── PPM - Lot TrackIn/TrackOut (설계서 19.4) ─────────────────────────────────
public record LotDto(
    string Id,
    string PlantId,
    string? WorkOrderId,
    string ProductId,
    decimal Qty,
    decimal DefectQty,
    string State,
    string ProcessState,
    List<string> RouteSteps,
    int CurrentStepIndex,
    string CurrentProcessId,
    bool IsLastStep,
    string? EquipmentId,
    string? RecipeDefId,
    int? RecipeDefVersion,
    string? CarrierId,
    bool IsHold,
    string? TrackInUser,
    DateTime? TrackInTime,
    string? TrackOutUser,
    DateTime? TrackOutTime);

// ── DLV ──────────────────────────────────────────────────────────────────────
public record DeliveryOrderDto(
    string Id,
    string CustomerName,
    string PlantId,
    DateTime RequestedDate,
    DateTime? ShippedDate,
    string Status,
    string? Remark);

// ── SYS ──────────────────────────────────────────────────────────────────────
public record RoleDto(
    string Id,
    string RoleName,
    string Description,
    IReadOnlyList<string> Permissions);

// ── SYS - 사용자 메뉴 개인화 (설계서 20.12 즐겨찾기/최근 메뉴) ────────────────
// UiId 포함 — 통합 호스트 셸은 /meta/{uiId}로 내비게이션한다(구 ProgramId 내비의 웹 적응).
public record FavoriteMenuDto(
    string MenuId,
    string MenuName,
    string ProgramId,
    string? ImageId,
    string UiId,
    int DisplaySequence);

public record RecentMenuDto(
    string MenuId,
    string MenuName,
    string ProgramId,
    string? ImageId,
    string UiId,
    DateTime LastUsedAt);

// ── SYS - Deploy (설계서 20.11 배포 파일 업로드/클라이언트 업데이트) ──────────
// 다운로드 URL은 별도 필드가 아니라 api/v1/deploy/files/{FileId}/download 규약으로 구성한다.
public record DeployFileDto(
    string FileId,
    string Version,
    string FileName,
    string Hash,
    long FileSize,
    string Description,
    bool ForceUpdate,
    bool IsActive,
    string UploadedBy,
    DateTime UploadedAt);

// ── SYS - 사용자 언어(P3-14 다국어) ───────────────────────────────────────────
public record UserLanguageDto(string Language);

// ── FDC - 가상 이벤트 수동 평가(브리지 VirtualEventEvaluationDto 미러) ─────────
public record VirtualEventEvaluationDto(
    string EquipmentId, string EventId, string EventName, bool IsOn, bool Changed, DateTime EvaluatedAt);

// ── SYS - ConditionSetting (설계서 20.8 조건 저장/불러오기) ───────────────────
public record ConditionSettingDto(
    ConditionItemDto? Latest,
    List<ConditionItemDto> Items);

public record ConditionItemDto(
    string Name,
    DateTime SavedAt,
    Dictionary<string, string?> Values);

// ── SYS - 사용자 등록 신청/승인 (설계서 19.3) ─────────────────────────────────
public record UserRequestDto(
    string RequestId,
    string UserId,
    string UserName,
    string Email,
    string Department,
    string Position,
    string? Duty,
    string PlantId,
    string Language,
    string? CellPhoneNumber,
    string? Address,
    string? Description,
    string? Nickname,
    string Status,
    int RequestVersion,
    DateTime RequestedAt,
    DateTime TermsAcceptedAt,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string? RejectReason,
    string? RejectedBy,
    DateTime? RejectedAt);

/// <summary>승인 응답 — 임시 비밀번호는 이 응답에서만 1회 노출된다(관리자 전달용, 최초 로그인 시 변경 강제).</summary>
public record UserRequestApprovalDto(UserRequestDto Request, string TempPassword);
