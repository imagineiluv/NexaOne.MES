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

// Phase 4 후속 — Low-Code 화면 정의 저장소 레코드(정의는 DefinitionJson에 직렬화)
public record ScreenDefinitionRecordDto(string UiId, string Title, string DefinitionJson);

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
    string SpecId,
    string LotId,
    string EquipmentId,
    decimal? MeasuredValue,
    string? AttributeResult,
    DateTime InspectedAt,
    string InspectorId,
    bool IsPass,
    string? Remark);

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
