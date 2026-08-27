using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.EST.Application.Est;

/// <summary>
/// 설비 출력의 검증·멱등성을 소유한다. carrier/LOT/제품 의미 변환은 호출 플러그인에 남겨
/// EST가 특정 장비 프로토콜이나 프로젝트 규칙에 의존하지 않게 한다.
/// </summary>
public sealed class EquipmentOutputService
{
    private readonly IEquipmentOutputRepository _repository;
    private readonly IEquipmentOutputMasterDirectory _masterDirectory;

    public EquipmentOutputService(
        IEquipmentOutputRepository repository,
        IEquipmentOutputMasterDirectory masterDirectory)
    {
        _repository = repository;
        _masterDirectory = masterDirectory;
    }

    public async Task<Result<EquipmentOutputRecord>> RecordAsync(
        EquipmentOutputCommand command,
        CancellationToken ct = default)
    {
        var quantities = CanonicalQuantities.From(command);
        var validation = Validate(command, quantities);
        if (validation is not null)
            return Result.Failure<EquipmentOutputRecord>(validation);

        var carrierId = Text(command.CarrierId);
        var scope = await _masterDirectory.GetScopeAsync(
            command.EquipmentId.Trim(), carrierId, ct);
        if (scope is null)
            return Result.Failure<EquipmentOutputRecord>(Error.Validation(
                nameof(command.EquipmentId), "EquipmentId does not reference an equipment master."));
        if (!scope.IsEquipmentValid)
            return Result.Failure<EquipmentOutputRecord>(Error.Validation(
                nameof(command.EquipmentId), "Equipment must be active before output can be recorded."));
        if (!string.Equals(scope.PlantId, command.PlantId.Trim(), StringComparison.Ordinal))
            return Result.Failure<EquipmentOutputRecord>(Error.Validation(
                nameof(command.PlantId), "Equipment does not belong to the requested plant."));
        if (carrierId is not null && !scope.CarrierExists)
            return Result.Failure<EquipmentOutputRecord>(Error.Validation(
                nameof(command.CarrierId), "CarrierId does not reference a carrier master."));

        var occurredAt = Utc(command.OccurredAt);
        var actorResult = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actorResult.IsFailure)
            return Result.Failure<EquipmentOutputRecord>(actorResult.Error);
        var actor = actorResult.Value;
        var requestHash = Hash(command, quantities, occurredAt, actor);
        var existing = await _repository.GetByIdempotencyKeyAsync(command.IdempotencyKey.Trim(), ct);
        if (existing is not null)
            return Replay(existing, requestHash);

        var source = command.Source.Trim();
        var sourceEventId = Text(command.SourceEventId);
        if (sourceEventId is not null)
        {
            var sourceReplay = await _repository.GetBySourceEventAsync(source, sourceEventId, ct);
            if (sourceReplay is not null)
                return SourceConflict(sourceReplay);
        }

        var record = new EquipmentOutputRecord(
            $"OUT_{Guid.NewGuid():N}",
            command.IdempotencyKey.Trim(),
            requestHash,
            command.PlantId.Trim(),
            command.EquipmentId.Trim(),
            command.OutputType.Trim(),
            carrierId,
            Text(command.ProcessLotId),
            Text(command.WorkOrderId),
            Text(command.ProcessId),
            Text(command.RecipeId),
            command.RecipeVersion,
            quantities.Total,
            quantities.Good,
            quantities.Defect,
            command.Unit.Trim(),
            source,
            sourceEventId,
            actor,
            Text(command.CorrelationId),
            Text(command.MetadataJson),
            occurredAt,
            DateTime.UtcNow,
            command.IsLotOutput);

        if (await _repository.TryAddAsync(record, ct))
            return Result.Success(record);

        existing = await _repository.GetByIdempotencyKeyAsync(record.IdempotencyKey, ct);
        if (existing is not null) return Replay(existing, requestHash);
        if (record.SourceEventId is not null)
        {
            var sourceReplay = await _repository.GetBySourceEventAsync(
                record.Source, record.SourceEventId, ct);
            if (sourceReplay is not null) return SourceConflict(sourceReplay);
        }
        return Result.Failure<EquipmentOutputRecord>(Error.Conflict(
            "EST.Output.ConcurrentWrite", "The output event could not be persisted because of a concurrent write."));
    }

    private static Result<EquipmentOutputRecord> Replay(EquipmentOutputRecord existing, string requestHash)
        => string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(existing)
            : Result.Failure<EquipmentOutputRecord>(Error.Conflict(
                "EST.Output.IdempotencyConflict",
                "The idempotency key has already been used for different output data."));

    private static Result<EquipmentOutputRecord> SourceConflict(EquipmentOutputRecord existing)
        => Result.Failure<EquipmentOutputRecord>(Error.Conflict(
            "EST.Output.SourceEventConflict",
            $"Source event '{existing.Source}/{existing.SourceEventId}' was already recorded as " +
            $"output '{existing.OutputEventId}'. Reuse its original idempotency key."));

    private static Error? Validate(
        EquipmentOutputCommand command,
        CanonicalQuantities quantities)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return Error.Validation(nameof(command.IdempotencyKey), "IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(command.PlantId))
            return Error.Validation(nameof(command.PlantId), "PlantId is required.");
        if (string.IsNullOrWhiteSpace(command.EquipmentId))
            return Error.Validation(nameof(command.EquipmentId), "EquipmentId is required.");
        if (string.IsNullOrWhiteSpace(command.OutputType))
            return Error.Validation(nameof(command.OutputType), "OutputType is required.");
        if (string.IsNullOrWhiteSpace(command.Unit))
            return Error.Validation(nameof(command.Unit), "Unit is required.");
        if (string.IsNullOrWhiteSpace(command.Source))
            return Error.Validation(nameof(command.Source), "Source is required.");
        if (command.OccurredAt == default)
            return Error.Validation(nameof(command.OccurredAt), "OccurredAt is required.");
        if (command.TotalQuantity <= 0m || quantities.Total <= 0m)
            return Error.Validation(nameof(command.TotalQuantity), "TotalQuantity must be greater than zero.");
        if (command.GoodQuantity < 0m || command.DefectQuantity < 0m)
            return Error.Validation("Output quantities cannot be negative.");
        if (command.TotalQuantity > CanonicalQuantities.MaxValue
            || command.GoodQuantity > CanonicalQuantities.MaxValue
            || command.DefectQuantity > CanonicalQuantities.MaxValue)
            return Error.Validation("Output quantities exceed DECIMAL(18,4) storage range.");
        if (quantities.Good + quantities.Defect != quantities.Total)
            return Error.Validation(
                "GoodQuantity + DefectQuantity must equal TotalQuantity after DECIMAL(18,4) normalization.");
        if (command.RecipeVersion is < 0)
            return Error.Validation(nameof(command.RecipeVersion), "RecipeVersion cannot be negative.");
        if (command.IsLotOutput && string.IsNullOrWhiteSpace(command.ProcessLotId))
            return Error.Validation(nameof(command.ProcessLotId), "LOT output requires ProcessLotId.");
        if (string.Equals(command.OutputType.Trim(), "CarrierCleaned", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(command.CarrierId))
                return Error.Validation(nameof(command.CarrierId), "CarrierCleaned output requires CarrierId.");
            if (command.IsLotOutput || !string.IsNullOrWhiteSpace(command.ProcessLotId))
                return Error.Validation(
                    nameof(command.ProcessLotId),
                    "CarrierCleaned is a carrier-only output and cannot reference a process LOT.");
        }
        return null;
    }

    private static string Hash(
        EquipmentOutputCommand c,
        CanonicalQuantities quantities,
        DateTime occurredAt,
        string actor)
        => CanonicalRequestHash.Compute(
            c.PlantId.Trim(), c.EquipmentId.Trim(), c.OutputType.Trim(),
            quantities.Total, quantities.Good, quantities.Defect, c.Unit.Trim(),
            occurredAt, c.Source.Trim(), Text(c.SourceEventId), Text(c.CarrierId),
            Text(c.ProcessLotId), Text(c.WorkOrderId), Text(c.ProcessId), Text(c.RecipeId),
            c.RecipeVersion, Text(c.CorrelationId), Text(c.MetadataJson), actor, c.IsLotOutput);

    private static string? Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private readonly record struct CanonicalQuantities(decimal Total, decimal Good, decimal Defect)
    {
        internal const decimal MaxValue = 99999999999999.9999m;

        internal static CanonicalQuantities From(EquipmentOutputCommand command) => new(
            Normalize(command.TotalQuantity),
            Normalize(command.GoodQuantity),
            Normalize(command.DefectQuantity));

        private static decimal Normalize(decimal value)
            => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
    }
}
