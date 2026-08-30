using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkScopes;

/// <summary>
/// Claims one durable, current projection and invokes the project policy outside a database
/// transaction. The store remains the authority for all lease/current/version fences at commit.
/// </summary>
public sealed class WorkScopeProjectionProcessor
{
    private static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromHours(1);
    private readonly IWorkScopeProjectionStore _store;
    private readonly IWorkScopeProjectionPolicy _policy;
    private readonly TimeSpan _leaseDuration;

    internal WorkScopeProjectionProcessor(
        IWorkScopeProjectionStore store,
        IWorkScopeProjectionPolicy policy,
        TimeSpan? leaseDuration = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _leaseDuration = Clamp(
            leaseDuration ?? TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(15));
    }

    internal Task EnsureReadyAsync(CancellationToken ct = default) =>
        _store.EnsureReadyAsync(ct);

    internal async Task<WorkScopeProjectionCommitResult?> ProcessNextAsync(
        string leaseOwner,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Trim().Length > 200)
            throw new ArgumentException("A lease owner up to 200 characters is required.", nameof(leaseOwner));

        var claim = await _store.TryClaimNextAsync(
            leaseOwner.Trim(), _leaseDuration, ct).ConfigureAwait(false);
        if (claim is null) return null;

        WorkScopeProjectionDecision decision;
        try
        {
            // The policy seam is deliberately synchronous and receives immutable DTOs. It must not
            // hold a database transaction or observe process-local time while deciding.
            decision = _policy.Decide(new WorkScopeProjectionContext(claim.Event, claim.WorkScope))
                ?? throw new ProjectionDecisionValidationException(
                    "Projection.InvalidDecision", "Projection policy returned null.");
            var prepared = ProjectionDecisionCodec.Prepare(_policy.Identity, claim.Event, decision);
            return await _store.CommitDecisionAsync(claim, prepared, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The database lease is intentionally left to expire. A replacement worker can reclaim
            // it without an unsafe best-effort write during host shutdown.
            throw;
        }
        catch (ProjectionDecisionValidationException ex)
        {
            return await _store.RecordFailureAsync(
                claim,
                _policy.Identity,
                ex.Code,
                ex.Message,
                quarantine: true,
                MinRetry,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await _store.RecordFailureAsync(
                claim,
                _policy.Identity,
                "Projection.PolicyException",
                Trim(ex.Message, 2_000) ?? ex.GetType().Name,
                quarantine: false,
                ExceptionBackoff(claim.AttemptCount),
                ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan ExceptionBackoff(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        return Clamp(TimeSpan.FromSeconds(Math.Pow(2, exponent)), MinRetry, TimeSpan.FromMinutes(15));
    }

    internal static TimeSpan BoundedRetry(TimeSpan requested) =>
        Clamp(requested, MinRetry, MaxRetry);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

/// <summary>
/// Hosted projection loop discovered through the Server's <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
/// composition. Each batch delegates durable claiming and fenced commit to the processor/store pair.
/// </summary>
public sealed class WorkScopeProjectionWorker : BackgroundService, IWorkScopeProjectionRuntime
{
    private readonly WorkScopeProjectionProcessor _processor;
    private readonly string _leaseOwner;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;
    private readonly bool _enabled;

    public WorkScopeProjectionWorker(
        WorkScopeProjectionProcessor processor,
        string? leaseOwner = null,
        TimeSpan? pollInterval = null,
        int batchSize = 50,
        bool enabled = false)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _leaseOwner = NormalizeOwner(leaseOwner);
        _pollInterval = pollInterval is { } configured && configured > TimeSpan.Zero
            ? configured > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : configured
            : TimeSpan.FromSeconds(2);
        _batchSize = Math.Clamp(batchSize, 1, 500);
        _enabled = enabled;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // BackgroundService.StartAsync normally returns as soon as ExecuteAsync reaches its first
        // incomplete await. Run readiness here so Kestrel/HTTP readiness cannot open while an
        // enabled projection worker has a missing schema or insufficient database permissions.
        if (_enabled)
            await _processor.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[WorkScopeProjectionWorker] disabled (enabled=false). Skipping startup.");
            return;
        }

        Console.WriteLine(
            $"[WorkScopeProjectionWorker] started (interval={_pollInterval.TotalMilliseconds:0}ms, batchSize={_batchSize}).");

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkScopeProjectionWorker] batch failed: {ex.Message}");
            }

            if (processed < _batchSize)
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var count = 0;
        while (count < _batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _processor.ProcessNextAsync(_leaseOwner, ct).ConfigureAwait(false);
            if (result is null) break;
            count++;
        }
        return count;
    }

    private static string NormalizeOwner(string? value)
    {
        var owner = string.IsNullOrWhiteSpace(value)
            ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"
            : value.Trim();
        return owner.Length <= 200 ? owner : owner[^200..];
    }
}

internal static class ProjectionDecisionCodec
{
    private const int MaxEffects = 32;
    internal static readonly TimeSpan AcceptedClockSkewAllowance = TimeSpan.FromMinutes(5);

    public static PreparedWorkScopeProjectionDecision Prepare(
        WorkScopeProjectionPolicyIdentity policy,
        WorkScopeProjectionEventDto evidence,
        WorkScopeProjectionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(decision);
        Validate(policy, evidence, decision);

        var bytes = Canonicalize(policy, decision);
        return new PreparedWorkScopeProjectionDecision(
            policy,
            decision,
            Convert.ToHexString(SHA256.HashData(bytes)),
            Encoding.UTF8.GetString(bytes));
    }

    private static void Validate(
        WorkScopeProjectionPolicyIdentity policy,
        WorkScopeProjectionEventDto evidence,
        WorkScopeProjectionDecision decision)
    {
        if (policy.PolicyId.Length > 100 || policy.Version.Length > 100)
            Invalid("Projection.PolicyIdentity", "Policy identity exceeds the durable application boundary.");
        if (evidence.OccurredAt > evidence.AcceptedAt
            && evidence.OccurredAt - evidence.AcceptedAt > AcceptedClockSkewAllowance)
        {
            Invalid(
                "Projection.OccurredAtFutureSkew",
                $"Projection OccurredAt cannot exceed AcceptedAt by more than {AcceptedClockSkewAllowance.TotalMinutes:0} minutes.");
        }
        if (!Enum.IsDefined(decision.Disposition))
            Invalid("Projection.InvalidDisposition", "Projection disposition is invalid.");
        if (decision.ReasonCode.Length is < 1 or > 100)
            Invalid("Projection.InvalidReason", "Projection reason code must contain at most 100 characters.");
        if (decision.Effects.Count > MaxEffects)
            Invalid("Projection.TooManyEffects", $"Projection decisions may contain at most {MaxEffects} effects.");
        if (decision.Disposition == WorkScopeProjectionDisposition.Apply && decision.Effects.Count == 0)
            Invalid("Projection.EmptyApply", "Apply decisions require at least one effect.");
        if (decision.Disposition != WorkScopeProjectionDisposition.Apply && decision.Effects.Count != 0)
            Invalid("Projection.UnexpectedEffects", "Only Apply decisions can contain effects.");
        if (decision.Disposition == WorkScopeProjectionDisposition.Retry)
        {
            if (decision.RetryAfter is not { } retry || retry <= TimeSpan.Zero)
                Invalid("Projection.InvalidRetry", "Retry decisions require a positive delay.");
        }
        else if (decision.RetryAfter is not null)
        {
            Invalid("Projection.InvalidRetry", "Only Retry decisions can specify a delay.");
        }

        ValidateJson(decision.AuditMetadataJson, 4_000, "Projection.InvalidAuditMetadata");
        var evidenceCarriers = evidence.Carriers
            .Select(static carrier => carrier.CarrierId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var effect in decision.Effects)
        {
            if (effect is null || !Enum.IsDefined(effect.Action))
                Invalid("Projection.InvalidEffect", "Projection effect action is invalid.");
            if (effect.Action is WorkScopeAction.Complete or WorkScopeAction.Cancel
                && !evidence.TerminalCleanupCompleted)
            {
                Invalid(
                    "Projection.TerminalCleanupRequired",
                    "Complete or Cancel cannot be applied before terminal cleanup is durable.");
            }
            if (effect.GoodQty is { } good && (!ProductionQuantityBoundary.Fits(good) || good < 0))
                Invalid("Projection.InvalidQuantity", "Good quantity does not fit DECIMAL(18,4).");
            if (effect.DefectQty is { } defect && (!ProductionQuantityBoundary.Fits(defect) || defect < 0))
                Invalid("Projection.InvalidQuantity", "Defect quantity does not fit DECIMAL(18,4).");
            if (effect.GoodQty is { } goodQty && effect.DefectQty is { } defectQty
                && !ProductionQuantityBoundary.TryAdd(goodQty, defectQty, out _))
            {
                Invalid("Projection.InvalidQuantity", "Effect quantities exceed DECIMAL(18,4).");
            }
            if (effect.CarrierId is { } carrierId && !evidenceCarriers.Contains(carrierId))
            {
                Invalid(
                    "Projection.CarrierEvidenceMismatch",
                    $"Carrier '{carrierId}' is not part of the immutable projection evidence.");
            }
            if (effect.CarrierId?.Length > 100 || effect.ResultCode?.Length > 50
                || effect.ResultMetadataJson?.Length > 4_000 || effect.Remark?.Length > 500)
            {
                Invalid("Projection.EffectBoundary", "Projection effect exceeds a POM storage boundary.");
            }
            ValidateJson(effect.ResultMetadataJson, 4_000, "Projection.InvalidResultMetadata");
        }
    }

    private static byte[] Canonicalize(
        WorkScopeProjectionPolicyIdentity policy,
        WorkScopeProjectionDecision decision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("policyId", policy.PolicyId);
            writer.WriteString("policyRevision", policy.Version);
            writer.WriteString("disposition", decision.Disposition.ToString());
            writer.WriteString("reasonCode", decision.ReasonCode);
            writer.WritePropertyName("effects");
            writer.WriteStartArray();
            foreach (var effect in decision.Effects)
            {
                writer.WriteStartObject();
                writer.WriteString("action", effect.Action.ToString());
                WriteDecimal(writer, "goodQty", effect.GoodQty);
                WriteDecimal(writer, "defectQty", effect.DefectQty);
                WriteString(writer, "carrierId", effect.CarrierId);
                WriteString(writer, "resultCode", effect.ResultCode);
                WriteCanonicalJson(writer, "resultMetadata", effect.ResultMetadataJson);
                WriteString(writer, "remark", effect.Remark);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (decision.RetryAfter is { } retry)
                writer.WriteNumber(
                    "retryAfterMilliseconds",
                    (long)Math.Ceiling(WorkScopeProjectionProcessor.BoundedRetry(retry).TotalMilliseconds));
            else
                writer.WriteNull("retryAfterMilliseconds");
            WriteCanonicalJson(writer, "auditMetadata", decision.AuditMetadataJson);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, string name, string? json)
    {
        writer.WritePropertyName(name);
        if (json is null)
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(json);
        WriteElement(writer, document.RootElement);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteElement(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                Invalid("Projection.InvalidJson", "Unsupported JSON value kind.");
                break;
        }
    }

    private static void WriteDecimal(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value.HasValue) writer.WriteNumber(name, value.Value);
        else writer.WriteNull(name);
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void ValidateJson(string? json, int maxLength, string code)
    {
        if (json is null) return;
        if (json.Length > maxLength) Invalid(code, "JSON exceeds the durable storage boundary.");
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ProjectionDecisionValidationException(code, ex.Message);
        }
    }

    [DoesNotReturn]
    private static void Invalid(string code, string message) =>
        throw new ProjectionDecisionValidationException(code, message);
}

internal sealed class ProjectionDecisionValidationException : Exception
{
    public ProjectionDecisionValidationException(string code, string message) : base(message)
        => Code = code;

    public string Code { get; }
}
