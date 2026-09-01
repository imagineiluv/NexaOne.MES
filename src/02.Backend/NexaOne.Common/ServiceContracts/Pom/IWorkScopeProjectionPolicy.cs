namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// Durable equipment evidence를 WorkScope 상태 전이로 해석하는 project-owned policy seam입니다.
/// 구현은 현재 시각, 난수, I/O 또는 외부 상태를 읽지 않고 입력만으로 같은 결정을 반환해야 합니다.
/// </summary>
public interface IWorkScopeProjectionPolicy
{
    WorkScopeProjectionPolicyIdentity Identity { get; }

    WorkScopeProjectionDecision Decide(WorkScopeProjectionContext context);
}

/// <summary>설정과 감사 로그에서 policy 구현을 안정적으로 식별하는 불변 identity입니다.</summary>
public sealed record WorkScopeProjectionPolicyIdentity
{
    public string PolicyId { get; }
    public string Version { get; }

    public WorkScopeProjectionPolicyIdentity(string policyId, string version)
    {
        PolicyId = Required(policyId, nameof(policyId), 100);
        Version = Required(version, nameof(version), 50);
    }

    private static string Required(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A policy identity value is required.", parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maxLength} characters.");
        return normalized;
    }
}

/// <summary>
/// 인증된 source identity와 durable inbox가 확정한 접수 시각을 포함하는 mapper 입력입니다.
/// SourceClientId는 transport body의 ClientId가 아니라 인증 계층이 확정한 값이어야 합니다.
/// </summary>
public sealed record WorkScopeProjectionEventDto(
    string SourceClientId,
    string EventId,
    string RequestHash,
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
    DateTimeOffset AcceptedAt,
    long SourceRevision,
    string ResultCode,
    string? ResultMetadataJson = null);

/// <summary>policy가 해석할 immutable equipment evidence와 현재 WorkScope snapshot입니다.</summary>
public sealed record WorkScopeProjectionContext(
    WorkScopeProjectionEventDto Event,
    WorkScopeDto WorkScope);

/// <summary>projection processor가 policy 결정을 처리하는 방법입니다.</summary>
public enum WorkScopeProjectionDisposition
{
    Apply,
    Observe,
    Retry,
    Quarantine,
}

/// <summary>
/// WorkScope operation으로 번역될 순서가 보존된 effect입니다. ExpectedVersion과 멱등 키는
/// processor가 현재 snapshot 및 event identity로부터 결정하므로 project policy가 생성하지 않습니다.
/// </summary>
public sealed record WorkScopeProjectionEffect
{
    public WorkScopeAction Action { get; }
    public decimal? GoodQty { get; }
    public decimal? DefectQty { get; }
    public string? CarrierId { get; }
    public string? ResultCode { get; }
    public string? ResultMetadataJson { get; }
    public string? Remark { get; }

    public WorkScopeProjectionEffect(
        WorkScopeAction action,
        decimal? goodQty = null,
        decimal? defectQty = null,
        string? carrierId = null,
        string? resultCode = null,
        string? resultMetadataJson = null,
        string? remark = null)
    {
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action), "WorkScope action is invalid.");
        if (goodQty is < 0)
            throw new ArgumentOutOfRangeException(nameof(goodQty), "Good quantity cannot be negative.");
        if (defectQty is < 0)
            throw new ArgumentOutOfRangeException(nameof(defectQty), "Defect quantity cannot be negative.");

        Action = action;
        GoodQty = goodQty;
        DefectQty = defectQty;
        CarrierId = Optional(carrierId, nameof(carrierId), 100);
        ResultCode = Optional(resultCode, nameof(resultCode), 50);
        ResultMetadataJson = Optional(resultMetadataJson, nameof(resultMetadataJson), 4_000);
        Remark = Optional(remark, nameof(remark), 500);
    }

    private static string? Optional(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maxLength} characters.");
        return normalized;
    }
}

/// <summary>project policy의 순수 판정과 processor가 순서대로 적용할 effect 목록입니다.</summary>
public sealed record WorkScopeProjectionDecision
{
    private static readonly IReadOnlyList<WorkScopeProjectionEffect> NoEffects =
        Array.Empty<WorkScopeProjectionEffect>();

    public WorkScopeProjectionDisposition Disposition { get; }
    public string ReasonCode { get; }
    public IReadOnlyList<WorkScopeProjectionEffect> Effects { get; }
    public TimeSpan? RetryAfter { get; }
    public string? AuditMetadataJson { get; }

    private WorkScopeProjectionDecision(
        WorkScopeProjectionDisposition disposition,
        string reasonCode,
        IReadOnlyList<WorkScopeProjectionEffect>? effects,
        TimeSpan? retryAfter,
        string? auditMetadataJson)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), "Projection disposition is invalid.");
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A projection reason code is required.", nameof(reasonCode));

        var normalizedReason = reasonCode.Trim();
        if (normalizedReason.Length > 100)
            throw new ArgumentOutOfRangeException(nameof(reasonCode), "Reason code cannot exceed 100 characters.");

        var snapshot = effects?.ToArray() ?? Array.Empty<WorkScopeProjectionEffect>();
        if (snapshot.Any(static effect => effect is null))
            throw new ArgumentException("Effects cannot contain null values.", nameof(effects));
        if (disposition == WorkScopeProjectionDisposition.Apply && snapshot.Length == 0)
            throw new ArgumentException("Apply decisions require at least one effect.", nameof(effects));
        if (disposition != WorkScopeProjectionDisposition.Apply && snapshot.Length != 0)
            throw new ArgumentException("Only Apply decisions can contain effects.", nameof(effects));
        if (disposition == WorkScopeProjectionDisposition.Retry && retryAfter is not { } delay)
            throw new ArgumentException("Retry decisions require RetryAfter.", nameof(retryAfter));
        if (disposition != WorkScopeProjectionDisposition.Retry && retryAfter is not null)
            throw new ArgumentException("Only Retry decisions can specify RetryAfter.", nameof(retryAfter));
        if (retryAfter is { } invalidDelay && invalidDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryAfter), "RetryAfter must be positive.");

        Disposition = disposition;
        ReasonCode = normalizedReason;
        Effects = Array.AsReadOnly(snapshot);
        RetryAfter = retryAfter;
        AuditMetadataJson = OptionalAudit(auditMetadataJson);
    }

    public static WorkScopeProjectionDecision Apply(
        string reasonCode,
        IEnumerable<WorkScopeProjectionEffect> effects,
        string? auditMetadataJson = null) => new(
        WorkScopeProjectionDisposition.Apply,
        reasonCode,
        effects?.ToArray() ?? throw new ArgumentNullException(nameof(effects)),
        null,
        auditMetadataJson);

    public static WorkScopeProjectionDecision Observe(
        string reasonCode,
        string? auditMetadataJson = null) => new(
        WorkScopeProjectionDisposition.Observe,
        reasonCode,
        NoEffects,
        null,
        auditMetadataJson);

    public static WorkScopeProjectionDecision Retry(
        string reasonCode,
        TimeSpan retryAfter,
        string? auditMetadataJson = null) => new(
        WorkScopeProjectionDisposition.Retry,
        reasonCode,
        NoEffects,
        retryAfter,
        auditMetadataJson);

    public static WorkScopeProjectionDecision Quarantine(
        string reasonCode,
        string? auditMetadataJson = null) => new(
        WorkScopeProjectionDisposition.Quarantine,
        reasonCode,
        NoEffects,
        null,
        auditMetadataJson);

    private static string? OptionalAudit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Length > 4_000)
            throw new ArgumentOutOfRangeException(nameof(value), "Audit metadata cannot exceed 4000 characters.");
        return normalized;
    }
}
