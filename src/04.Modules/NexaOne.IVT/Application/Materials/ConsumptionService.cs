using System.Globalization;
using System.Text.Json;
using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

/// <summary>
/// 자재 소비의 공통 규칙을 숨기는 깊은 모듈. Mode는 분기 힌트일 뿐 외부 서비스 종류가 아니며,
/// 프로젝트 플러그인은 TRACE/설비 신호를 이 계약의 명령으로 번역한다.
/// </summary>
public sealed class ConsumptionService
{
    private const int IdentifierLength = 50;
    private readonly IConsumptionRepository _repository;
    private readonly TraceConsumptionPolicyCatalog _tracePolicies;

    public ConsumptionService(IConsumptionRepository repository)
        : this(repository, new TraceConsumptionPolicyCatalog())
    {
    }

    internal ConsumptionService(
        IConsumptionRepository repository,
        TraceConsumptionPolicyCatalog tracePolicies)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _tracePolicies = tracePolicies ?? throw new ArgumentNullException(nameof(tracePolicies));
    }

    internal Result<TraceConsumptionDecision> EvaluateTrace(
        TraceProjectionItem item,
        TraceProjectionState? state)
        => _tracePolicies.Evaluate(item, state);

    public async Task<Result<MaterialConsumptionDto>> ConsumeAsync(
        MaterialConsumptionCommand command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure)
            return Result.Failure<MaterialConsumptionDto>(normalized.Error);

        var record = normalized.Value;
        var replay = await _repository.GetByIdempotencyKeyAsync(record.IdempotencyKey, ct);
        if (replay is not null)
        {
            return string.Equals(replay.RequestHash, record.RequestHash, StringComparison.Ordinal)
                ? Result.Success(ToDto(replay))
                : Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                    $"Idempotency key '{record.IdempotencyKey}' is already used for a different material consumption."));
        }

        var sourceReplay = await _repository.GetBySourceEventAsync(
            record.SourceSystem, record.SourceEventId, ct);
        if (sourceReplay is not null)
            return Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                $"Source event '{record.SourceSystem}/{record.SourceEventId}' was already recorded as " +
                $"consumption '{sourceReplay.ConsumptionId}'. Reuse its original idempotency key."));

        var lot = await _repository.GetLotAsync(record.MaterialLotId, ct);
        if (lot is null)
            return Result.Failure<MaterialConsumptionDto>(
                Error.NotFoundOf(nameof(MaterialLotBalance), record.MaterialLotId));
        if (!string.Equals(lot.MaterialId, record.MaterialId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                $"Material lot '{lot.LotId}' belongs to material '{lot.MaterialId}', not '{record.MaterialId}'."));
        if (!string.Equals(lot.Unit, record.Unit, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                $"Material lot '{lot.LotId}' uses unit '{lot.Unit}', not '{record.Unit}'."));
        if (lot.CurrentQuantity < record.Quantity)
            return Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                $"Material lot '{lot.LotId}' has {lot.CurrentQuantity} {lot.Unit}; requested {record.Quantity} {record.Unit}."));

        var persisted = await _repository.PersistAsync(record, ct);
        if (!persisted)
        {
            sourceReplay = await _repository.GetBySourceEventAsync(
                record.SourceSystem, record.SourceEventId, ct);
            return Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                sourceReplay is null
                    ? "Material stock changed concurrently or the consumption was already recorded."
                    : $"Source event '{record.SourceSystem}/{record.SourceEventId}' was already recorded as " +
                      $"consumption '{sourceReplay.ConsumptionId}'."));
        }

        return Result.Success(ToDto(record));
    }

    public async Task<Result<MaterialConsumptionDto>> ReverseAsync(
        MaterialConsumptionReversalCommand command,
        CancellationToken ct = default)
    {
        var validation = ValidateReversal(command);
        if (validation is not null)
            return Result.Failure<MaterialConsumptionDto>(validation);

        var reversalId = command.ReversalId.Trim();
        var idempotencyKey = command.IdempotencyKey.Trim();
        var originalId = command.ConsumptionId.Trim();
        var reason = command.Reason.Trim();
        var sourceSystem = command.SourceSystem.Trim();
        var original = await _repository.GetByIdAsync(originalId, ct);
        if (original is null)
            return Result.Failure<MaterialConsumptionDto>(
                Error.NotFoundOf(nameof(ConsumptionRecord), originalId));

        var actorResult = CommandActor.Resolve(command.OperatorId, nameof(command.OperatorId));
        if (actorResult.IsFailure)
            return Result.Failure<MaterialConsumptionDto>(actorResult.Error);
        var actor = actorResult.Value;
        var occurredAt = Utc(command.OccurredAt);
        var correlationId = Clean(command.CorrelationId) ?? original.CorrelationId;
        var requestHash = Hash(
            reversalId, idempotencyKey, originalId, reason,
            occurredAt.ToString("O", CultureInfo.InvariantCulture), sourceSystem, actor,
            correlationId ?? string.Empty);

        var replay = await _repository.GetByIdempotencyKeyAsync(idempotencyKey, ct);
        if (replay is not null)
            return string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal)
                ? Result.Success(ToDto(replay))
                : Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                    $"Idempotency key '{command.IdempotencyKey}' is already used for a different reversal."));

        if (original.ReversalOfId is not null || string.Equals(original.Mode, "Reversal", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<MaterialConsumptionDto>(Error.Conflict("A reversal record cannot be reversed again."));
        if (!string.Equals(original.Status, "Committed", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<MaterialConsumptionDto>(Error.Conflict("Material consumption is already reversed."));

        var reversal = original with
        {
            ConsumptionId = reversalId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            Mode = "Reversal",
            SourceEventId = original.ConsumptionId,
            SourceSystem = sourceSystem,
            OperatorId = actor,
            CorrelationId = correlationId,
            ReversalOfId = original.ConsumptionId,
            Status = "Committed",
            MetadataJson = JsonSerializer.Serialize(new { reason }),
            OccurredAt = occurredAt,
        };

        var persisted = await _repository.PersistReversalAsync(
            original, reversal, reason, ct);
        return persisted
            ? Result.Success(ToDto(reversal))
            : Result.Failure<MaterialConsumptionDto>(Error.Conflict(
                "Material consumption was reversed concurrently."));
    }

    private static Result<ConsumptionRecord> Normalize(MaterialConsumptionCommand command)
    {
        if (command is null)
            return Result.Failure<ConsumptionRecord>(Error.Validation(nameof(command), "Command is required."));

        var required = new (string Name, string? Value, int Max)[]
        {
            (nameof(command.ConsumptionId), command.ConsumptionId, IdentifierLength),
            (nameof(command.IdempotencyKey), command.IdempotencyKey, 100),
            (nameof(command.PlantId), command.PlantId, IdentifierLength),
            (nameof(command.EquipmentId), command.EquipmentId, IdentifierLength),
            (nameof(command.MaterialLotId), command.MaterialLotId, IdentifierLength),
            (nameof(command.MaterialId), command.MaterialId, IdentifierLength),
            (nameof(command.Unit), command.Unit, 30),
            (nameof(command.Mode), command.Mode, 30),
            (nameof(command.SourceSystem), command.SourceSystem, IdentifierLength),
        };
        foreach (var item in required)
        {
            if (string.IsNullOrWhiteSpace(item.Value))
                return Result.Failure<ConsumptionRecord>(Error.Validation(item.Name, $"{item.Name} is required."));
            if (item.Value.Trim().Length > item.Max)
                return Result.Failure<ConsumptionRecord>(Error.Validation(item.Name, $"{item.Name} cannot exceed {item.Max} characters."));
        }
        if (command.Quantity <= 0)
            return Result.Failure<ConsumptionRecord>(Error.Validation(nameof(command.Quantity), "Quantity must be greater than zero."));

        // SQL Server accounting columns use DECIMAL(22,6), while SQLite NUMERIC does not enforce
        // scale. Normalize once at the domain boundary so both engines debit and report exactly the
        // same quantity. Values below one micro-unit must not turn into a zero-value ledger row.
        var quantity = decimal.Round(command.Quantity, 6, MidpointRounding.AwayFromZero);
        if (quantity <= 0)
            return Result.Failure<ConsumptionRecord>(Error.Validation(
                nameof(command.Quantity), "Quantity must be at least 0.000001."));
        if (quantity >= 10_000_000_000_000_000m)
            return Result.Failure<ConsumptionRecord>(Error.Validation(
                nameof(command.Quantity), "Quantity exceeds the DECIMAL(22,6) accounting range."));

        var mode = command.Mode.Trim();
        var sourceEventId = Clean(command.SourceEventId);
        if (string.Equals(mode, "Trace", StringComparison.OrdinalIgnoreCase) && sourceEventId is null)
            return Result.Failure<ConsumptionRecord>(Error.Validation(
                nameof(command.SourceEventId), "Trace consumption requires a source event ID."));
        sourceEventId ??= command.ConsumptionId.Trim();
        if (sourceEventId.Length > 100)
            return Result.Failure<ConsumptionRecord>(Error.Validation(nameof(command.SourceEventId), "SourceEventId cannot exceed 100 characters."));

        var actorResult = CommandActor.Resolve(command.OperatorId, nameof(command.OperatorId));
        if (actorResult.IsFailure)
            return Result.Failure<ConsumptionRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var occurredAt = Utc(command.OccurredAt);
        var values = new[]
        {
            command.ConsumptionId.Trim(), command.IdempotencyKey.Trim(), command.PlantId.Trim(),
            command.EquipmentId.Trim(), command.MaterialLotId.Trim(), command.MaterialId.Trim(),
            quantity.ToString(CultureInfo.InvariantCulture), command.Unit.Trim(), mode,
            occurredAt.ToString("O", CultureInfo.InvariantCulture), sourceEventId,
            Clean(command.ProcessLotId) ?? string.Empty, Clean(command.WorkOrderId) ?? string.Empty,
            Clean(command.ProcessId) ?? string.Empty, Clean(command.RecipeId) ?? string.Empty,
            command.RecipeVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Clean(command.TraceId) ?? string.Empty, Clean(command.TagId) ?? string.Empty,
            command.SourceSystem.Trim(), actor, Clean(command.CorrelationId) ?? string.Empty,
            Clean(command.MetadataJson) ?? string.Empty,
        };

        return Result.Success(new ConsumptionRecord(
            command.ConsumptionId.Trim(), command.IdempotencyKey.Trim(), Hash(values),
            command.PlantId.Trim(), command.EquipmentId.Trim(), command.MaterialLotId.Trim(),
            command.MaterialId.Trim(), Clean(command.ProcessLotId), Clean(command.WorkOrderId),
            Clean(command.ProcessId), Clean(command.RecipeId), command.RecipeVersion, mode,
            quantity, command.Unit.Trim(), Clean(command.TraceId), Clean(command.TagId),
            sourceEventId, command.SourceSystem.Trim(), actor, Clean(command.CorrelationId), null,
            "Committed", Clean(command.MetadataJson), occurredAt));
    }

    private static Error? ValidateReversal(MaterialConsumptionReversalCommand command)
    {
        if (command is null) return Error.Validation(nameof(command), "Command is required.");
        if (string.IsNullOrWhiteSpace(command.ReversalId) || command.ReversalId.Trim().Length > IdentifierLength)
            return Error.Validation(nameof(command.ReversalId), "ReversalId is required and cannot exceed 50 characters.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Trim().Length > 100)
            return Error.Validation(nameof(command.IdempotencyKey), "IdempotencyKey is required and cannot exceed 100 characters.");
        if (string.IsNullOrWhiteSpace(command.ConsumptionId) || command.ConsumptionId.Trim().Length > IdentifierLength)
            return Error.Validation(nameof(command.ConsumptionId), "ConsumptionId is required and cannot exceed 50 characters.");
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 500)
            return Error.Validation(nameof(command.Reason), "Reason is required and cannot exceed 500 characters.");
        if (string.IsNullOrWhiteSpace(command.SourceSystem) || command.SourceSystem.Trim().Length > IdentifierLength)
            return Error.Validation(nameof(command.SourceSystem), "SourceSystem is required and cannot exceed 50 characters.");
        return null;
    }

    private static DateTime Utc(DateTime value) => value == default
        ? DateTime.UtcNow
        : value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Hash(params object?[] values)
        => CanonicalRequestHash.Compute(values);

    private static MaterialConsumptionDto ToDto(ConsumptionRecord r) => new(
        r.ConsumptionId, r.IdempotencyKey, r.PlantId, r.EquipmentId, r.MaterialLotId,
        r.MaterialId, r.Quantity, r.Unit, r.Mode, r.OccurredAt, r.OperatorId, r.SourceSystem,
        r.SourceEventId, r.Status, r.ProcessLotId, r.WorkOrderId, r.ProcessId, r.RecipeId,
        r.RecipeVersion, r.TraceId, r.TagId, r.CorrelationId, r.ReversalOfId, r.MetadataJson);
}
