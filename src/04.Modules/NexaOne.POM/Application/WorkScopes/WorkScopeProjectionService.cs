using System.Text.Json;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkScopes;

/// <summary>
/// Equipment transport payload를 canonical snapshot으로 정규화하고 durable inbox 결과를
/// 공개 receipt로 번역합니다. Business status mapping은 의도적으로 이 module 밖입니다.
/// </summary>
internal sealed class WorkScopeProjectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IWorkScopeProjectionInbox _repository;

    public WorkScopeProjectionService(IWorkScopeProjectionInbox repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<Result<WorkScopeProjectionReceiptDto>> IngestAsync(
        string sourceClientId,
        WorkScopeProjectionCommand command,
        CancellationToken ct = default)
    {
        if (command is null)
            return Result.Failure<WorkScopeProjectionReceiptDto>(
                Error.Validation(nameof(command), "Projection command is required."));

        var normalizedSource = Normalize(sourceClientId);
        if (!IsIdentifier(normalizedSource, 100)
            || !string.Equals(normalizedSource, Normalize(command.ClientId), StringComparison.Ordinal))
        {
            return Result.Failure<WorkScopeProjectionReceiptDto>(Error.Validation(
                nameof(command.ClientId),
                "The authenticated source client must match command ClientId."));
        }

        var validation = TryNormalize(command, out var snapshot);
        if (validation is not null)
            return Result.Failure<WorkScopeProjectionReceiptDto>(validation);

        var payloadJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        var requestHash = CanonicalRequestHash.Compute(normalizedSource, payloadJson);
        var persisted = await _repository.PersistAsync(new WorkScopeProjectionEnvelope(
            normalizedSource,
            snapshot.EventId,
            requestHash,
            snapshot.WorkScopeId,
            snapshot.EquipmentId,
            snapshot.OperationKey,
            snapshot.PairRunId,
            snapshot.SequenceRunId,
            snapshot.Revision,
            snapshot.Status.ToString(),
            snapshot.TerminalCleanupCompleted,
            snapshot.RecipeId,
            snapshot.RecipeSnapshotHash,
            snapshot.ProgramHash,
            snapshot.Carriers.Select(static carrier => new WorkScopeProjectionCarrierEnvelope(
                carrier.Lane, carrier.CarrierId, carrier.CleaningRunId)).ToArray(),
            JsonSerializer.Serialize(snapshot.Carriers, JsonOptions),
            snapshot.ResultCode,
            snapshot.ResultMetadataJson,
            snapshot.OccurredAt.UtcDateTime,
            payloadJson), ct).ConfigureAwait(false);

        return persisted.Kind switch
        {
            WorkScopeProjectionPersistKind.Accepted => Success(persisted, replayed: false),
            WorkScopeProjectionPersistKind.Replayed => Success(persisted, replayed: true),
            WorkScopeProjectionPersistKind.EventHashConflict => Conflict(
                "Projection.EventHashConflict",
                $"Event '{snapshot.EventId}' was already accepted with a different request hash."),
            WorkScopeProjectionPersistKind.SequenceIdentityConflict => Conflict(
                "Projection.SequenceIdentityConflict",
                $"Sequence '{snapshot.SequenceRunId}' is already bound to another work scope, operation, or pair run."),
            WorkScopeProjectionPersistKind.WorkScopeBindingConflict => Conflict(
                "Projection.WorkScopeBindingConflict",
                $"Work scope '{snapshot.WorkScopeId}' is already bound to another equipment projection stream."),
            WorkScopeProjectionPersistKind.ScopeNotFound =>
                Result.Failure<WorkScopeProjectionReceiptDto>(
                    Error.NotFoundOf("WorkScope", snapshot.WorkScopeId)),
            WorkScopeProjectionPersistKind.ScopeEquipmentConflict => Conflict(
                "Projection.ScopeEquipmentConflict",
                $"Work scope '{snapshot.WorkScopeId}' does not belong to equipment '{snapshot.EquipmentId}'."),
            WorkScopeProjectionPersistKind.AuthorityMissing => Conflict(
                "Projection.AuthorityRequired",
                $"Work scope '{snapshot.WorkScopeId}' has no trusted projection authority."),
            WorkScopeProjectionPersistKind.AuthorityIdentityMismatch => Conflict(
                "Projection.Authority.IdentityMismatch",
                "Projection stream identity does not match the provisioned authority."),
            WorkScopeProjectionPersistKind.RecipeSnapshotHashMismatch => Conflict(
                "Projection.RecipeSnapshotHashMismatch",
                "Projection recipe snapshot hash does not match the provisioned authority."),
            WorkScopeProjectionPersistKind.ProgramHashMismatch => Conflict(
                "Projection.ProgramHashMismatch",
                "Projection program hash does not match the provisioned authority."),
            _ => Result.Failure<WorkScopeProjectionReceiptDto>(
                Error.Failure("Projection.Persistence", "Projection persistence returned an unknown outcome.")),
        };
    }

    private static Result<WorkScopeProjectionReceiptDto> Success(
        WorkScopeProjectionPersistResult persisted,
        bool replayed) => Result.Success(new WorkScopeProjectionReceiptDto(
        persisted.SourceClientId,
        persisted.EventId,
        persisted.WorkScopeId,
        replayed,
        persisted.IsCurrent,
        persisted.CurrentRevision,
        persisted.AcceptedAt));

    private static Result<WorkScopeProjectionReceiptDto> Conflict(string code, string message) =>
        Result.Failure<WorkScopeProjectionReceiptDto>(Error.Conflict(code, message));

    private static Error? TryNormalize(
        WorkScopeProjectionCommand command,
        out NormalizedWorkScopeProjection snapshot)
    {
        snapshot = default!;
        if (!Enum.IsDefined(command.Status))
            return Error.Validation(nameof(command.Status), "Projection status is invalid.");
        if (command.Revision <= 0)
            return Error.Validation(nameof(command.Revision), "Projection revision must be greater than zero.");
        if (command.OccurredAt == default)
            return Error.Validation(nameof(command.OccurredAt), "Projection occurrence time is required.");
        if (command.TerminalCleanupCompleted
            && command.Status is not WorkScopeProjectionStatus.Completed
                and not WorkScopeProjectionStatus.Abandoned)
        {
            return Error.Validation(
                nameof(command.TerminalCleanupCompleted),
                "Terminal cleanup can be completed only for Completed or Abandoned projections.");
        }

        var eventId = Normalize(command.EventId);
        var workScopeId = Normalize(command.WorkScopeId);
        var equipmentId = Normalize(command.EquipmentId);
        var operationKey = Normalize(command.OperationKey);
        var pairRunId = Normalize(command.PairRunId);
        var sequenceRunId = Normalize(command.SequenceRunId);
        var recipeId = Normalize(command.RecipeId);
        var resultCode = Normalize(command.ResultCode);
        if (!IsIdentifier(eventId, 200)
            || !IsIdentifier(workScopeId, 50)
            || !IsIdentifier(equipmentId, 100)
            || !IsIdentifier(operationKey, 200)
            || !IsIdentifier(pairRunId, 100)
            || !IsIdentifier(sequenceRunId, 100)
            || !IsIdentifier(recipeId, 100)
            || !IsIdentifier(resultCode, 100))
        {
            return Error.Validation("Projection.Identity", "Projection identifiers are blank, too long, or contain control characters.");
        }

        var recipeHash = NormalizeHash(command.RecipeSnapshotHash);
        var programHash = NormalizeHash(command.ProgramHash);
        if (recipeHash is null || programHash is null)
            return Error.Validation("Projection.Hash", "RecipeSnapshotHash and ProgramHash must be 64 hexadecimal characters.");

        if (command.Carriers is null
            || command.Carriers.Count != 2
            || command.Carriers.Any(static carrier => carrier is null))
            return Error.Validation(nameof(command.Carriers), "A Cleaner pair snapshot must contain exactly two carriers.");
        var carriers = command.Carriers
            .Select(static carrier => new WorkScopeProjectionCarrierDto(
                Normalize(carrier.Lane).ToLowerInvariant(),
                Normalize(carrier.CarrierId),
                Normalize(carrier.CleaningRunId)))
            .OrderBy(static carrier => carrier.Lane, StringComparer.Ordinal)
            .ToArray();
        if (carriers.Any(static carrier =>
                !IsIdentifier(carrier.Lane, 30)
                || !IsIdentifier(carrier.CarrierId, 100)
                || !IsIdentifier(carrier.CleaningRunId, 100))
            || carriers.Select(static carrier => carrier.Lane).Distinct(StringComparer.Ordinal).Count() != carriers.Length
            || carriers.Select(static carrier => carrier.CarrierId).Distinct(StringComparer.Ordinal).Count() != carriers.Length
            || carriers.Select(static carrier => carrier.CleaningRunId).Distinct(StringComparer.Ordinal).Count() != carriers.Length)
        {
            return Error.Validation(
                nameof(command.Carriers),
                "Carrier lanes, carrier identities, and cleaning-run identities must be valid and distinct.");
        }

        string? metadata = null;
        if (!string.IsNullOrWhiteSpace(command.ResultMetadataJson))
        {
            if (command.ResultMetadataJson.Length > 64_000)
                return Error.Validation(nameof(command.ResultMetadataJson), "Result metadata is too large.");
            try
            {
                using var document = JsonDocument.Parse(command.ResultMetadataJson);
                metadata = JsonSerializer.Serialize(document.RootElement, JsonOptions);
            }
            catch (JsonException)
            {
                return Error.Validation(nameof(command.ResultMetadataJson), "Result metadata must be valid JSON.");
            }
        }

        snapshot = new NormalizedWorkScopeProjection(
            eventId,
            workScopeId,
            equipmentId,
            operationKey,
            pairRunId,
            sequenceRunId,
            command.Status,
            command.TerminalCleanupCompleted,
            recipeId,
            recipeHash,
            programHash,
            carriers,
            command.OccurredAt.ToUniversalTime(),
            command.Revision,
            resultCode,
            metadata);
        return null;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsIdentifier(string value, int maxLength) =>
        value.Length is > 0 && value.Length <= maxLength
        && value.All(static character => !char.IsControl(character));

    private static string? NormalizeHash(string? value)
    {
        var normalized = Normalize(value).ToUpperInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    private sealed record NormalizedWorkScopeProjection(
        string EventId,
        string WorkScopeId,
        string EquipmentId,
        string OperationKey,
        string PairRunId,
        string SequenceRunId,
        WorkScopeProjectionStatus Status,
        bool TerminalCleanupCompleted,
        string RecipeId,
        string RecipeSnapshotHash,
        string ProgramHash,
        IReadOnlyList<WorkScopeProjectionCarrierDto> Carriers,
        DateTimeOffset OccurredAt,
        long Revision,
        string ResultCode,
        string? ResultMetadataJson);
}
