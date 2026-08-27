namespace NexaOne.EMS.Application.Tools;

public interface IToolRepository
{
    Task<ToolRecord?> GetToolAsync(string toolId, CancellationToken ct = default);
    Task<ToolSaveCommandRecord?> GetSaveCommandAsync(
        string idempotencyKey,
        CancellationToken ct = default);
    Task<bool> TrySaveToolAsync(
        ToolRecord tool,
        string? expectedStatus,
        int expectedVersion,
        ToolSaveCommandRecord command,
        string actorId,
        CancellationToken ct = default);
    Task<ToolMountRecord?> GetMountAsync(string mountId, CancellationToken ct = default);
    Task<ToolMountRecord?> GetActiveMountAsync(string toolId, CancellationToken ct = default);
    Task<ToolMountRecord?> GetActiveMountAtPositionAsync(
        string equipmentId,
        string positionCode,
        CancellationToken ct = default);
    Task<ToolMountRecord?> GetMountByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<ToolMountRecord?> GetUnmountByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<DateTime?> GetLatestUsageAtAsync(string mountId, CancellationToken ct = default);
    Task<bool> TryMountAsync(
        ToolMountRecord mount,
        string? expectedEquipmentClassId,
        CancellationToken ct = default);
    Task<bool> TryUnmountAsync(ToolMountRecord mount, string key, string hash, DateTime at,
        string actorId, string? reason, CancellationToken ct = default);
    Task<ToolUsageRecord?> GetUsageByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> TryRecordUsageAsync(
        ToolUsageRecord usage,
        string? expectedEquipmentClassId,
        CancellationToken ct = default);
    Task<ToolInspectionRecord?> GetInspectionByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> TryRecordInspectionAsync(ToolInspectionRecord inspection, CancellationToken ct = default);
}

public sealed record ToolRecord(
    string ToolId, string ToolName, string ToolType, string? ToolNumber, string? SerialNumber,
    string? EquipmentClassId, decimal? MaxUseCount, decimal? MaxUseMinutes,
    decimal CurrentUseCount, decimal CurrentUseMinutes,
    int? InspectionCycleDays, int? CalibrationCycleDays,
    DateTime? LastInspectedAt, DateTime? LastCalibratedAt,
    DateTime? NextInspectionDueAt, DateTime? NextCalibrationDueAt,
    string Status, string? Location, bool IsActive, int Version = 1);

public sealed record ToolSaveCommandRecord(
    string CommandId,
    string IdempotencyKey,
    string RequestHash,
    string ToolId,
    int ExpectedVersion,
    int ResultVersion,
    string ResultJson,
    string ActorId,
    DateTime CreatedAt);

public sealed record ToolMountRecord(
    string MountId, string IdempotencyKey, string RequestHash, string ToolId, string EquipmentId,
    string? PositionCode, DateTime MountedAt, string MountedBy, DateTime? UnmountedAt,
    string? UnmountedBy, string? UnmountIdempotencyKey, string? UnmountRequestHash,
    string? UnmountReason, DateTime CreatedAt);

public sealed record ToolUsageRecord(
    string UsageId, string IdempotencyKey, string RequestHash, string ToolId, string? MountId,
    string EquipmentId, string? ProcessLotId, string? WorkOrderId, string? ProcessId,
    string? RecipeId, int? RecipeVersion, decimal UseCount, decimal UseMinutes,
    DateTime UsedAt, string UsedBy, string? TraceId, string? ConditionSnapshotJson, DateTime CreatedAt);

public sealed record ToolInspectionRecord(
    string InspectionId, string IdempotencyKey, string RequestHash, string ToolId,
    string InspectionType, string Result, string? MeasuredValue, string? StandardValue,
    string? CertificateNumber, DateTime InspectedAt, string InspectedBy, DateTime? NextDueAt,
    string? Remark, DateTime CreatedAt);
