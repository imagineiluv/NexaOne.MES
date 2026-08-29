using NexaOne.Common;
using NexaOne.EMS.Domain;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.EMS.Application.Ems;

public sealed class MaintenancePlanService
{
    private readonly IMaintenancePlanRepository _planRepo;
    private readonly ISparePartRepository _partRepo;
    private readonly IEquipmentDirectory _equipmentDirectory;

    public MaintenancePlanService(
        IMaintenancePlanRepository planRepo,
        ISparePartRepository partRepo,
        IEquipmentDirectory equipmentDirectory)
    {
        _planRepo = planRepo ?? throw new ArgumentNullException(nameof(planRepo));
        _partRepo = partRepo ?? throw new ArgumentNullException(nameof(partRepo));
        _equipmentDirectory = equipmentDirectory
                              ?? throw new ArgumentNullException(nameof(equipmentDirectory));
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
        string location, string? equipmentClassId, MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure<SparePart>(normalized.Error);
        command = normalized.Value;
        var result = SparePart.Create(partId, partName, partNumber, description,
            unitOfMeasure, currentStock, minStock, maxStock, location, equipmentClassId);
        if (result.IsFailure) return result;

        var replay = await _partRepo.GetTransactionByIdempotencyKeyAsync(
            command.IdempotencyKey, ct);
        if (replay is not null)
            return await ReplayCreatedPartAsync(result.Value, replay, command, ct);

        var now = DateTime.UtcNow;
        var opening = new SparePartStockTransaction(
            Guid.NewGuid().ToString("N"), result.Value.Id, "Opening", result.Value.CurrentStock,
            0m, result.Value.CurrentStock, command.ActorId, now,
            command.IdempotencyKey, command.ClientChannel, command.DeviceId,
            command.CorrelationId, ToLocation: result.Value.Location,
            Remark: "Opening balance");
        if (await _partRepo.TryAddWithOpeningBalanceAsync(result.Value, opening, ct))
            return result;

        replay = await _partRepo.GetTransactionByIdempotencyKeyAsync(command.IdempotencyKey, ct);
        if (replay is not null)
            return await ReplayCreatedPartAsync(result.Value, replay, command, ct);
        return Result.Failure<SparePart>(Error.Conflict(
            "EMS.SparePart.IdentityConflict",
            $"Spare part '{result.Value.Id}' already exists or changed concurrently."));
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
        var equipmentId = Trimmed(context.EquipmentId);
        var isUsage = string.Equals(type.Value, "Usage", StringComparison.OrdinalIgnoreCase);
        if (isUsage && equipmentId is null)
            return Result.Failure(Error.Validation(
                nameof(context.EquipmentId),
                "EquipmentId is required for Usage. Use Scrap or Adjustment for an equipment-independent stock decrease."));
        if (Trimmed(context.BomItemId) is not null
            && !isUsage)
            return Result.Failure(Error.Validation(
                nameof(context.BomItemId), "BomItemId can only be supplied for a Usage adjustment."));

        var existing = await _partRepo.GetTransactionByIdempotencyKeyAsync(
            normalized.Value.IdempotencyKey, ct);
        if (existing is not null)
        {
            return SameAdjustment(existing, partId, delta, type.Value, normalized.Value, context, isUsage)
                ? Result.Success()
                : Result.Failure(AdjustmentIdempotencyConflict(normalized.Value.IdempotencyKey));
        }

        EquipmentDirectoryEntry? usageEquipment = null;
        if (isUsage)
        {
            usageEquipment = await _equipmentDirectory.GetEquipmentAsync(equipmentId!, ct);
            if (usageEquipment is null)
                return Result.Failure(Error.NotFoundOf("Equipment", equipmentId!));
            if (!usageEquipment.IsValid)
                return Result.Failure(Error.Conflict(
                    "EMS.SparePart.EquipmentInactive",
                    $"Equipment '{equipmentId}' is not active."));
        }

        var part = await _partRepo.GetByIdAsync(partId, ct);
        if (part is null)
            return Result.Failure(Error.NotFoundOf(nameof(SparePart), partId));

        var command = normalized.Value;
        var bomItemId = Trimmed(context.BomItemId);
        var workOrderId = Trimmed(context.WorkOrderId);
        if (bomItemId is not null && equipmentId is null)
            return Result.Failure(Error.Validation(
                nameof(context.BomItemId), "EquipmentId is required when BomItemId is supplied."));
        if (isUsage && !await _partRepo.IsUsageScopeValidAsync(
                part.Id, equipmentId!, usageEquipment!.EquipmentClassId,
                bomItemId, workOrderId, ct))
            return Result.Failure(Error.Validation(
                nameof(context.EquipmentId),
                "Equipment, BOM item, or maintenance work order does not match this spare-part usage."));

        var balanceBefore = part.CurrentStock;
        var r = part.AdjustStock(delta);
        if (r.IsFailure) return r;

        var inoutId = Guid.NewGuid().ToString("N");
        var transactionAt = DateTime.UtcNow;
        var usage = isUsage
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
        var persisted = await _partRepo.PersistAdjustmentAsync(
            transaction, usageEquipment?.EquipmentClassId, ct);
        if (persisted) return Result.Success();

        var winner = await _partRepo.GetTransactionByIdempotencyKeyAsync(
            command.IdempotencyKey, ct);
        if (winner is not null)
            return SameAdjustment(winner, partId, delta, type.Value, command, context, isUsage)
                ? Result.Success()
                : Result.Failure(AdjustmentIdempotencyConflict(command.IdempotencyKey));
        return Result.Failure(Error.Conflict(
            "Spare-part stock changed concurrently; reload the balance before retrying."));
    }

    private async Task<Result<SparePart>> ReplayCreatedPartAsync(
        SparePart requested,
        SparePartStockTransaction opening,
        MaintenanceCommandContext command,
        CancellationToken ct)
    {
        var persisted = await _partRepo.GetByIdAsync(requested.Id, ct);
        if (persisted is null)
            return Result.Failure<SparePart>(Error.Conflict(
                "EMS.SparePart.IdempotencyStateConflict",
                "The opening-balance idempotency key exists but its spare-part master is missing."));

        var same = string.Equals(opening.PartId, requested.Id, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(opening.TransactionType, "Opening", StringComparison.OrdinalIgnoreCase)
                   && opening.Quantity == requested.CurrentStock
                   && opening.BalanceBefore == 0m
                   && opening.BalanceAfter == requested.CurrentStock
                   && string.Equals(opening.ActorId, command.ActorId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(opening.ClientChannel, command.ClientChannel, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(opening.DeviceId), command.DeviceId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(opening.CorrelationId), command.CorrelationId, StringComparison.OrdinalIgnoreCase)
                   && opening.WorkOrderId is null
                   && opening.EquipmentId is null
                   && opening.FromLocation is null
                   && string.Equals(opening.ToLocation, requested.Location, StringComparison.Ordinal)
                   && string.Equals(opening.Remark, "Opening balance", StringComparison.Ordinal)
                   && SamePart(persisted, requested);
        return same
            ? Result.Success(persisted)
            : Result.Failure<SparePart>(Error.Conflict(
                "EMS.SparePart.IdempotencyConflict",
                $"Idempotency key '{command.IdempotencyKey}' was already used for different spare-part data."));
    }

    private static bool SamePart(SparePart existing, SparePart requested) =>
        string.Equals(existing.Id, requested.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.PartName, requested.PartName, StringComparison.Ordinal)
        && string.Equals(existing.PartNumber, requested.PartNumber, StringComparison.Ordinal)
        && string.Equals(existing.Description, requested.Description, StringComparison.Ordinal)
        && string.Equals(existing.UnitOfMeasure, requested.UnitOfMeasure, StringComparison.OrdinalIgnoreCase)
        && existing.CurrentStock == requested.CurrentStock
        && existing.MinStock == requested.MinStock
        && existing.MaxStock == requested.MaxStock
        && string.Equals(existing.Location, requested.Location, StringComparison.Ordinal)
        && string.Equals(existing.EquipmentClassId, requested.EquipmentClassId, StringComparison.OrdinalIgnoreCase);

    private static bool SameAdjustment(
        SparePartStockTransaction existing,
        string partId,
        decimal delta,
        string transactionType,
        MaintenanceCommandContext command,
        SparePartAdjustmentContext context,
        bool isUsage)
        => string.Equals(existing.PartId, partId, StringComparison.OrdinalIgnoreCase)
           && existing.Delta == delta
           && string.Equals(existing.TransactionType, transactionType, StringComparison.OrdinalIgnoreCase)
           && string.Equals(existing.ActorId, command.ActorId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(existing.ClientChannel, command.ClientChannel, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Trimmed(existing.DeviceId), command.DeviceId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Trimmed(existing.CorrelationId), command.CorrelationId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Trimmed(existing.WorkOrderId), Trimmed(context.WorkOrderId), StringComparison.OrdinalIgnoreCase)
           && string.Equals(Trimmed(existing.EquipmentId), Trimmed(context.EquipmentId), StringComparison.OrdinalIgnoreCase)
           && (existing.Usage is not null) == isUsage
           && string.Equals(Trimmed(existing.Usage?.BomItemId), Trimmed(context.BomItemId), StringComparison.OrdinalIgnoreCase)
           && (!isUsage
               || (existing.Usage is not null
                   && existing.Usage.Quantity == Math.Abs(delta)
                   && string.Equals(Trimmed(existing.Usage.EquipmentId), Trimmed(context.EquipmentId), StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(existing.Usage.WorkOrderId), Trimmed(context.WorkOrderId), StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.Usage.UsedBy, command.ActorId, StringComparison.OrdinalIgnoreCase)))
           && (Trimmed(context.FromLocation) is null
               || string.Equals(Trimmed(existing.FromLocation), Trimmed(context.FromLocation), StringComparison.OrdinalIgnoreCase))
           && (Trimmed(context.ToLocation) is null
               || string.Equals(Trimmed(existing.ToLocation), Trimmed(context.ToLocation), StringComparison.OrdinalIgnoreCase))
           && string.Equals(Trimmed(existing.Remark), Trimmed(context.Remark), StringComparison.Ordinal);

    private static Error AdjustmentIdempotencyConflict(string idempotencyKey) => Error.Conflict(
        "EMS.SparePart.IdempotencyConflict",
        $"Idempotency key '{idempotencyKey}' was already used by another spare-part adjustment.");

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
