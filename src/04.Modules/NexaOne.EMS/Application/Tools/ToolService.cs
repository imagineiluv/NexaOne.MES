using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.Tools;

public sealed class ToolService
{
    private static readonly HashSet<string> Statuses = new(StringComparer.OrdinalIgnoreCase)
        { "Available", "Mounted", "Due", "Blocked", "Retired" };
    private readonly IToolRepository _repository;

    public ToolService(IToolRepository repository) => _repository = repository;

    public async Task<Result<ToolRecord>> SaveAsync(ToolCommand command, CancellationToken ct = default)
    {
        var error = ValidateTool(command);
        if (error is not null) return Result.Failure<ToolRecord>(error);
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure) return Result.Failure<ToolRecord>(actorResult.Error);
        var equipmentClassId = Text(command.EquipmentClassId);
        if (equipmentClassId is not null
            && !await _repository.EquipmentClassExistsAsync(equipmentClassId, ct))
            return Result.Failure<ToolRecord>(Error.NotFoundOf("EquipmentClass", equipmentClassId));

        var existing = await _repository.GetToolAsync(command.ToolId.Trim(), ct);
        var tool = new ToolRecord(
            command.ToolId.Trim(), command.ToolName.Trim(), command.ToolType.Trim(), Text(command.ToolNumber),
            Text(command.SerialNumber), equipmentClassId, command.MaxUseCount, command.MaxUseMinutes,
            existing?.CurrentUseCount ?? 0m, existing?.CurrentUseMinutes ?? 0m,
            command.InspectionCycleDays, command.CalibrationCycleDays,
            existing?.LastInspectedAt, existing?.LastCalibratedAt,
            existing?.NextInspectionDueAt, existing?.NextCalibrationDueAt,
            Canonical(Statuses, command.Status), Text(command.Location), command.IsActive);

        var activeMount = await _repository.GetActiveMountAsync(tool.ToolId, ct);
        if (activeMount is not null)
        {
            if (existing is null
                || !tool.IsActive
                || !string.Equals(tool.Status, existing.Status, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<ToolRecord>(Error.Conflict(
                    "EMS.Tool.ActiveMountState",
                    "A mounted tool must remain active and its lifecycle status cannot be overwritten by a master-data save."));
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

        if (!await _repository.TrySaveToolAsync(tool, existing?.Status, actorResult.Value, ct))
            return Result.Failure<ToolRecord>(Error.Conflict(
                "EMS.Tool.ConcurrentSave",
                "The tool lifecycle state or mount changed concurrently."));
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
        var hash = Hash(toolId, equipmentId, Text(command.PositionCode), at, actor);
        var existing = await _repository.GetMountByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
        if (existing is not null) return Replay(existing, existing.RequestHash, hash);
        var tool = await _repository.GetToolAsync(toolId, ct);
        if (tool is null) return Result.Failure<ToolMountRecord>(Error.NotFoundOf("Tool", command.ToolId));
        if (!await _repository.EquipmentExistsAsync(equipmentId, ct))
            return Result.Failure<ToolMountRecord>(Error.NotFoundOf("Equipment", equipmentId));
        if (!tool.Status.Equals("Available", StringComparison.OrdinalIgnoreCase) || !CanUse(tool, at))
            return Result.Failure<ToolMountRecord>(Error.Conflict("EMS.Tool.NotMountable", $"Tool '{tool.ToolId}' is not available."));
        var mount = new ToolMountRecord(
            $"TMT_{Guid.NewGuid():N}", command.IdempotencyKey.Trim(), hash, tool.ToolId,
            equipmentId, Text(command.PositionCode), at, actor,
            null, null, null, null, null, DateTime.UtcNow);
        if (await _repository.TryMountAsync(mount, ct)) return Result.Success(mount);
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
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure) return Result.Failure<ToolUsageRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var at = Utc(command.UsedAt);
        var hash = Hash(command.ToolId, command.EquipmentId, command.UseCount, command.UseMinutes, at,
            Text(command.MountId), Text(command.ProcessLotId), Text(command.WorkOrderId), Text(command.ProcessId),
            Text(command.RecipeId), command.RecipeVersion, Text(command.TraceId), Text(command.ConditionSnapshotJson), actor);
        var existing = await _repository.GetUsageByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
        if (existing is not null) return Replay(existing, existing.RequestHash, hash);
        var tool = await _repository.GetToolAsync(command.ToolId.Trim(), ct);
        if (tool is null) return Result.Failure<ToolUsageRecord>(Error.NotFoundOf("Tool", command.ToolId));
        var equipmentId = command.EquipmentId.Trim();
        if (!await _repository.EquipmentExistsAsync(equipmentId, ct))
            return Result.Failure<ToolUsageRecord>(Error.NotFoundOf("Equipment", equipmentId));
        if (!CanUse(tool, at) || !WithinLifeAfterUsage(tool, command.UseCount, command.UseMinutes))
            return Result.Failure<ToolUsageRecord>(Error.Conflict("EMS.Tool.NotUsable", $"Tool '{tool.ToolId}' cannot be used in status '{tool.Status}'."));
        var activeMount = await _repository.GetActiveMountAsync(tool.ToolId, ct);
        if (activeMount is not null)
        {
            if (string.IsNullOrWhiteSpace(command.MountId)
                || !string.Equals(activeMount.MountId, command.MountId.Trim(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(activeMount.EquipmentId, equipmentId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<ToolUsageRecord>(Error.Conflict("EMS.Tool.InvalidMount", "The active mount does not match the tool/equipment."));
        }
        else if (!string.IsNullOrWhiteSpace(command.MountId)
                 || tool.Status.Equals("Mounted", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ToolUsageRecord>(Error.Conflict(
                "EMS.Tool.InvalidMount", "The requested tool mount is not active."));
        var usage = new ToolUsageRecord(
            $"TUS_{Guid.NewGuid():N}", command.IdempotencyKey.Trim(), hash, tool.ToolId, Text(command.MountId),
            equipmentId, Text(command.ProcessLotId), Text(command.WorkOrderId), Text(command.ProcessId),
            Text(command.RecipeId), command.RecipeVersion, command.UseCount, command.UseMinutes, at, actor,
            Text(command.TraceId), Text(command.ConditionSnapshotJson), DateTime.UtcNow);
        if (await _repository.TryRecordUsageAsync(usage, ct)) return Result.Success(usage);
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
}
