using NexaOne.Common;
using NexaOne.EMS.Domain;

namespace NexaOne.EMS.Application.Ems;

public sealed class EmsService
{
    private readonly IWorkOrderRepository _workOrderRepository;

    public EmsService(IWorkOrderRepository workOrderRepository)
    {
        _workOrderRepository = workOrderRepository;
    }

    public async Task<Result<IReadOnlyList<WorkOrder>>> GetByEquipmentAsync(
        string equipmentId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var list = await _workOrderRepository.GetByEquipmentAsync(equipmentId, from, to, ct);
        return Result.Success(list);
    }

    public async Task<Result<IReadOnlyList<WorkOrder>>> GetByStatusAsync(
        WorkOrderStatus status, CancellationToken ct = default)
    {
        var list = await _workOrderRepository.GetByStatusAsync(status, ct);
        return Result.Success(list);
    }

    public Task<int> GetCountByStatusAsync(WorkOrderStatus status, CancellationToken ct = default)
        => _workOrderRepository.GetCountByStatusAsync(status, ct);

    public async Task<Result<WorkOrder>> CreateWorkOrderAsync(
        string woId,
        string equipmentId,
        string woType,
        string desc,
        string assigneeId,
        string? maintenancePlanId,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure<WorkOrder>(normalized.Error);
        command = normalized.Value;

        var requested = WorkOrder.Create(
            woId, equipmentId, woType, desc, assigneeId, DateTime.UtcNow, maintenancePlanId);
        if (requested.IsFailure) return requested;

        var replay = await IsReplayAsync(woId, "Create", command, null, ct);
        if (replay.IsFailure) return Result.Failure<WorkOrder>(replay.Error);
        if (replay.Value)
            return await ReplayCreatedWorkOrderAsync(requested.Value, command.IdempotencyKey, ct);

        var persisted = await _workOrderRepository.AddWithActionAsync(
            requested.Value,
            NewAction(requested.Value, "Create", null, requested.Value.Status, command, null),
            ct);
        if (persisted) return requested;

        replay = await IsReplayAsync(woId, "Create", command, null, ct);
        if (replay.IsFailure) return Result.Failure<WorkOrder>(replay.Error);
        return replay.Value
            ? await ReplayCreatedWorkOrderAsync(requested.Value, command.IdempotencyKey, ct)
            : Result.Failure<WorkOrder>(ConcurrentWrite("create", woId));
    }

    public async Task<Result> StartWorkOrderAsync(
        string woId,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure(normalized.Error);
        command = normalized.Value;
        var replay = await IsReplayAsync(woId, "Start", command, null, ct);
        if (replay.IsFailure || replay.Value) return replay.IsFailure ? Result.Failure(replay.Error) : Result.Success();

        var wo = await _workOrderRepository.GetByIdAsync(woId, ct);
        if (wo is null)
            return Result.Failure(Error.NotFoundOf(nameof(WorkOrder), woId));
        var fromStatus = wo.Status;
        var startResult = wo.Start();
        if (startResult.IsFailure) return startResult;

        var persisted = await _workOrderRepository.UpdateWithActionAsync(
            wo, NewAction(wo, "Start", fromStatus, wo.Status, command, null), ct);
        return persisted
            ? Result.Success()
            : await ResolveTransitionWriteRaceAsync(
                woId, "Start", command, null, "start", ct);
    }

    public async Task<Result> CompleteWorkOrderAsync(
        string woId,
        string remark,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure(normalized.Error);
        command = normalized.Value;
        var replay = await IsReplayAsync(woId, "Complete", command, remark, ct);
        if (replay.IsFailure || replay.Value) return replay.IsFailure ? Result.Failure(replay.Error) : Result.Success();

        var wo = await _workOrderRepository.GetByIdAsync(woId, ct);
        if (wo is null)
            return Result.Failure(Error.NotFoundOf(nameof(WorkOrder), woId));
        if (await _workOrderRepository.HasOpenLaborAsync(woId, ct))
            return Result.Failure(Error.Conflict(
                "EMS.WorkOrder.OpenLabor",
                "Complete every open maintenance labor session before completing the work order."));

        var fromStatus = wo.Status;
        var completeResult = wo.Complete(remark);
        if (completeResult.IsFailure) return completeResult;

        var persisted = await _workOrderRepository.UpdateWithActionAsync(
            wo, NewAction(wo, "Complete", fromStatus, wo.Status, command, remark), ct);
        return persisted
            ? Result.Success()
            : await ResolveTransitionWriteRaceAsync(
                woId, "Complete", command, remark, "complete", ct);
    }

    public async Task<Result> CancelWorkOrderAsync(
        string woId,
        MaintenanceCommandContext command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure(normalized.Error);
        command = normalized.Value;
        var replay = await IsReplayAsync(woId, "Cancel", command, null, ct);
        if (replay.IsFailure || replay.Value) return replay.IsFailure ? Result.Failure(replay.Error) : Result.Success();

        var wo = await _workOrderRepository.GetByIdAsync(woId, ct);
        if (wo is null)
            return Result.Failure(Error.NotFoundOf(nameof(WorkOrder), woId));
        if (await _workOrderRepository.HasOpenLaborAsync(woId, ct))
            return Result.Failure(Error.Conflict(
                "EMS.WorkOrder.OpenLabor",
                "Complete every open maintenance labor session before cancelling the work order."));

        var fromStatus = wo.Status;
        var cancelResult = wo.Cancel();
        if (cancelResult.IsFailure) return cancelResult;

        var persisted = await _workOrderRepository.UpdateWithActionAsync(
            wo, NewAction(wo, "Cancel", fromStatus, wo.Status, command, null), ct);
        return persisted
            ? Result.Success()
            : await ResolveTransitionWriteRaceAsync(
                woId, "Cancel", command, null, "cancel", ct);
    }

    private async Task<Result<bool>> IsReplayAsync(
        string workOrderId,
        string actionType,
        MaintenanceCommandContext command,
        string? remark,
        CancellationToken ct)
    {
        var existing = await _workOrderRepository.GetActionByIdempotencyKeyAsync(
            command.IdempotencyKey, ct);
        if (existing is null) return Result.Success(false);

        var same = string.Equals(existing.WorkOrderId, workOrderId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.ActionType, actionType, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.ActorId, command.ActorId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.Source, command.Source, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.ClientChannel, command.ClientChannel, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(existing.DeviceId), command.DeviceId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(existing.CorrelationId), command.CorrelationId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Trimmed(existing.Remark), Trimmed(remark), StringComparison.Ordinal);
        return same
            ? Result.Success(true)
            : Result.Failure<bool>(IdempotencyConflict(command.IdempotencyKey));
    }

    private async Task<Result<WorkOrder>> ReplayCreatedWorkOrderAsync(
        WorkOrder requested,
        string idempotencyKey,
        CancellationToken ct)
    {
        var existing = await _workOrderRepository.GetByIdAsync(requested.Id, ct);
        if (existing is null)
            return Result.Failure<WorkOrder>(Error.Conflict(
                "EMS.WorkOrder.IdempotencyStateConflict",
                "The maintenance creation idempotency key exists but its work order is missing."));

        return SameCreatePayload(existing, requested)
            ? Result.Success(existing)
            : Result.Failure<WorkOrder>(IdempotencyConflict(idempotencyKey));
    }

    private async Task<Result> ResolveTransitionWriteRaceAsync(
        string workOrderId,
        string actionType,
        MaintenanceCommandContext command,
        string? remark,
        string operation,
        CancellationToken ct)
    {
        var replay = await IsReplayAsync(workOrderId, actionType, command, remark, ct);
        if (replay.IsFailure) return Result.Failure(replay.Error);
        return replay.Value
            ? Result.Success()
            : Result.Failure(ConcurrentWrite(operation, workOrderId));
    }

    private static bool SameCreatePayload(WorkOrder existing, WorkOrder requested) =>
        string.Equals(existing.Id, requested.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.EquipmentId, requested.EquipmentId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.WoType, requested.WoType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.Description, requested.Description, StringComparison.Ordinal)
        && string.Equals(existing.AssigneeId, requested.AssigneeId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.PlanId, requested.PlanId, StringComparison.OrdinalIgnoreCase);

    private static Error IdempotencyConflict(string idempotencyKey) => Error.Conflict(
        "EMS.WorkOrder.IdempotencyConflict",
        $"Idempotency key '{idempotencyKey}' was already used for a different maintenance command payload.");

    private static Error ConcurrentWrite(string operation, string workOrderId) => Error.Conflict(
        "EMS.WorkOrder.ConcurrentWrite",
        $"Work order '{workOrderId}' changed concurrently while attempting to {operation} it.");

    private static Result<MaintenanceCommandContext> Normalize(MaintenanceCommandContext command) =>
        MaintenanceCommandContext.Create(
            command.ActorId, command.IdempotencyKey, command.ClientChannel,
            command.DeviceId, command.CorrelationId, command.Source);

    private static MaintenanceAction NewAction(
        WorkOrder workOrder,
        string actionType,
        WorkOrderStatus? fromStatus,
        WorkOrderStatus toStatus,
        MaintenanceCommandContext command,
        string? remark) => new(
        Guid.NewGuid().ToString("N"), workOrder.Id, actionType,
        fromStatus?.ToString(), toStatus.ToString(), command.ActorId,
        command.IdempotencyKey, DateTime.UtcNow, command.Source,
        command.ClientChannel, command.DeviceId, command.CorrelationId, remark);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
