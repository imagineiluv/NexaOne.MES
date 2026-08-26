using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.EMS.Application.Ems;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.MaintenanceExecution;

/// <summary>
/// Owns immutable checklist evidence and optimistic labor sessions for manual maintenance.
/// </summary>
public sealed class MaintenanceExecutionService
{
    private const int IdLength = 50;
    private static readonly HashSet<string> LaborTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Work", "Inspection", "Travel", "Standby",
    };

    private readonly IMaintenanceExecutionRepository _repository;

    public MaintenanceExecutionService(IMaintenanceExecutionRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<Result<MaintenanceCheckRecord>> RecordCheckAsync(
        MaintenanceCheckCommand? command,
        CancellationToken ct = default)
    {
        var context = Normalize(command?.Command);
        if (context.IsFailure) return Result.Failure<MaintenanceCheckRecord>(context.Error);
        var validation = ValidateCheck(command);
        if (validation is not null) return Result.Failure<MaintenanceCheckRecord>(validation);
        ArgumentNullException.ThrowIfNull(command);

        var recordedAt = Utc(command.RecordedAt);
        decimal? measured = command.MeasuredValue is null
            ? null
            : decimal.Round(command.MeasuredValue.Value, 6, MidpointRounding.AwayFromZero);
        var requestHash = CanonicalRequestHash.Compute(
            command.CheckResultId.Trim(), command.WorkOrderId.Trim(), Text(command.ItemId),
            command.ItemSequence, command.CheckName.Trim(), measured, Text(command.AttributeValue),
            Text(command.Unit), command.IsPass, Text(command.Finding), recordedAt,
            context.Value.ActorId, context.Value.ClientChannel, context.Value.DeviceId,
            context.Value.CorrelationId);

        var replay = await _repository.GetCheckByIdempotencyKeyAsync(
            context.Value.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, replay.RequestHash, requestHash);

        var status = await _repository.GetWorkOrderStatusAsync(command.WorkOrderId.Trim(), ct);
        if (status is null)
            return Result.Failure<MaintenanceCheckRecord>(
                Error.NotFoundOf("WorkOrder", command.WorkOrderId.Trim()));
        if (!status.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<MaintenanceCheckRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.WorkOrderNotActive",
                "Checklist evidence can only be recorded while the work order is InProgress."));

        var itemId = Text(command.ItemId);
        if (itemId is not null && !await _repository.MaintenanceItemExistsAsync(itemId, ct))
            return Result.Failure<MaintenanceCheckRecord>(Error.NotFoundOf("MaintenanceItem", itemId));

        var record = new MaintenanceCheckRecord(
            command.CheckResultId.Trim(), context.Value.IdempotencyKey, requestHash,
            command.WorkOrderId.Trim(), itemId, command.ItemSequence, command.CheckName.Trim(),
            measured, Text(command.AttributeValue), Text(command.Unit), command.IsPass,
            Text(command.Finding), context.Value.ActorId, recordedAt,
            context.Value.ClientChannel, context.Value.DeviceId, context.Value.CorrelationId,
            DateTime.UtcNow);

        if (await _repository.TryAddCheckAsync(record, ct)) return Result.Success(record);
        replay = await _repository.GetCheckByIdempotencyKeyAsync(context.Value.IdempotencyKey, ct);
        return replay is not null
            ? Replay(replay, replay.RequestHash, requestHash)
            : Result.Failure<MaintenanceCheckRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.CheckConflict",
                "The checklist sequence or work-order state changed concurrently."));
    }

    public async Task<Result<MaintenanceLaborRecord>> StartLaborAsync(
        MaintenanceLaborStartCommand? command,
        CancellationToken ct = default)
    {
        var context = Normalize(command?.Command);
        if (context.IsFailure) return Result.Failure<MaintenanceLaborRecord>(context.Error);
        var validation = ValidateLaborStart(command);
        if (validation is not null) return Result.Failure<MaintenanceLaborRecord>(validation);
        ArgumentNullException.ThrowIfNull(command);

        var laborType = LaborTypes.First(value =>
            value.Equals(command.LaborType.Trim(), StringComparison.OrdinalIgnoreCase));
        var startedAt = Utc(command.StartedAt);
        var requestedWorkerId = Text(command.WorkerId);
        var requestHash = CanonicalRequestHash.Compute(
            command.LaborId.Trim(), command.WorkOrderId.Trim(), laborType, startedAt,
            requestedWorkerId, Text(command.Remark), context.Value.ActorId,
            context.Value.ClientChannel, context.Value.DeviceId, context.Value.CorrelationId);

        var replay = await _repository.GetLaborByStartIdempotencyKeyAsync(
            context.Value.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, replay.StartRequestHash, requestHash);

        var status = await _repository.GetWorkOrderStatusAsync(command.WorkOrderId.Trim(), ct);
        if (status is null)
            return Result.Failure<MaintenanceLaborRecord>(
                Error.NotFoundOf("WorkOrder", command.WorkOrderId.Trim()));
        if (!status.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<MaintenanceLaborRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.WorkOrderNotActive",
                "Labor can only start while the work order is InProgress."));

        var mappedWorkerId = await _repository.GetActiveWorkerIdAsync(
            context.Value.ActorId, startedAt, ct);
        if (requestedWorkerId is not null
            && !string.Equals(requestedWorkerId, mappedWorkerId, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<MaintenanceLaborRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.WorkerMappingMismatch",
                "The requested worker is not actively mapped to the authenticated user."));
        }

        var now = DateTime.UtcNow;
        var record = new MaintenanceLaborRecord(
            command.LaborId.Trim(), context.Value.IdempotencyKey, requestHash,
            command.WorkOrderId.Trim(), context.Value.ActorId, requestedWorkerId ?? mappedWorkerId,
            laborType, startedAt, null, null, null, Text(command.Remark),
            context.Value.CorrelationId, context.Value.ClientChannel, context.Value.DeviceId,
            null, null, null, null, 1, now, now);

        if (await _repository.TryStartLaborAsync(record, ct)) return Result.Success(record);
        replay = await _repository.GetLaborByStartIdempotencyKeyAsync(context.Value.IdempotencyKey, ct);
        return replay is not null
            ? Replay(replay, replay.StartRequestHash, requestHash)
            : Result.Failure<MaintenanceLaborRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.LaborStartConflict",
                "An open labor session already exists or the work-order state changed concurrently."));
    }

    public async Task<Result<MaintenanceLaborRecord>> CompleteLaborAsync(
        MaintenanceLaborCompleteCommand? command,
        CancellationToken ct = default)
    {
        var context = Normalize(command?.Command);
        if (context.IsFailure) return Result.Failure<MaintenanceLaborRecord>(context.Error);
        if (command is null || !ValidId(command.LaborId))
            return Result.Failure<MaintenanceLaborRecord>(
                Error.Validation(nameof(MaintenanceLaborCompleteCommand.LaborId), "LaborId is required and cannot exceed 50 characters."));
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedVersion < 1)
            return Result.Failure<MaintenanceLaborRecord>(
                Error.Validation(nameof(command.ExpectedVersion), "ExpectedVersion must be positive."));
        if (Text(command.Remark)?.Length > 500)
            return Result.Failure<MaintenanceLaborRecord>(
                Error.Validation(nameof(command.Remark), "Remark cannot exceed 500 characters."));

        var endedAt = Utc(command.EndedAt);
        var requestHash = CanonicalRequestHash.Compute(
            command.LaborId.Trim(), command.ExpectedVersion, endedAt, Text(command.Remark),
            context.Value.ActorId, context.Value.ClientChannel, context.Value.DeviceId,
            context.Value.CorrelationId);
        var replay = await _repository.GetLaborByEndIdempotencyKeyAsync(
            context.Value.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, replay.EndRequestHash ?? string.Empty, requestHash);

        var current = await _repository.GetLaborAsync(command.LaborId.Trim(), ct);
        if (current is null)
            return Result.Failure<MaintenanceLaborRecord>(
                Error.NotFoundOf("MaintenanceLabor", command.LaborId.Trim()));
        if (current.EndedAt is not null)
            return Result.Failure<MaintenanceLaborRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.LaborAlreadyCompleted",
                "The labor session is already completed."));
        if (current.Version != command.ExpectedVersion)
            return Result.Failure<MaintenanceLaborRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.LaborVersionConflict",
                $"Expected labor version {command.ExpectedVersion}, current {current.Version}."));
        if (endedAt < current.StartedAt)
            return Result.Failure<MaintenanceLaborRecord>(
                Error.Validation(nameof(command.EndedAt), "EndedAt cannot precede StartedAt."));

        var hours = decimal.Round((decimal)(endedAt - current.StartedAt).TotalHours,
            4, MidpointRounding.AwayFromZero);
        var completed = current with
        {
            EndedAt = endedAt,
            EndedBy = context.Value.ActorId,
            LaborHours = hours,
            Remark = Text(command.Remark) ?? current.Remark,
            CorrelationId = context.Value.CorrelationId ?? current.CorrelationId,
            EndIdempotencyKey = context.Value.IdempotencyKey,
            EndRequestHash = requestHash,
            EndClientChannel = context.Value.ClientChannel,
            EndDeviceId = context.Value.DeviceId,
            Version = current.Version + 1,
            UpdatedAt = DateTime.UtcNow,
        };

        if (await _repository.TryCompleteLaborAsync(completed, command.ExpectedVersion, ct))
            return Result.Success(completed);
        replay = await _repository.GetLaborByEndIdempotencyKeyAsync(context.Value.IdempotencyKey, ct);
        return replay is not null
            ? Replay(replay, replay.EndRequestHash ?? string.Empty, requestHash)
            : Result.Failure<MaintenanceLaborRecord>(Error.Conflict(
                "EMS.MaintenanceExecution.LaborCompleteConflict",
                "The labor session changed concurrently."));
    }

    private static Result<MaintenanceCommandContext> Normalize(EmsCommandContextDto? command)
        => command is null
            ? Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(command), "Command context is required."))
            : MaintenanceCommandContext.Create(
                command.ActorId, command.IdempotencyKey, command.ClientChannel,
                command.DeviceId, command.CorrelationId);

    private static Error? ValidateCheck(MaintenanceCheckCommand? command)
    {
        if (command is null) return Error.Validation(nameof(command), "Command is required.");
        if (!ValidId(command.CheckResultId))
            return Error.Validation(nameof(command.CheckResultId), "CheckResultId is required and cannot exceed 50 characters.");
        if (!ValidId(command.WorkOrderId))
            return Error.Validation(nameof(command.WorkOrderId), "WorkOrderId is required and cannot exceed 50 characters.");
        if (command.ItemSequence < 1)
            return Error.Validation(nameof(command.ItemSequence), "ItemSequence must be positive.");
        if (string.IsNullOrWhiteSpace(command.CheckName) || command.CheckName.Trim().Length > 200)
            return Error.Validation(nameof(command.CheckName), "CheckName is required and cannot exceed 200 characters.");
        if (!OptionalLength(command.ItemId, 50) || !OptionalLength(command.AttributeValue, 100)
            || !OptionalLength(command.Unit, 50) || !OptionalLength(command.Finding, 1000))
            return Error.Validation("CheckEvidence", "A checklist field exceeds its supported length.");
        if (command.MeasuredValue is null && Text(command.AttributeValue) is null
            && command.IsPass is null && Text(command.Finding) is null)
            return Error.Validation("CheckEvidence", "At least one measurement, attribute, pass result, or finding is required.");
        return null;
    }

    private static Error? ValidateLaborStart(MaintenanceLaborStartCommand? command)
    {
        if (command is null) return Error.Validation(nameof(command), "Command is required.");
        if (!ValidId(command.LaborId))
            return Error.Validation(nameof(command.LaborId), "LaborId is required and cannot exceed 50 characters.");
        if (!ValidId(command.WorkOrderId))
            return Error.Validation(nameof(command.WorkOrderId), "WorkOrderId is required and cannot exceed 50 characters.");
        if (string.IsNullOrWhiteSpace(command.LaborType) || !LaborTypes.Contains(command.LaborType.Trim()))
            return Error.Validation(nameof(command.LaborType), "LaborType must be Work, Inspection, Travel, or Standby.");
        if (!OptionalLength(command.WorkerId, 50) || !OptionalLength(command.Remark, 500))
            return Error.Validation("Labor", "WorkerId or Remark exceeds its supported length.");
        return null;
    }

    private static Result<T> Replay<T>(T value, string storedHash, string requestHash)
        => string.Equals(storedHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(value)
            : Result.Failure<T>(Error.Conflict(
                "EMS.MaintenanceExecution.IdempotencyConflict",
                "The idempotency key was already used for different maintenance data."));

    private static bool ValidId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= IdLength;
    private static bool OptionalLength(string? value, int max)
        => string.IsNullOrWhiteSpace(value) || value.Trim().Length <= max;
    private static string? Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime Utc(DateTime value) => value == default
        ? DateTime.UtcNow
        : value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
