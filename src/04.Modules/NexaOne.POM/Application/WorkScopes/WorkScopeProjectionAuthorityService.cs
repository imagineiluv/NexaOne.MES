using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkScopes;

internal sealed class WorkScopeProjectionAuthorityService
{
    private readonly IWorkScopeProjectionAuthorityRepository _repository;
    private readonly IWorkScopeProjectionAuthorityValidator _validator;

    public WorkScopeProjectionAuthorityService(
        IWorkScopeProjectionAuthorityRepository repository,
        IWorkScopeProjectionAuthorityValidator validator)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<Result<WorkScopeProjectionAuthorityDto>> GetAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        var normalized = Normalize(workScopeId);
        if (!IsIdentifier(normalized, 50))
            return Failure(Error.Validation(nameof(workScopeId), "Work scope ID is required."));

        var authority = await _repository.GetByWorkScopeIdAsync(normalized, ct).ConfigureAwait(false);
        return authority is null
            ? Failure(Error.NotFoundOf("WorkScopeProjectionAuthority", normalized))
            : Result.Success(ToDto(authority, replay: false));
    }

    public async Task<Result<WorkScopeProjectionAuthorityDto>> ProvisionAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default)
    {
        if (command is null)
            return Failure(Error.Validation(nameof(command), "Projection authority command is required."));

        var normalized = Normalize(command, out var validationError);
        if (validationError is not null) return Failure(validationError);

        var requestHash = CanonicalRequestHash.Compute(
            normalized.WorkScopeId,
            normalized.SourceClientId,
            normalized.EquipmentId,
            normalized.OperationKey,
            normalized.PairRunId,
            normalized.SequenceRunId,
            normalized.RecipeExecutionId,
            normalized.ProgramArtifactId);

        // Exact retries do not depend on the external validator remaining reachable. They read only
        // previously trusted evidence and therefore preserve idempotency during coordinator outages.
        var existing = await _repository.GetByWorkScopeIdAsync(normalized.WorkScopeId, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var sameIdempotencyKey = string.Equals(
                existing.ProvisionIdempotencyKey,
                normalized.IdempotencyKey,
                StringComparison.Ordinal);
            if (sameIdempotencyKey
                && string.Equals(existing.ProvisionRequestHash, requestHash, StringComparison.Ordinal))
            {
                return Result.Success(ToDto(existing, replay: true));
            }

            return sameIdempotencyKey
                ? Failure(Error.Conflict(
                    "Projection.Authority.IdempotencyConflict",
                    "Projection authority idempotency key is already bound to a different request."))
                : Failure(Error.Conflict(
                    "Projection.Authority.EvidenceAlreadyBound",
                    $"Work scope '{normalized.WorkScopeId}' already has different projection authority."));
        }

        var validated = await _validator.ValidateAsync(normalized, ct).ConfigureAwait(false);
        if (validated.IsFailure) return Failure(validated.Error);
        var evidence = Normalize(validated.Value, out validationError);
        if (validationError is not null) return Failure(validationError);
        if (!EvidenceMatchesCommand(evidence, normalized))
        {
            return Failure(Error.Conflict(
                "Projection.Authority.IdentityMismatch",
                "Validated projection authority does not exactly match the provision references."));
        }

        var persisted = await _repository.ProvisionAsync(
            evidence,
            normalized.IdempotencyKey,
            requestHash,
            normalized.ActorId,
            ct).ConfigureAwait(false);

        return persisted.Kind switch
        {
            WorkScopeProjectionAuthorityProvisionKind.Provisioned =>
                Result.Success(ToDto(persisted.Authority!, replay: false)),
            WorkScopeProjectionAuthorityProvisionKind.Replayed =>
                Result.Success(ToDto(persisted.Authority!, replay: true)),
            WorkScopeProjectionAuthorityProvisionKind.ScopeNotFound =>
                Failure(Error.NotFoundOf("WorkScope", normalized.WorkScopeId)),
            WorkScopeProjectionAuthorityProvisionKind.ScopeNotPristine =>
                Failure(Error.Conflict(
                    "Projection.Authority.ScopeNotPristine",
                    $"Work scope '{normalized.WorkScopeId}' is no longer pristine and cannot become projection-owned.")),
            WorkScopeProjectionAuthorityProvisionKind.ScopeIdentityMismatch =>
                Failure(Error.Conflict(
                    "Projection.Authority.ScopeIdentityMismatch",
                    $"Work scope '{normalized.WorkScopeId}' does not match the validated equipment, pair, or recipe identity.")),
            WorkScopeProjectionAuthorityProvisionKind.IdempotencyConflict =>
                Failure(Error.Conflict(
                    "Projection.Authority.IdempotencyConflict",
                    "Projection authority idempotency key is already bound to different evidence.")),
            WorkScopeProjectionAuthorityProvisionKind.EvidenceConflict =>
                Failure(Error.Conflict(
                    "Projection.Authority.EvidenceAlreadyBound",
                    "The recipe execution or projection stream is already bound to another work scope.")),
            _ => Failure(Error.Failure(
                "Projection.Authority.Persistence",
                "Projection authority persistence returned an unknown outcome.")),
        };
    }

    private static WorkScopeProjectionAuthorityProvisionCommand Normalize(
        WorkScopeProjectionAuthorityProvisionCommand command,
        out Error? error)
    {
        var normalized = command with
        {
            WorkScopeId = Normalize(command.WorkScopeId),
            SourceClientId = Normalize(command.SourceClientId),
            EquipmentId = Normalize(command.EquipmentId),
            OperationKey = Normalize(command.OperationKey),
            PairRunId = Normalize(command.PairRunId),
            SequenceRunId = Normalize(command.SequenceRunId),
            RecipeExecutionId = Normalize(command.RecipeExecutionId),
            ProgramArtifactId = Normalize(command.ProgramArtifactId),
            IdempotencyKey = Normalize(command.IdempotencyKey),
            ActorId = Normalize(command.ActorId),
        };
        error = !IsIdentifier(normalized.WorkScopeId, 50)
            || !IsIdentifier(normalized.SourceClientId, 100)
            || !IsIdentifier(normalized.EquipmentId, 100)
            || !IsIdentifier(normalized.OperationKey, 200)
            || !IsIdentifier(normalized.PairRunId, 100)
            || !IsIdentifier(normalized.SequenceRunId, 100)
            || !IsIdentifier(normalized.RecipeExecutionId, 100)
            || !IsIdentifier(normalized.ProgramArtifactId, 200)
            || !IsIdentifier(normalized.IdempotencyKey, 100)
            || !IsIdentifier(normalized.ActorId, 50)
            ? Error.Validation(
                "Projection.Authority.Identity",
                "Projection authority identifiers are blank, too long, or contain control characters.")
            : null;
        return normalized;
    }

    private static WorkScopeProjectionAuthorityEvidence Normalize(
        WorkScopeProjectionAuthorityEvidence evidence,
        out Error? error)
    {
        var normalized = evidence with
        {
            WorkScopeId = Normalize(evidence.WorkScopeId),
            SourceClientId = Normalize(evidence.SourceClientId),
            EquipmentId = Normalize(evidence.EquipmentId),
            OperationKey = Normalize(evidence.OperationKey),
            PairRunId = Normalize(evidence.PairRunId),
            SequenceRunId = Normalize(evidence.SequenceRunId),
            RecipeExecutionId = Normalize(evidence.RecipeExecutionId),
            RecipeId = Normalize(evidence.RecipeId),
            RecipeSnapshotSchema = Normalize(evidence.RecipeSnapshotSchema),
            RecipeSnapshotHash = NormalizeHash(evidence.RecipeSnapshotHash),
            ProgramArtifactId = Normalize(evidence.ProgramArtifactId),
            ProgramSchema = Normalize(evidence.ProgramSchema),
            ProgramHash = NormalizeHash(evidence.ProgramHash),
        };
        error = !IsIdentifier(normalized.WorkScopeId, 50)
            || !IsIdentifier(normalized.SourceClientId, 100)
            || !IsIdentifier(normalized.EquipmentId, 100)
            || !IsIdentifier(normalized.OperationKey, 200)
            || !IsIdentifier(normalized.PairRunId, 100)
            || !IsIdentifier(normalized.SequenceRunId, 100)
            || !IsIdentifier(normalized.RecipeExecutionId, 100)
            || !IsIdentifier(normalized.RecipeId, 100)
            || normalized.RecipeVersion <= 0
            || !IsIdentifier(normalized.RecipeSnapshotSchema, 100)
            || !IsHash(normalized.RecipeSnapshotHash)
            || !IsIdentifier(normalized.ProgramArtifactId, 200)
            || !IsIdentifier(normalized.ProgramSchema, 100)
            || !IsHash(normalized.ProgramHash)
            ? Error.Conflict(
                "Projection.Authority.InvalidEvidence",
                "The authority validator returned incomplete or invalid evidence.")
            : null;
        return normalized;
    }

    private static bool EvidenceMatchesCommand(
        WorkScopeProjectionAuthorityEvidence evidence,
        WorkScopeProjectionAuthorityProvisionCommand command) =>
        string.Equals(evidence.WorkScopeId, command.WorkScopeId, StringComparison.Ordinal)
        && string.Equals(evidence.SourceClientId, command.SourceClientId, StringComparison.Ordinal)
        && string.Equals(evidence.EquipmentId, command.EquipmentId, StringComparison.Ordinal)
        && string.Equals(evidence.OperationKey, command.OperationKey, StringComparison.Ordinal)
        && string.Equals(evidence.PairRunId, command.PairRunId, StringComparison.Ordinal)
        && string.Equals(evidence.SequenceRunId, command.SequenceRunId, StringComparison.Ordinal)
        && string.Equals(evidence.RecipeExecutionId, command.RecipeExecutionId, StringComparison.Ordinal)
        && string.Equals(evidence.ProgramArtifactId, command.ProgramArtifactId, StringComparison.Ordinal);

    private static WorkScopeProjectionAuthorityDto ToDto(
        WorkScopeProjectionAuthorityRecord authority,
        bool replay) => new(
        authority.WorkScopeId,
        authority.SourceClientId,
        authority.EquipmentId,
        authority.OperationKey,
        authority.PairRunId,
        authority.SequenceRunId,
        authority.RecipeExecutionId,
        authority.RecipeId,
        authority.RecipeVersion,
        authority.RecipeSnapshotSchema,
        authority.RecipeSnapshotHash,
        authority.ProgramArtifactId,
        authority.ProgramSchema,
        authority.ProgramHash,
        authority.BaselineVersionNo,
        authority.LastAppliedVersionNo,
        authority.ProvisionIdempotencyKey,
        authority.ProvisionedAt,
        authority.ProvisionedBy,
        replay);

    private static Result<WorkScopeProjectionAuthorityDto> Failure(Error error) =>
        Result.Failure<WorkScopeProjectionAuthorityDto>(error);

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
    private static string NormalizeHash(string? value) => Normalize(value).ToUpperInvariant();
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsIdentifier(string value, int maxLength) =>
        value.Length is > 0 && value.Length <= maxLength
        && value.All(static character => !char.IsControl(character));
}

internal sealed class WorkScopeProjectionAuthorityBridge : IWorkScopeProjectionAuthorityBridge
{
    private readonly WorkScopeProjectionAuthorityService _service;

    public WorkScopeProjectionAuthorityBridge(WorkScopeProjectionAuthorityService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public Task<Result<WorkScopeProjectionAuthorityDto>> ProvisionAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default) => _service.ProvisionAsync(command, ct);

    public Task<Result<WorkScopeProjectionAuthorityDto>> GetAsync(
        string workScopeId,
        CancellationToken ct = default) => _service.GetAsync(workScopeId, ct);
}

/// <summary>
/// Safe default until RMS canonical recipe snapshots and released project program artifacts are
/// composed by a trusted coordinator. A module that uses this validator can read existing authority
/// and replay it, but can never create new authority from caller-provided references.
/// </summary>
public sealed class RejectingWorkScopeProjectionAuthorityValidator
    : IWorkScopeProjectionAuthorityValidator
{
    public Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default) => Task.FromResult(
        Result.Failure<WorkScopeProjectionAuthorityEvidence>(Error.Conflict(
            "Projection.Authority.ValidatorUnavailable",
            "A trusted recipe/program authority coordinator is not configured.")));
}
