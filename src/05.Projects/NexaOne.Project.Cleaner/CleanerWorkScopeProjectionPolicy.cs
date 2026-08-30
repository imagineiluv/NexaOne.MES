using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Project.Cleaner;

/// <summary>
/// Carrier 두 대를 세척하는 Cleaner 설비 증거를 기존 WorkScope 상태 전이로 수렴시킵니다.
/// 설비 증거가 cleanup 완료를 선언하기 전에는 terminal 상태를 만들지 않습니다.
/// </summary>
public sealed class CleanerWorkScopeProjectionPolicy : IWorkScopeProjectionPolicy
{
    public const string PolicyId = "nexa.cleaner.work-scope-projection";
    public const string PolicyVersion = "1.0.0";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public WorkScopeProjectionPolicyIdentity Identity { get; } = new(PolicyId, PolicyVersion);

    public WorkScopeProjectionDecision Decide(WorkScopeProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Event);
        ArgumentNullException.ThrowIfNull(context.WorkScope);

        var evidence = context.Event;
        var scope = context.WorkScope;

        if (!string.Equals(evidence.WorkScopeId, scope.WorkScopeId, StringComparison.Ordinal))
            return Quarantine("cleaner.identity.work-scope-mismatch", evidence);
        if (!string.Equals(evidence.EquipmentId, scope.EquipmentId, StringComparison.Ordinal))
            return Quarantine("cleaner.identity.equipment-mismatch", evidence);
        if (!scope.ScopeType.Equals(nameof(WorkScopeType.Other), StringComparison.OrdinalIgnoreCase))
            return Quarantine("cleaner.scope.pair-required", evidence);
        if (!string.Equals(scope.TargetId, evidence.PairRunId, StringComparison.Ordinal))
            return Quarantine("cleaner.scope.pair-run-mismatch", evidence);
        if (!string.IsNullOrWhiteSpace(scope.CarrierId))
            return Quarantine("cleaner.scope.pair-carrier-binding-invalid", evidence);
        if (scope.PlanQty != 2m)
            return Quarantine("cleaner.scope.pair-quantity-invalid", evidence);
        if (!string.Equals(scope.ProcessId, "CLEANING", StringComparison.Ordinal))
            return Quarantine("cleaner.scope.process-invalid", evidence);
        if (!string.IsNullOrWhiteSpace(scope.WorkOrderId))
            return Quarantine("cleaner.scope.work-order-not-allowed", evidence);
        if (!string.IsNullOrWhiteSpace(scope.ProductId))
            return Quarantine("cleaner.scope.product-not-allowed", evidence);
        if (!string.Equals(scope.RecipeId, evidence.RecipeId, StringComparison.Ordinal))
            return Quarantine("cleaner.scope.recipe-mismatch", evidence);
        if (scope.RecipeVersion is null or <= 0)
            return Quarantine("cleaner.scope.recipe-version-required", evidence);

        if (!TryValidateCarrierPair(evidence, out var carrierFailure))
            return Quarantine(carrierFailure!, evidence);
        const string? carrierId = null;
        if (!Enum.IsDefined(evidence.Status))
            return Quarantine("cleaner.evidence.status-invalid", evidence);
        if (!TryNormalizeResultCode(evidence, out var resultCode))
            return Quarantine("cleaner.evidence.result-code-too-long", evidence);
        if (!TryBuildMetadata(evidence, out var metadata))
            return Quarantine("cleaner.evidence.result-metadata-invalid", evidence);
        if (!TryParseScopeStatus(scope.Status, out var scopeStatus))
            return WorkScopeProjectionDecision.Quarantine(
                "cleaner.scope.status-invalid",
                metadata);

        if (scopeStatus is ScopeStatus.Completed or ScopeStatus.Cancelled)
            return WorkScopeProjectionDecision.Quarantine(
                "cleaner.terminal.preexisting",
                metadata);

        return evidence.Status switch
        {
            WorkScopeProjectionStatus.Running => DecideRunning(
                evidence, scope, scopeStatus, carrierId, resultCode, metadata),
            WorkScopeProjectionStatus.RecoveryRequired => DecideRecoveryRequired(
                evidence, scope, carrierId, resultCode, metadata),
            WorkScopeProjectionStatus.Completed when !evidence.TerminalCleanupCompleted =>
                DecidePendingCleanup(evidence, scope, carrierId, resultCode, metadata),
            WorkScopeProjectionStatus.Abandoned when !evidence.TerminalCleanupCompleted =>
                DecidePendingCleanup(evidence, scope, carrierId, resultCode, metadata),
            WorkScopeProjectionStatus.Completed => DecideCompleted(
                evidence, scope, scopeStatus, carrierId, resultCode, metadata),
            WorkScopeProjectionStatus.Abandoned => Apply(
                "cleaner.abandoned.cancel",
                metadata,
                Effect(WorkScopeAction.Cancel, evidence, carrierId, resultCode, metadata)),
            _ => WorkScopeProjectionDecision.Quarantine(
                "cleaner.evidence.status-invalid",
                metadata),
        };
    }

    private static WorkScopeProjectionDecision DecideRunning(
        WorkScopeProjectionEventDto evidence,
        WorkScopeDto scope,
        ScopeStatus scopeStatus,
        string? carrierId,
        string resultCode,
        string metadata)
    {
        var effects = new List<WorkScopeProjectionEffect>(3);
        if (scopeStatus == ScopeStatus.Created)
            effects.Add(Effect(WorkScopeAction.Release, evidence, carrierId, resultCode, metadata));
        if (scope.IsHold)
            effects.Add(Effect(WorkScopeAction.ReleaseHold, evidence, carrierId, resultCode, metadata));
        if (scopeStatus is ScopeStatus.Created or ScopeStatus.Released)
            effects.Add(Effect(WorkScopeAction.Start, evidence, carrierId, resultCode, metadata));

        return effects.Count == 0
            ? WorkScopeProjectionDecision.Observe("cleaner.running.already-started", metadata)
            : WorkScopeProjectionDecision.Apply("cleaner.running.converge", effects, metadata);
    }

    private static WorkScopeProjectionDecision DecideRecoveryRequired(
        WorkScopeProjectionEventDto evidence,
        WorkScopeDto scope,
        string? carrierId,
        string resultCode,
        string metadata) => scope.IsHold
        ? WorkScopeProjectionDecision.Observe("cleaner.recovery.already-held", metadata)
        : Apply(
            "cleaner.recovery.hold",
            metadata,
            Effect(WorkScopeAction.Hold, evidence, carrierId, resultCode, metadata));

    private static WorkScopeProjectionDecision DecidePendingCleanup(
        WorkScopeProjectionEventDto evidence,
        WorkScopeDto scope,
        string? carrierId,
        string resultCode,
        string metadata) => scope.IsHold
        ? WorkScopeProjectionDecision.Observe("cleaner.cleanup-pending.already-held", metadata)
        : Apply(
            "cleaner.cleanup-pending.hold",
            metadata,
            Effect(WorkScopeAction.Hold, evidence, carrierId, resultCode, metadata));

    private static WorkScopeProjectionDecision DecideCompleted(
        WorkScopeProjectionEventDto evidence,
        WorkScopeDto scope,
        ScopeStatus scopeStatus,
        string? carrierId,
        string resultCode,
        string metadata)
    {
        var effects = new List<WorkScopeProjectionEffect>(4);
        if (scopeStatus == ScopeStatus.Created)
            effects.Add(Effect(WorkScopeAction.Release, evidence, carrierId, resultCode, metadata));
        if (scope.IsHold)
            effects.Add(Effect(WorkScopeAction.ReleaseHold, evidence, carrierId, resultCode, metadata));
        if (scopeStatus is ScopeStatus.Created or ScopeStatus.Released)
            effects.Add(Effect(WorkScopeAction.Start, evidence, carrierId, resultCode, metadata));
        effects.Add(Effect(
            WorkScopeAction.Complete,
            evidence,
            carrierId,
            resultCode,
            metadata,
            goodQty: evidence.Carriers.Count,
            defectQty: 0m));

        return WorkScopeProjectionDecision.Apply("cleaner.completed.converge", effects, metadata);
    }

    private static WorkScopeProjectionDecision Apply(
        string reasonCode,
        string metadata,
        params WorkScopeProjectionEffect[] effects) =>
        WorkScopeProjectionDecision.Apply(reasonCode, effects, metadata);

    private static WorkScopeProjectionDecision Quarantine(
        string reasonCode,
        WorkScopeProjectionEventDto evidence) => WorkScopeProjectionDecision.Quarantine(
        reasonCode,
        BuildFallbackMetadata(evidence, reasonCode));

    private static WorkScopeProjectionEffect Effect(
        WorkScopeAction action,
        WorkScopeProjectionEventDto evidence,
        string? carrierId,
        string resultCode,
        string metadata,
        decimal? goodQty = null,
        decimal? defectQty = null) => new(
        action,
        goodQty,
        defectQty,
        carrierId,
        resultCode,
        metadata,
        $"Cleaner projection {evidence.Status}; cleanup={evidence.TerminalCleanupCompleted.ToString().ToLowerInvariant()}.");

    private static bool TryValidateCarrierPair(
        WorkScopeProjectionEventDto evidence,
        out string? failure)
    {
        failure = null;
        if (evidence.Carriers is null || evidence.Carriers.Count != 2)
        {
            failure = "cleaner.evidence.carrier-count-invalid";
            return false;
        }

        var carriers = evidence.Carriers.ToArray();
        if (carriers.Any(static carrier => carrier is null
                || string.IsNullOrWhiteSpace(carrier.CarrierId)
                || string.IsNullOrWhiteSpace(carrier.Lane)
                || string.IsNullOrWhiteSpace(carrier.CleaningRunId)))
        {
            failure = "cleaner.evidence.carrier-invalid";
            return false;
        }
        if (string.Equals(
                carriers[0].CarrierId.Trim(),
                carriers[1].CarrierId.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            failure = "cleaner.evidence.carrier-duplicate";
            return false;
        }
        if (string.Equals(
                carriers[0].CleaningRunId.Trim(),
                carriers[1].CleaningRunId.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            failure = "cleaner.evidence.cleaning-run-duplicate";
            return false;
        }

        var lanes = carriers
            .Select(static carrier => carrier.Lane.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!lanes.SetEquals(["front", "rear"]))
        {
            failure = "cleaner.evidence.lanes-invalid";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeResultCode(
        WorkScopeProjectionEventDto evidence,
        out string resultCode)
    {
        resultCode = string.IsNullOrWhiteSpace(evidence.ResultCode)
            ? $"CLEANER_{evidence.Status.ToString().ToUpperInvariant()}"
            : evidence.ResultCode.Trim();
        return resultCode.Length <= 50;
    }

    private static bool TryBuildMetadata(
        WorkScopeProjectionEventDto evidence,
        out string metadata)
    {
        JsonElement? sourceResultMetadata = null;
        string? sourceResultMetadataHash = null;
        if (!string.IsNullOrWhiteSpace(evidence.ResultMetadataJson))
        {
            try
            {
                using var document = JsonDocument.Parse(evidence.ResultMetadataJson);
                sourceResultMetadata = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                metadata = BuildFallbackMetadata(evidence, "invalid-result-metadata");
                return false;
            }
        }

        var carriers = evidence.Carriers?
            .Where(static carrier => carrier is not null)
            .OrderBy(static carrier => carrier.Lane, StringComparer.Ordinal)
            .ThenBy(static carrier => carrier.CarrierId, StringComparer.Ordinal)
            .Select(static carrier => new MetadataCarrier(
                carrier.Lane,
                carrier.CarrierId,
                carrier.CleaningRunId))
            .ToArray() ?? Array.Empty<MetadataCarrier>();

        metadata = JsonSerializer.Serialize(new ProjectionMetadata(
            PolicyId,
            PolicyVersion,
            evidence.SourceClientId,
            evidence.EventId,
            evidence.RequestHash,
            evidence.EquipmentId,
            evidence.OperationKey,
            evidence.PairRunId,
            evidence.SequenceRunId,
            evidence.SourceRevision,
            evidence.Status.ToString(),
            evidence.TerminalCleanupCompleted,
            evidence.RecipeId,
            evidence.RecipeSnapshotHash,
            evidence.ProgramHash,
            carriers,
            evidence.OccurredAt,
            evidence.AcceptedAt,
            evidence.ResultCode,
            sourceResultMetadata,
            sourceResultMetadataHash),
            MetadataJsonOptions);

        if (metadata.Length <= 4_000)
            return true;

        sourceResultMetadataHash = sourceResultMetadata is null
            ? null
            : Sha256(JsonSerializer.Serialize(sourceResultMetadata.Value, MetadataJsonOptions));
        metadata = JsonSerializer.Serialize(new CompactProjectionMetadata(
            PolicyId,
            PolicyVersion,
            evidence.SourceClientId,
            evidence.EventId,
            evidence.RequestHash,
            evidence.EquipmentId,
            evidence.SequenceRunId,
            evidence.SourceRevision,
            evidence.Status.ToString(),
            evidence.TerminalCleanupCompleted,
            evidence.OccurredAt,
            evidence.AcceptedAt,
            sourceResultMetadataHash),
            MetadataJsonOptions);

        if (metadata.Length <= 4_000)
            return true;

        metadata = BuildFallbackMetadata(evidence, "metadata-compacted");
        return true;
    }

    private static string BuildFallbackMetadata(
        WorkScopeProjectionEventDto evidence,
        string reason) => JsonSerializer.Serialize(new
        {
            policyId = PolicyId,
            policyVersion = PolicyVersion,
            eventFingerprint = Sha256(string.Join(
                "\u001f",
                evidence.SourceClientId,
                evidence.EventId,
                evidence.RequestHash,
                evidence.WorkScopeId,
                evidence.EquipmentId,
                evidence.SourceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            evidence.SourceRevision,
            status = evidence.Status.ToString(),
            reason,
        }, MetadataJsonOptions);

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool TryParseScopeStatus(string value, out ScopeStatus status)
    {
        if (Enum.TryParse(value, true, out status) && Enum.IsDefined(status))
            return true;
        if (string.Equals(value, "Canceled", StringComparison.OrdinalIgnoreCase))
        {
            status = ScopeStatus.Cancelled;
            return true;
        }
        return false;
    }

    private enum ScopeStatus
    {
        Created,
        Released,
        Started,
        Completed,
        Cancelled,
    }

    private sealed record MetadataCarrier(string Lane, string CarrierId, string CleaningRunId);

    private sealed record ProjectionMetadata(
        string PolicyId,
        string PolicyVersion,
        string SourceClientId,
        string EventId,
        string RequestHash,
        string EquipmentId,
        string OperationKey,
        string PairRunId,
        string SequenceRunId,
        long SourceRevision,
        string Status,
        bool TerminalCleanupCompleted,
        string RecipeId,
        string RecipeSnapshotHash,
        string ProgramHash,
        IReadOnlyList<MetadataCarrier> Carriers,
        DateTimeOffset OccurredAt,
        DateTimeOffset AcceptedAt,
        string ResultCode,
        JsonElement? SourceResultMetadata,
        string? SourceResultMetadataSha256);

    private sealed record CompactProjectionMetadata(
        string PolicyId,
        string PolicyVersion,
        string SourceClientId,
        string EventId,
        string RequestHash,
        string EquipmentId,
        string SequenceRunId,
        long SourceRevision,
        string Status,
        bool TerminalCleanupCompleted,
        DateTimeOffset OccurredAt,
        DateTimeOffset AcceptedAt,
        string? SourceResultMetadataSha256);
}
