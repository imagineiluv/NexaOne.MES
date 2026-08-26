using NexaOne.Common;
using NexaOne.EMS.Domain;

namespace NexaOne.EMS.Application.Ems;

public sealed class MaintenancePlanService
{
    private readonly IMaintenancePlanRepository _planRepo;
    private readonly ISparePartRepository _partRepo;

    public MaintenancePlanService(IMaintenancePlanRepository planRepo, ISparePartRepository partRepo)
    {
        _planRepo = planRepo;
        _partRepo = partRepo;
    }

    // ── Maintenance Plans ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MaintenancePlan>> GetByEquipmentAsync(
        string equipmentId, CancellationToken ct = default)
        => await _planRepo.GetByEquipmentAsync(equipmentId, ct);

    public async Task<IReadOnlyList<MaintenancePlan>> GetByStatusAsync(
        MaintenancePlanStatus status, CancellationToken ct = default)
        => await _planRepo.GetByStatusAsync(status, ct);

    public async Task<Result<MaintenancePlan>> CreatePlanAsync(
        string planId, string planName, string equipmentId, string planType,
        string cycleType, DateTime scheduledDate, decimal estimatedHours, string assigneeId,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure<MaintenancePlan>(normalized.Error);
        command = normalized.Value;

        var requested = MaintenancePlan.Create(planId, planName, equipmentId, planType,
            cycleType, scheduledDate, estimatedHours, assigneeId);
        if (requested.IsFailure) return requested;

        var replay = await IsPlanReplayAsync(planId, "Create", command, ct);
        if (replay.IsFailure) return Result.Failure<MaintenancePlan>(replay.Error);
        if (replay.Value)
            return await ReplayCreatedPlanAsync(requested.Value, command.IdempotencyKey, ct);

        var persisted = await _planRepo.AddWithActionAsync(
            requested.Value,
            NewPlanAction(requested.Value, "Create", null, requested.Value.Status, command),
            ct);
        if (persisted) return requested;

        replay = await IsPlanReplayAsync(planId, "Create", command, ct);
        if (replay.IsFailure) return Result.Failure<MaintenancePlan>(replay.Error);
        return replay.Value
            ? await ReplayCreatedPlanAsync(requested.Value, command.IdempotencyKey, ct)
            : Result.Failure<MaintenancePlan>(ConcurrentPlanWrite("create", planId));
    }

    public async Task<Result> StartPlanAsync(
        string planId,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure(normalized.Error);
        command = normalized.Value;
        var replay = await IsPlanReplayAsync(planId, "Start", command, ct);
        if (replay.IsFailure || replay.Value)
            return replay.IsFailure ? Result.Failure(replay.Error) : Result.Success();

        var plan = await _planRepo.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFoundOf(nameof(MaintenancePlan), planId));
        var fromStatus = plan.Status;
        var r = plan.Start();
        if (r.IsFailure) return r;
        var persisted = await _planRepo.UpdateWithActionAsync(
            plan, NewPlanAction(plan, "Start", fromStatus, plan.Status, command), ct);
        return persisted
            ? Result.Success()
            : await ResolvePlanTransitionRaceAsync(planId, "Start", command, "start", ct);
    }

    public async Task<Result> CompletePlanAsync(
        string planId,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure(normalized.Error);
        command = normalized.Value;
        var replay = await IsPlanReplayAsync(planId, "Complete", command, ct);
        if (replay.IsFailure || replay.Value)
            return replay.IsFailure ? Result.Failure(replay.Error) : Result.Success();

        var plan = await _planRepo.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFoundOf(nameof(MaintenancePlan), planId));
        var fromStatus = plan.Status;
        var r = plan.Complete();
        if (r.IsFailure) return r;
        var persisted = await _planRepo.UpdateWithActionAsync(
            plan, NewPlanAction(plan, "Complete", fromStatus, plan.Status, command), ct);
        return persisted
            ? Result.Success()
            : await ResolvePlanTransitionRaceAsync(planId, "Complete", command, "complete", ct);
    }

    public async Task<Result> CancelPlanAsync(
        string planId,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure(normalized.Error);
        command = normalized.Value;
        var replay = await IsPlanReplayAsync(planId, "Cancel", command, ct);
        if (replay.IsFailure || replay.Value)
            return replay.IsFailure ? Result.Failure(replay.Error) : Result.Success();

        var plan = await _planRepo.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFoundOf(nameof(MaintenancePlan), planId));
        var fromStatus = plan.Status;
        var r = plan.Cancel();
        if (r.IsFailure) return r;
        var persisted = await _planRepo.UpdateWithActionAsync(
            plan, NewPlanAction(plan, "Cancel", fromStatus, plan.Status, command), ct);
        return persisted
            ? Result.Success()
            : await ResolvePlanTransitionRaceAsync(planId, "Cancel", command, "cancel", ct);
    }

    // ── Spare Parts ───────────────────────────────────────────────────────────

    public Task<IReadOnlyList<SparePart>> GetPartsAsync(CancellationToken ct = default)
        => _partRepo.GetAllAsync(ct);

    public Task<IReadOnlyList<SparePart>> GetLowStockPartsAsync(CancellationToken ct = default)
        => _partRepo.GetLowStockAsync(ct);

    public async Task<Result<SparePart>> CreatePartAsync(
        string partId, string partName, string partNumber, string description,
        string unitOfMeasure, decimal currentStock, decimal minStock, decimal maxStock,
        string location, string? equipmentClassId, string actorId,
        CancellationToken ct = default)
    {
        var actor = Trimmed(actorId);
        if (actor is null || actor.Length > 50)
            return Result.Failure<SparePart>(Error.Validation(
                nameof(actorId), "Authenticated spare-part actor is required and cannot exceed 50 characters."));
        var result = SparePart.Create(partId, partName, partNumber, description,
            unitOfMeasure, currentStock, minStock, maxStock, location, equipmentClassId);
        if (result.IsFailure) return result;
        await _partRepo.AddAsync(result.Value, actor, ct);
        return result;
    }

    public async Task<Result> AdjustStockAsync(
        string partId,
        decimal delta,
        SparePartAdjustmentContext context,
        CancellationToken ct = default)
    {
        var normalized = MaintenanceCommandContext.Create(
            context.Command.ActorId, context.Command.IdempotencyKey,
            context.Command.ClientChannel, context.Command.DeviceId,
            context.Command.CorrelationId, context.Command.Source);
        if (normalized.IsFailure) return Result.Failure(normalized.Error);

        var type = context.ResolveTransactionType(delta);
        if (type.IsFailure) return Result.Failure(type.Error);
        if (Trimmed(context.BomItemId) is not null
            && !string.Equals(type.Value, "Usage", StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Validation(
                nameof(context.BomItemId), "BomItemId can only be supplied for a Usage adjustment."));

        var existing = await _partRepo.GetTransactionByIdempotencyKeyAsync(
            normalized.Value.IdempotencyKey, ct);
        if (existing is not null)
        {
            var requestedEquipmentId = Trimmed(context.EquipmentId);
            var requiresUsageLedger = string.Equals(type.Value, "Usage", StringComparison.OrdinalIgnoreCase)
                                      && requestedEquipmentId is not null;
            var same = string.Equals(existing.PartId, partId, StringComparison.OrdinalIgnoreCase)
                       && existing.Delta == delta
                       && string.Equals(existing.TransactionType, type.Value, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(existing.ActorId, normalized.Value.ActorId, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(existing.ClientChannel, normalized.Value.ClientChannel, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(Trimmed(existing.DeviceId), normalized.Value.DeviceId, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(Trimmed(existing.CorrelationId), normalized.Value.CorrelationId, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(Trimmed(existing.WorkOrderId), Trimmed(context.WorkOrderId), StringComparison.OrdinalIgnoreCase)
                       && string.Equals(Trimmed(existing.EquipmentId), Trimmed(context.EquipmentId), StringComparison.OrdinalIgnoreCase)
                       && (existing.Usage is not null) == requiresUsageLedger
                       && string.Equals(Trimmed(existing.Usage?.BomItemId), Trimmed(context.BomItemId), StringComparison.OrdinalIgnoreCase)
                       && (Trimmed(context.FromLocation) is null
                           || string.Equals(Trimmed(existing.FromLocation), Trimmed(context.FromLocation), StringComparison.OrdinalIgnoreCase))
                       && (Trimmed(context.ToLocation) is null
                           || string.Equals(Trimmed(existing.ToLocation), Trimmed(context.ToLocation), StringComparison.OrdinalIgnoreCase))
                       && string.Equals(Trimmed(existing.Remark), Trimmed(context.Remark), StringComparison.Ordinal);
            return same
                ? Result.Success()
                : Result.Failure(Error.Conflict(
                    $"Idempotency key '{normalized.Value.IdempotencyKey}' was already used by another spare-part adjustment."));
        }

        var part = await _partRepo.GetByIdAsync(partId, ct);
        if (part is null)
            return Result.Failure(Error.NotFoundOf(nameof(SparePart), partId));

        var command = normalized.Value;
        var equipmentId = Trimmed(context.EquipmentId);
        var bomItemId = Trimmed(context.BomItemId);
        var workOrderId = Trimmed(context.WorkOrderId);
        var requiresUsage = string.Equals(type.Value, "Usage", StringComparison.OrdinalIgnoreCase)
                            && equipmentId is not null;
        if (bomItemId is not null && equipmentId is null)
            return Result.Failure(Error.Validation(
                nameof(context.BomItemId), "EquipmentId is required when BomItemId is supplied."));
        if (requiresUsage && !await _partRepo.IsUsageScopeValidAsync(
                part.Id, equipmentId!, bomItemId, workOrderId, ct))
            return Result.Failure(Error.Validation(
                nameof(context.EquipmentId),
                "Equipment, BOM item, or maintenance work order does not match this spare-part usage."));

        var balanceBefore = part.CurrentStock;
        var r = part.AdjustStock(delta);
        if (r.IsFailure) return r;

        var inoutId = Guid.NewGuid().ToString("N");
        var transactionAt = DateTime.UtcNow;
        var usage = requiresUsage
            ? new SparePartUsage(
                Guid.NewGuid().ToString("N"), inoutId, part.Id, bomItemId, equipmentId!,
                workOrderId, Math.Abs(delta), command.ActorId, transactionAt,
                Trimmed(context.Remark))
            : null;
        var transaction = new SparePartStockTransaction(
            inoutId, part.Id, type.Value, Math.Abs(delta),
            balanceBefore, part.CurrentStock, command.ActorId, transactionAt,
            command.IdempotencyKey, command.ClientChannel, command.DeviceId,
            command.CorrelationId, workOrderId, equipmentId,
            Trimmed(context.FromLocation) ?? (delta < 0 ? part.Location : null),
            Trimmed(context.ToLocation) ?? (delta > 0 ? part.Location : null),
            Trimmed(context.Remark), usage);
        var persisted = await _partRepo.PersistAdjustmentAsync(transaction, ct);
        return persisted
            ? Result.Success()
            : Result.Failure(Error.Conflict(
                "Spare-part stock changed concurrently; reload the balance before retrying."));
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<Result<bool>> IsPlanReplayAsync(
        string planId,
        string actionType,
        MaintenanceCommandContext command,
        CancellationToken ct)
    {
        var existing = await _planRepo.GetActionByIdempotencyKeyAsync(
            command.IdempotencyKey, ct);
        if (existing is null) return Result.Success(false);

        var same = existing.WorkOrderId is null
                   && string.Equals(existing.PlanId, planId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.ActionType, actionType, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.ActorId, command.ActorId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.Source, command.Source, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.ClientChannel, command.ClientChannel, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(existing.DeviceId), command.DeviceId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(existing.CorrelationId), command.CorrelationId, StringComparison.OrdinalIgnoreCase);
        return same
            ? Result.Success(true)
            : Result.Failure<bool>(PlanIdempotencyConflict(command.IdempotencyKey));
    }

    private async Task<Result<MaintenancePlan>> ReplayCreatedPlanAsync(
        MaintenancePlan requested,
        string idempotencyKey,
        CancellationToken ct)
    {
        var existing = await _planRepo.GetByIdAsync(requested.Id, ct);
        if (existing is null)
            return Result.Failure<MaintenancePlan>(Error.Conflict(
                "EMS.MaintenancePlan.IdempotencyStateConflict",
                "The plan creation idempotency key exists but its maintenance plan is missing."));
        return SamePlanCreatePayload(existing, requested)
            ? Result.Success(existing)
            : Result.Failure<MaintenancePlan>(PlanIdempotencyConflict(idempotencyKey));
    }

    private async Task<Result> ResolvePlanTransitionRaceAsync(
        string planId,
        string actionType,
        MaintenanceCommandContext command,
        string operation,
        CancellationToken ct)
    {
        var replay = await IsPlanReplayAsync(planId, actionType, command, ct);
        if (replay.IsFailure) return Result.Failure(replay.Error);
        return replay.Value
            ? Result.Success()
            : Result.Failure(ConcurrentPlanWrite(operation, planId));
    }

    private static bool SamePlanCreatePayload(MaintenancePlan existing, MaintenancePlan requested) =>
        string.Equals(existing.Id, requested.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.PlanName, requested.PlanName, StringComparison.Ordinal)
        && string.Equals(existing.EquipmentId, requested.EquipmentId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.PlanType, requested.PlanType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.CycleType, requested.CycleType, StringComparison.OrdinalIgnoreCase)
        && existing.ScheduledDate == requested.ScheduledDate
        && existing.EstimatedDurationHours == requested.EstimatedDurationHours
        && string.Equals(existing.AssigneeId, requested.AssigneeId, StringComparison.OrdinalIgnoreCase);

    private static Result<MaintenanceCommandContext> Normalize(MaintenanceCommandContext command) =>
        MaintenanceCommandContext.Create(
            command.ActorId, command.IdempotencyKey, command.ClientChannel,
            command.DeviceId, command.CorrelationId, command.Source);

    private static MaintenancePlanAction NewPlanAction(
        MaintenancePlan plan,
        string actionType,
        MaintenancePlanStatus? fromStatus,
        MaintenancePlanStatus toStatus,
        MaintenanceCommandContext command) => new(
        Guid.NewGuid().ToString("N"), plan.Id, actionType,
        fromStatus?.ToString(), toStatus.ToString(), command.ActorId,
        command.IdempotencyKey, DateTime.UtcNow, command.Source,
        command.ClientChannel, command.DeviceId, command.CorrelationId);

    private static Error PlanIdempotencyConflict(string idempotencyKey) => Error.Conflict(
        "EMS.MaintenancePlan.IdempotencyConflict",
        $"Idempotency key '{idempotencyKey}' was already used for a different maintenance-plan command payload.");

    private static Error ConcurrentPlanWrite(string operation, string planId) => Error.Conflict(
        "EMS.MaintenancePlan.ConcurrentWrite",
        $"Maintenance plan '{planId}' changed concurrently while attempting to {operation} it.");
}
