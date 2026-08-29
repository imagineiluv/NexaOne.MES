using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Mdm;
using System.Text.Json;

namespace NexaOne.EMS.Application.Tools;

public sealed class ToolService
{
    private static readonly HashSet<string> Statuses = new(StringComparer.OrdinalIgnoreCase)
        { "Available", "Mounted", "Due", "Blocked", "Retired" };
    private static readonly HashSet<string> ActivityTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Use", "Cleaning" };
    private static readonly HashSet<string> CleaningResults = new(StringComparer.OrdinalIgnoreCase)
        { "Pass", "Fail" };
    private readonly IToolRepository _repository;
    private readonly IEquipmentDirectory _equipmentDirectory;
    private readonly IEquipmentOutputMasterDirectory? _equipmentOutputMasterDirectory;

    public ToolService(
        IToolRepository repository,
        IEquipmentDirectory equipmentDirectory,
        IEquipmentOutputMasterDirectory? equipmentOutputMasterDirectory = null)
    {
        _repository = repository;
        _equipmentDirectory = equipmentDirectory
                              ?? throw new ArgumentNullException(nameof(equipmentDirectory));
        _equipmentOutputMasterDirectory = equipmentOutputMasterDirectory;
    }

    public async Task<Result<ToolRecord>> SaveAsync(ToolCommand command, CancellationToken ct = default)
    {
        var error = ValidateTool(command);
        if (error is not null) return Result.Failure<ToolRecord>(error);
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure) return Result.Failure<ToolRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var toolId = command.ToolId.Trim();
        var idempotencyKey = Text(command.IdempotencyKey)
                             ?? $"tool-master:{toolId}:v{command.ExpectedVersion}";
        var equipmentClassId = Text(command.EquipmentClassId);
        var status = Canonical(Statuses, command.Status);
        var requestHash = Hash(
            "ToolMaster", toolId, command.ToolName.Trim(), command.ToolType.Trim(),
            Text(command.ToolNumber), Text(command.SerialNumber), equipmentClassId,
            command.MaxUseCount, command.MaxUseMinutes, command.InspectionCycleDays,
            command.CalibrationCycleDays, status, Text(command.Location), command.IsActive,
            command.ExpectedVersion, actor);
        var replay = await _repository.GetSaveCommandAsync(idempotencyKey, ct);
        if (replay is not null) return ReplaySave(replay, requestHash);
        if (equipmentClassId is not null
            && !await _equipmentDirectory.EquipmentClassExistsAsync(equipmentClassId, ct))
            return Result.Failure<ToolRecord>(Error.NotFoundOf("EquipmentClass", equipmentClassId));

        var existing = await _repository.GetToolAsync(toolId, ct);
        if (command.ExpectedVersion == 0 && existing is not null)
            return Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.IdentityConflict", $"Tool '{toolId}' already exists."));
        if (command.ExpectedVersion > 0 && existing is null)
            return Result.Failure<ToolRecord>(Error.NotFoundOf("Tool", toolId));
        if (existing is not null && existing.Version != command.ExpectedVersion)
            return Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.VersionConflict",
                $"Tool '{toolId}' is version {existing.Version}, not {command.ExpectedVersion}."));
        var tool = new ToolRecord(
            toolId, command.ToolName.Trim(), command.ToolType.Trim(), Text(command.ToolNumber),
            Text(command.SerialNumber), equipmentClassId, command.MaxUseCount, command.MaxUseMinutes,
            existing?.CurrentUseCount ?? 0m, existing?.CurrentUseMinutes ?? 0m,
            command.InspectionCycleDays, command.CalibrationCycleDays,
            existing?.LastInspectedAt, existing?.LastCalibratedAt,
            existing?.NextInspectionDueAt, existing?.NextCalibrationDueAt,
            status, Text(command.Location), command.IsActive, command.ExpectedVersion + 1);

        var activeMount = await _repository.GetActiveMountAsync(tool.ToolId, ct);
        if (activeMount is not null)
        {
            if (existing is null
                || !tool.IsActive
                || !string.Equals(tool.Status, existing.Status, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    tool.EquipmentClassId,
                    existing.EquipmentClassId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<ToolRecord>(Error.Conflict(
                    "EMS.Tool.ActiveMountState",
                    "A mounted tool must remain active; its lifecycle status and equipment class cannot be overwritten by a master-data save."));
            }
        }
        else if (tool.Status.Equals("Mounted", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.MountStateMissing",
                "Tool status cannot be Mounted without an active mount history row."));
        }

        if (tool.Status.Equals("Available", StringComparison.OrdinalIgnoreCase)
            && !CanUse(tool, DateTime.UtcNow))
        {
            return Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.NotAvailable",
                "A tool past its life, inspection, or calibration limit cannot be marked Available."));
        }

        var write = new ToolSaveCommandRecord(
            $"TSC_{Guid.NewGuid():N}", idempotencyKey, requestHash, tool.ToolId,
            command.ExpectedVersion, tool.Version, JsonSerializer.Serialize(tool), actor,
            DateTime.UtcNow);
        if (!await _repository.TrySaveToolAsync(
                tool, existing?.Status, command.ExpectedVersion, write, actor, ct))
        {
            var winner = await _repository.GetSaveCommandAsync(idempotencyKey, ct);
            if (winner is not null) return ReplaySave(winner, requestHash);
            return Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.ConcurrentSave",
                "The tool version, lifecycle state, or mount changed concurrently."));
        }
        return Result.Success(tool);
    }

    public async Task<Result<ToolMountRecord>> MountAsync(ToolMountCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) return InvalidMount(nameof(command.IdempotencyKey), "IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(command.ToolId)) return InvalidMount(nameof(command.ToolId), "ToolId is required.");
        if (string.IsNullOrWhiteSpace(command.EquipmentId)) return InvalidMount(nameof(command.EquipmentId), "EquipmentId is required.");
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure) return Result.Failure<ToolMountRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var at = Utc(command.MountedAt);
        var toolId = command.ToolId.Trim();
        var equipmentId = command.EquipmentId.Trim();
        var positionCode = Text(command.PositionCode);
        var hash = Hash(toolId, equipmentId, positionCode, at, actor);
        var existing = await _repository.GetMountByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
        if (existing is not null) return Replay(existing, existing.RequestHash, hash);
        var tool = await _repository.GetToolAsync(toolId, ct);
        if (tool is null) return Result.Failure<ToolMountRecord>(Error.NotFoundOf("Tool", command.ToolId));
        var equipment = await _equipmentDirectory.GetEquipmentAsync(equipmentId, ct);
        if (equipment is null)
            return Result.Failure<ToolMountRecord>(Error.NotFoundOf("Equipment", equipmentId));
        if (!equipment.IsValid)
            return Result.Failure<ToolMountRecord>(Error.Conflict(
                "EMS.Tool.EquipmentInactive",
                $"Equipment '{equipmentId}' is not active."));
        if (tool.EquipmentClassId is not null)
        {
            if (!string.Equals(
                    tool.EquipmentClassId,
                    equipment.EquipmentClassId,
                    StringComparison.OrdinalIgnoreCase))
                return Result.Failure<ToolMountRecord>(Error.Conflict(
                    "EMS.Tool.EquipmentClassMismatch",
                    $"Tool '{tool.ToolId}' is assigned to equipment class '{tool.EquipmentClassId}', not '{equipment.EquipmentClassId}'."));
        }
        if (positionCode is not null
            && await _repository.GetActiveMountAtPositionAsync(equipmentId, positionCode, ct) is not null)
            return Result.Failure<ToolMountRecord>(Error.Conflict(
                "EMS.Tool.PositionOccupied",
                $"Equipment '{equipmentId}' position '{positionCode}' already has an active tool mount."));
        if (!tool.Status.Equals("Available", StringComparison.OrdinalIgnoreCase) || !CanUse(tool, at))
            return Result.Failure<ToolMountRecord>(Error.Conflict("EMS.Tool.NotMountable", $"Tool '{tool.ToolId}' is not available."));
        var mount = new ToolMountRecord(
            $"TMT_{Guid.NewGuid():N}", command.IdempotencyKey.Trim(), hash, tool.ToolId,
            equipmentId, positionCode, at, actor,
            null, null, null, null, null, DateTime.UtcNow);
        if (await _repository.TryMountAsync(mount, tool.EquipmentClassId, ct))
            return Result.Success(mount);
        var winner = await _repository.GetMountByIdempotencyKeyAsync(mount.IdempotencyKey, ct);
        return winner is not null
            ? Replay(winner, winner.RequestHash, hash)
            : Result.Failure<ToolMountRecord>(Error.Conflict(
                "EMS.Tool.AlreadyMounted", "The tool is already mounted or was changed concurrently."));
    }

    public async Task<Result<ToolMountRecord>> UnmountAsync(ToolUnmountCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) return InvalidMount(nameof(command.IdempotencyKey), "IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(command.MountId)) return InvalidMount(nameof(command.MountId), "MountId is required.");
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure) return Result.Failure<ToolMountRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var at = Utc(command.UnmountedAt);
        var hash = Hash(command.MountId, at, Text(command.Reason), actor);
        var replay = await _repository.GetUnmountByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
        if (replay is not null) return Replay(replay, replay.UnmountRequestHash ?? "", hash);
        var mount = await _repository.GetMountAsync(command.MountId.Trim(), ct);
        if (mount is null) return Result.Failure<ToolMountRecord>(Error.NotFoundOf("ToolMount", command.MountId));
        if (mount.UnmountedAt is not null)
            return Result.Failure<ToolMountRecord>(Error.Conflict("EMS.Tool.AlreadyUnmounted", "The tool has already been unmounted."));
        if (at < mount.MountedAt) return InvalidMount(nameof(command.UnmountedAt), "UnmountedAt cannot precede MountedAt.");
        var latestUsageAt = await _repository.GetLatestUsageAtAsync(mount.MountId, ct);
        if (latestUsageAt.HasValue && at < latestUsageAt.Value)
            return InvalidMount(
                nameof(command.UnmountedAt),
                "UnmountedAt cannot precede usage already recorded for the mount.");
        if (!await _repository.TryUnmountAsync(mount, command.IdempotencyKey.Trim(), hash, at, actor, Text(command.Reason), ct))
        {
            var winner = await _repository.GetUnmountByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
            return winner is not null
                ? Replay(winner, winner.UnmountRequestHash ?? string.Empty, hash)
                : Result.Failure<ToolMountRecord>(Error.Conflict("EMS.Tool.ConcurrentUnmount", "The mount was changed concurrently."));
        }
        return Result.Success(mount with
        {
            UnmountedAt = at, UnmountedBy = actor, UnmountIdempotencyKey = command.IdempotencyKey.Trim(),
            UnmountRequestHash = hash, UnmountReason = Text(command.Reason),
        });
    }

    public async Task<Result<ToolUsageRecord>> RecordUsageAsync(ToolUsageCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) return InvalidUsage(nameof(command.IdempotencyKey), "IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(command.ToolId)) return InvalidUsage(nameof(command.ToolId), "ToolId is required.");
        if (string.IsNullOrWhiteSpace(command.EquipmentId)) return InvalidUsage(nameof(command.EquipmentId), "EquipmentId is required.");
        if (command.UseCount < 0m || command.UseMinutes < 0m || command.UseCount + command.UseMinutes <= 0m)
            return InvalidUsage("Usage", "UseCount/UseMinutes must be non-negative and at least one must be greater than zero.");
        if (!ActivityTypes.Contains(command.ActivityType))
            return InvalidUsage(nameof(command.ActivityType), "ActivityType must be Use or Cleaning.");
        var activityType = Canonical(ActivityTypes, command.ActivityType);
        var workScopeId = Text(command.WorkScopeId);
        var carrierId = Text(command.CarrierId);
        var cleaningProgramId = Text(command.CleaningProgramId);
        var cleaningResult = Text(command.CleaningResult);
        if (workScopeId?.Length > 50)
            return InvalidUsage(nameof(command.WorkScopeId), "WorkScopeId cannot exceed 50 characters.");
        if (carrierId?.Length > 100)
            return InvalidUsage(nameof(command.CarrierId), "CarrierId cannot exceed 100 characters.");
        if (cleaningProgramId?.Length > 100)
            return InvalidUsage(nameof(command.CleaningProgramId), "CleaningProgramId cannot exceed 100 characters.");
        if (cleaningResult?.Length > 20)
            return InvalidUsage(nameof(command.CleaningResult), "CleaningResult cannot exceed 20 characters.");
        if (activityType.Equals("Cleaning", StringComparison.OrdinalIgnoreCase))
        {
            if (workScopeId is null)
                return InvalidUsage(nameof(command.WorkScopeId), "Cleaning activity requires WorkScopeId.");
            if (carrierId is null)
                return InvalidUsage(nameof(command.CarrierId), "Cleaning activity requires CarrierId.");
            if (cleaningProgramId is null)
                return InvalidUsage(nameof(command.CleaningProgramId), "Cleaning activity requires CleaningProgramId.");
            if (cleaningResult is null || !CleaningResults.Contains(cleaningResult))
                return InvalidUsage(nameof(command.CleaningResult), "CleaningResult must be Pass or Fail.");
            if (Text(command.ProcessLotId) is not null)
                return InvalidUsage(nameof(command.ProcessLotId), "Cleaning activity cannot reference a process LOT.");
        }
        else if (cleaningProgramId is not null || cleaningResult is not null)
        {
            return InvalidUsage(nameof(command.ActivityType),
                "CleaningProgramId and CleaningResult are only valid for Cleaning activity.");
        }
        else
        {
            cleaningResult = null;
        }
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure) return Result.Failure<ToolUsageRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var at = Utc(command.UsedAt);
        var hash = Hash(command.ToolId.Trim(), command.EquipmentId.Trim(), command.UseCount, command.UseMinutes, at,
            Text(command.MountId), Text(command.ProcessLotId), Text(command.WorkOrderId), Text(command.ProcessId),
            Text(command.RecipeId), command.RecipeVersion, Text(command.TraceId), Text(command.ConditionSnapshotJson),
            workScopeId, carrierId, activityType, cleaningProgramId, cleaningResult, actor);
        var existing = await _repository.GetUsageByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
        if (existing is not null) return Replay(existing, existing.RequestHash, hash);
        var tool = await _repository.GetToolAsync(command.ToolId.Trim(), ct);
        if (tool is null) return Result.Failure<ToolUsageRecord>(Error.NotFoundOf("Tool", command.ToolId));
        var equipmentId = command.EquipmentId.Trim();
        var equipment = await _equipmentDirectory.GetEquipmentAsync(equipmentId, ct);
        if (equipment is null)
            return Result.Failure<ToolUsageRecord>(Error.NotFoundOf("Equipment", equipmentId));
        if (!equipment.IsValid)
            return Result.Failure<ToolUsageRecord>(Error.Conflict(
                "EMS.Tool.EquipmentInactive",
                $"Equipment '{equipmentId}' is not active."));
        if (activityType.Equals("Cleaning", StringComparison.OrdinalIgnoreCase))
        {
            if (_equipmentOutputMasterDirectory is null)
                return Result.Failure<ToolUsageRecord>(Error.Failure(
                    "EMS.Tool.CarrierMasterUnavailable",
                    "Carrier cleaning usage requires the MDM carrier master directory."));
            var masterScope = await _equipmentOutputMasterDirectory.GetScopeAsync(
                equipmentId, carrierId, ct);
            if (masterScope is null || !masterScope.IsEquipmentValid)
                return Result.Failure<ToolUsageRecord>(Error.Validation(
                    nameof(command.EquipmentId), "EquipmentId does not reference an equipment master."));
            if (!masterScope.CarrierExists)
                return Result.Failure<ToolUsageRecord>(Error.NotFoundOf("Carrier", carrierId!));
        }
        if (tool.EquipmentClassId is not null
            && !string.Equals(
                tool.EquipmentClassId,
                equipment.EquipmentClassId,
                StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ToolUsageRecord>(Error.Conflict(
                "EMS.Tool.EquipmentClassMismatch",
                $"Tool '{tool.ToolId}' is assigned to equipment class '{tool.EquipmentClassId}', not '{equipment.EquipmentClassId}'."));
        if (!CanUse(tool, at) || !WithinLifeAfterUsage(tool, command.UseCount, command.UseMinutes))
            return Result.Failure<ToolUsageRecord>(Error.Conflict("EMS.Tool.NotUsable", $"Tool '{tool.ToolId}' cannot be used in status '{tool.Status}'."));
        var activeMount = await _repository.GetActiveMountAsync(tool.ToolId, ct);
        if (activeMount is not null)
        {
            if (string.IsNullOrWhiteSpace(command.MountId)
                || !string.Equals(activeMount.MountId, command.MountId.Trim(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(activeMount.EquipmentId, equipmentId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<ToolUsageRecord>(Error.Conflict("EMS.Tool.InvalidMount", "The active mount does not match the tool/equipment."));
            if (at < activeMount.MountedAt)
                return InvalidUsage(
                    nameof(command.UsedAt),
                    "UsedAt cannot precede the matching tool mount.");
        }
        else if (!string.IsNullOrWhiteSpace(command.MountId)
                 || tool.Status.Equals("Mounted", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ToolUsageRecord>(Error.Conflict(
                "EMS.Tool.InvalidMount", "The requested tool mount is not active."));
        var usage = new ToolUsageRecord(
            $"TUS_{Guid.NewGuid():N}", command.IdempotencyKey.Trim(), hash, tool.ToolId, Text(command.MountId),
            equipmentId, Text(command.ProcessLotId), Text(command.WorkOrderId), Text(command.ProcessId),
            Text(command.RecipeId), command.RecipeVersion, command.UseCount, command.UseMinutes, at, actor,
            Text(command.TraceId), Text(command.ConditionSnapshotJson), DateTime.UtcNow,
            workScopeId, carrierId, activityType, cleaningProgramId,
            cleaningResult is null ? null : Canonical(CleaningResults, cleaningResult));
        if (await _repository.TryRecordUsageAsync(usage, tool.EquipmentClassId, ct))
            return Result.Success(usage);
        var usageWinner = await _repository.GetUsageByIdempotencyKeyAsync(usage.IdempotencyKey, ct);
        return usageWinner is not null
            ? Replay(usageWinner, usageWinner.RequestHash, hash)
            : Result.Failure<ToolUsageRecord>(Error.Conflict("EMS.Tool.ConcurrentUsage", "The tool usage could not be recorded."));
    }

    public async Task<Result<ToolInspectionRecord>> RecordInspectionAsync(ToolInspectionCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) return InvalidInspection(nameof(command.IdempotencyKey), "IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(command.ToolId)) return InvalidInspection(nameof(command.ToolId), "ToolId is required.");
        if (!command.InspectionType.Equals("Inspection", StringComparison.OrdinalIgnoreCase) &&
            !command.InspectionType.Equals("Calibration", StringComparison.OrdinalIgnoreCase))
            return InvalidInspection(nameof(command.InspectionType), "InspectionType must be Inspection or Calibration.");
        if (!command.Result.Equals("Pass", StringComparison.OrdinalIgnoreCase) &&
            !command.Result.Equals("Fail", StringComparison.OrdinalIgnoreCase))
            return InvalidInspection(nameof(command.Result), "Result must be Pass or Fail.");
        var tool = await _repository.GetToolAsync(command.ToolId.Trim(), ct);
        if (tool is null) return Result.Failure<ToolInspectionRecord>(Error.NotFoundOf("Tool", command.ToolId));
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure) return Result.Failure<ToolInspectionRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var at = Utc(command.InspectedAt);
        var type = command.InspectionType.Equals("Calibration", StringComparison.OrdinalIgnoreCase) ? "Calibration" : "Inspection";
        var result = command.Result.Equals("Pass", StringComparison.OrdinalIgnoreCase) ? "Pass" : "Fail";
        var due = command.NextDueAt is null
            ? AddCycle(at, type == "Calibration" ? tool.CalibrationCycleDays : tool.InspectionCycleDays)
            : Utc(command.NextDueAt.Value);
        if (due is not null && due <= at)
            return InvalidInspection(nameof(command.NextDueAt), "NextDueAt must be after InspectedAt.");
        var hash = Hash(tool.ToolId, type, result, at, due, Text(command.MeasuredValue), Text(command.StandardValue),
            Text(command.CertificateNumber), Text(command.Remark), actor);
        var existing = await _repository.GetInspectionByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
        if (existing is not null) return Replay(existing, existing.RequestHash, hash);
        if (!CanInspect(tool))
            return Result.Failure<ToolInspectionRecord>(Error.Conflict(
                "EMS.Tool.NotInspectable", $"Tool '{tool.ToolId}' cannot be inspected in status '{tool.Status}'."));
        var inspection = new ToolInspectionRecord(
            $"TIN_{Guid.NewGuid():N}", command.IdempotencyKey.Trim(), hash, tool.ToolId, type, result,
            Text(command.MeasuredValue), Text(command.StandardValue), Text(command.CertificateNumber), at, actor,
            due, Text(command.Remark), DateTime.UtcNow);
        if (await _repository.TryRecordInspectionAsync(inspection, ct)) return Result.Success(inspection);
        var inspectionWinner = await _repository.GetInspectionByIdempotencyKeyAsync(inspection.IdempotencyKey, ct);
        return inspectionWinner is not null
            ? Replay(inspectionWinner, inspectionWinner.RequestHash, hash)
            : Result.Failure<ToolInspectionRecord>(Error.Conflict("EMS.Tool.ConcurrentInspection", "The inspection could not be recorded."));
    }

    private static Error? ValidateTool(ToolCommand c)
    {
        if (string.IsNullOrWhiteSpace(c.ToolId)) return Error.Validation(nameof(c.ToolId), "ToolId is required.");
        if (string.IsNullOrWhiteSpace(c.ToolName)) return Error.Validation(nameof(c.ToolName), "ToolName is required.");
        if (string.IsNullOrWhiteSpace(c.ToolType)) return Error.Validation(nameof(c.ToolType), "ToolType is required.");
        if (!Statuses.Contains(c.Status)) return Error.Validation(nameof(c.Status), "Unknown tool status.");
        if (c.MaxUseCount is < 0m || c.MaxUseMinutes is < 0m) return Error.Validation("Tool life limits cannot be negative.");
        if (c.InspectionCycleDays is < 1 || c.CalibrationCycleDays is < 1) return Error.Validation("Inspection/calibration cycle must be positive.");
        if (c.ExpectedVersion < 0) return Error.Validation(nameof(c.ExpectedVersion), "ExpectedVersion cannot be negative.");
        if (Text(c.IdempotencyKey)?.Length > 100) return Error.Validation(nameof(c.IdempotencyKey), "IdempotencyKey cannot exceed 100 characters.");
        return null;
    }

    private static DateTime? AddCycle(DateTime at, int? days) => days is > 0 ? at.AddDays(days.Value) : null;
    private static bool CanUse(ToolRecord tool, DateTime at) =>
        tool.IsActive
        && (tool.Status.Equals("Available", StringComparison.OrdinalIgnoreCase)
            || tool.Status.Equals("Mounted", StringComparison.OrdinalIgnoreCase))
        && (tool.MaxUseCount is null || tool.CurrentUseCount < tool.MaxUseCount.Value)
        && (tool.MaxUseMinutes is null || tool.CurrentUseMinutes < tool.MaxUseMinutes.Value)
        && (tool.NextInspectionDueAt is null || tool.NextInspectionDueAt > at)
        && (tool.NextCalibrationDueAt is null || tool.NextCalibrationDueAt > at);
    private static bool CanInspect(ToolRecord tool) =>
        tool.IsActive
        && (tool.Status.Equals("Available", StringComparison.OrdinalIgnoreCase)
            || tool.Status.Equals("Mounted", StringComparison.OrdinalIgnoreCase)
            || tool.Status.Equals("Due", StringComparison.OrdinalIgnoreCase)
            || tool.Status.Equals("Blocked", StringComparison.OrdinalIgnoreCase));
    private static bool WithinLifeAfterUsage(ToolRecord tool, decimal useCount, decimal useMinutes) =>
        (tool.MaxUseCount is null || tool.CurrentUseCount + useCount <= tool.MaxUseCount.Value)
        && (tool.MaxUseMinutes is null || tool.CurrentUseMinutes + useMinutes <= tool.MaxUseMinutes.Value);
    private static string Canonical(HashSet<string> values, string value) => values.First(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime Utc(DateTime value) => value.Kind switch { DateTimeKind.Utc => value, DateTimeKind.Local => value.ToUniversalTime(), _ => DateTime.SpecifyKind(value, DateTimeKind.Utc) };
    private static string Hash(params object?[] values)
        => CanonicalRequestHash.Compute(values);
    private static Result<ToolMountRecord> InvalidMount(string c, string d) => Result.Failure<ToolMountRecord>(Error.Validation(c, d));
    private static Result<ToolUsageRecord> InvalidUsage(string c, string d) => Result.Failure<ToolUsageRecord>(Error.Validation(c, d));
    private static Result<ToolInspectionRecord> InvalidInspection(string c, string d) => Result.Failure<ToolInspectionRecord>(Error.Validation(c, d));
    private static Result<T> Replay<T>(T value, string storedHash, string requestHash)
        => string.Equals(storedHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(value)
            : Result.Failure<T>(Error.Conflict("EMS.Tool.IdempotencyConflict", "The idempotency key was already used for different data."));

    private static Result<ToolRecord> ReplaySave(ToolSaveCommandRecord command, string requestHash)
    {
        if (!string.Equals(command.RequestHash, requestHash, StringComparison.Ordinal))
            return Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.IdempotencyConflict",
                "The idempotency key was already used for different tool-master data."));
        var result = JsonSerializer.Deserialize<ToolRecord>(command.ResultJson);
        return result is null
            ? Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.IdempotencyStateConflict", "The persisted tool command result is invalid."))
            : Result.Success(result);
    }
}
