using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ems;

/// <summary>
/// 생산 툴의 마스터·장착·사용·점검/교정 원장을 관리한다. 측정기는 QMS gauge/calibration이 소유하며,
/// 설비별 사용 조건과 PLC 태그 해석은 프로젝트 플러그인이 표준 명령으로 변환한다.
/// </summary>
[NexaModuleBridge("Ems", "toolBridge")]
public interface IToolBridge : INexaModuleBridge
{
    Task<Result<ToolDto>> SaveAsync(ToolCommand command, CancellationToken ct = default);
    Task<Result<ToolMountDto>> MountAsync(ToolMountCommand command, CancellationToken ct = default);
    Task<Result<ToolMountDto>> UnmountAsync(ToolUnmountCommand command, CancellationToken ct = default);
    Task<Result<ToolUsageDto>> RecordUsageAsync(ToolUsageCommand command, CancellationToken ct = default);
    Task<Result<ToolInspectionDto>> RecordInspectionAsync(ToolInspectionCommand command, CancellationToken ct = default);
}

public sealed record ToolCommand(
    string ToolId,
    string ToolName,
    string ToolType,
    string? ToolNumber = null,
    string? SerialNumber = null,
    string? EquipmentClassId = null,
    decimal? MaxUseCount = null,
    decimal? MaxUseMinutes = null,
    int? InspectionCycleDays = null,
    int? CalibrationCycleDays = null,
    string Status = "Available",
    string? Location = null,
    bool IsActive = true,
    string? ActorId = null);

public sealed record ToolMountCommand(
    string IdempotencyKey,
    string ToolId,
    string EquipmentId,
    DateTime MountedAt,
    string? PositionCode = null,
    string? ActorId = null);

public sealed record ToolUnmountCommand(
    string IdempotencyKey,
    string MountId,
    DateTime UnmountedAt,
    string? Reason = null,
    string? ActorId = null);

public sealed record ToolUsageCommand(
    string IdempotencyKey,
    string ToolId,
    string EquipmentId,
    decimal UseCount,
    decimal UseMinutes,
    DateTime UsedAt,
    string? MountId = null,
    string? ProcessLotId = null,
    string? WorkOrderId = null,
    string? ProcessId = null,
    string? RecipeId = null,
    int? RecipeVersion = null,
    string? TraceId = null,
    string? ConditionSnapshotJson = null,
    string? ActorId = null);

public sealed record ToolInspectionCommand(
    string IdempotencyKey,
    string ToolId,
    string InspectionType,
    string Result,
    DateTime InspectedAt,
    DateTime? NextDueAt = null,
    string? MeasuredValue = null,
    string? StandardValue = null,
    string? CertificateNumber = null,
    string? Remark = null,
    string? ActorId = null);

public sealed record ToolDto(
    string ToolId,
    string ToolName,
    string ToolType,
    string Status,
    decimal CurrentUseCount,
    decimal CurrentUseMinutes,
    decimal? MaxUseCount,
    decimal? MaxUseMinutes,
    DateTime? NextInspectionDueAt,
    DateTime? NextCalibrationDueAt,
    bool IsActive);

public sealed record ToolMountDto(
    string MountId,
    string ToolId,
    string EquipmentId,
    string? PositionCode,
    DateTime MountedAt,
    string MountedBy,
    DateTime? UnmountedAt,
    string? UnmountedBy,
    string? UnmountReason);

public sealed record ToolUsageDto(
    string UsageId,
    string ToolId,
    string EquipmentId,
    decimal UseCount,
    decimal UseMinutes,
    DateTime UsedAt,
    string UsedBy,
    string? ProcessLotId,
    string? WorkOrderId,
    string? TraceId);

public sealed record ToolInspectionDto(
    string InspectionId,
    string ToolId,
    string InspectionType,
    string Result,
    DateTime InspectedAt,
    string InspectedBy,
    DateTime? NextDueAt,
    string? CertificateNumber);
