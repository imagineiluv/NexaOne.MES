namespace NexaOne.ServiceContracts.Fdc;

/// <summary>
/// 프로젝트가 opaque FDC action key를 실제 PLC/STO 명령으로 해석하는 필수 seam이다.
/// 구현은 같은 <see cref="FdcInterlockActionRequest.EffectId"/> 재호출을 멱등하게 처리해야 하며,
/// 명령을 adapter 자체 durable journal/controller에 EffectId로 수락한 뒤 실제 출력/상태 readback까지
/// 확인해야 성공을 반환한다. FDC DB의 Prepared 기록이 실패해도 물리 STOP을 억제하지 않기 때문에,
/// 이 durable acceptance와 Reconcile inventory가 action→MES DB crash window의 최종 fail-safe 경계다.
/// 구현은 전달된 cancellation을 장치 명령의 deadline/fence로 강제해야 한다. 특히 Release는 timeout 뒤
/// 늦게 물리 해제를 완료해서는 안 되며, 이 보장은 caller의 WaitAsync만으로 대신할 수 없다.
/// 여러 EffectId가 같은 물리 출력(STOP/STO 등)을 공유할 때는 adapter/controller가 출력별 활성 EffectId
/// 집합을 영속 관리하고 마지막 소유자가 해제될 때만 출력을 deassert해야 한다.
/// </summary>
public interface IFdcInterlockActionPort
{
    /// <summary>
    /// 기동 전에 모든 필수 action key와 물리 통신/readback 경로가 사용 가능한지 검증한다.
    /// 반환값의 outstanding inventory에는 adapter/controller가 durable하게 수락했지만 아직 물리 해제하지
    /// 않은 모든 EffectId가 포함돼야 한다. 이 목록이 MES DB의 Prepared INSERT 유실 구간을 복구한다.
    /// </summary>
    Task<FdcInterlockActionReadiness> CheckReadyAsync(
        IReadOnlyCollection<string> requiredActions,
        CancellationToken ct = default);

    /// <summary>EffectId를 멱등 키로 action을 적용하고 장치 승인과 물리 상태 판독 결과를 함께 반환한다.</summary>
    Task<FdcInterlockActionResult> ApplyAsync(
        FdcInterlockActionRequest request,
        CancellationToken ct = default);

    /// <summary>재시작 시 EffectId의 물리 적용 상태를 조회하고 필요하면 멱등하게 재적용한다.</summary>
    Task<FdcInterlockActionResult> ReconcileAsync(
        FdcInterlockActionRequest request,
        CancellationToken ct = default);

    /// <summary>정상화된 EffectId를 프로젝트 reset 정책에 따라 해제하고 물리 readback까지 확인한다.
    /// cancellation 이후에는 미적용으로 확정되거나 더 강한 STOP 상태로 fenced되어야 한다.</summary>
    Task<FdcInterlockReleaseResult> ReleaseAsync(
        FdcInterlockReleaseRequest request,
        CancellationToken ct = default);
}

public sealed record FdcInterlockActionRequest(
    string EffectId,
    string RuleId,
    string EquipmentId,
    string ParameterId,
    decimal TriggerValue,
    string Action,
    bool IsRecovery,
    DateTime TriggeredAt,
    string Message)
{
    /// <summary>
    /// 선택적 runtime fence extension. Positional constructor/Deconstruct는 기존 plugin ABI를 유지한다.
    /// Production adapter는 이 값의 fence token을 controller journal의 high-water와 비교하고
    /// controller 현재 UTC가 LeaseExpiresAt 이상이면 요청을 거부해야 한다.
    /// </summary>
    public FdcRuntimeAuthority? RuntimeAuthority { get; init; }

    /// <summary>
    /// RuntimeAuthority가 positional parameter였던 직전 계약과 이미 컴파일된 plugin의 binary ABI를
    /// 보존한다. 짧은 legacy constructor도 primary constructor로 계속 제공한다.
    /// </summary>
    public FdcInterlockActionRequest(
        string EffectId,
        string RuleId,
        string EquipmentId,
        string ParameterId,
        decimal TriggerValue,
        string Action,
        bool IsRecovery,
        DateTime TriggeredAt,
        string Message,
        FdcRuntimeAuthority? RuntimeAuthority = null)
        : this(
            EffectId,
            RuleId,
            EquipmentId,
            ParameterId,
            TriggerValue,
            Action,
            IsRecovery,
            TriggeredAt,
            Message)
    {
        this.RuntimeAuthority = RuntimeAuthority;
    }

    public void Deconstruct(
        out string effectId,
        out string ruleId,
        out string equipmentId,
        out string parameterId,
        out decimal triggerValue,
        out string action,
        out bool isRecovery,
        out DateTime triggeredAt,
        out string message,
        out FdcRuntimeAuthority? runtimeAuthority)
    {
        effectId = EffectId;
        ruleId = RuleId;
        equipmentId = EquipmentId;
        parameterId = ParameterId;
        triggerValue = TriggerValue;
        action = Action;
        isRecovery = IsRecovery;
        triggeredAt = TriggeredAt;
        message = Message;
        runtimeAuthority = RuntimeAuthority;
    }
}

/// <summary>
/// 프로젝트 action adapter/controller가 durable하게 수락했고 아직 release하지 않은 물리 effect 증거다.
/// 같은 EffectId는 inventory에 정확히 한 번만 나타나야 한다.
/// </summary>
public sealed record FdcInterlockOutstandingEffect(
    FdcInterlockActionRequest Request,
    string ApplyAcknowledgementId,
    DateTime ApplyConfirmedAt);

public sealed record FdcInterlockActionReadiness(
    bool IsAvailable,
    bool CancellationFencingConfirmed,
    string? Detail,
    IReadOnlyCollection<FdcInterlockOutstandingEffect> OutstandingEffects)
{
    /// <summary>공유 출력의 활성 EffectId 집합을 controller가 durable하게 관리한다는 명시적 증거다.</summary>
    public bool AggregateEffectOwnershipConfirmed { get; init; }

    /// <summary>controller가 runtime fence high-water를 영속하고 stale token을 거부한다는 명시적 증거다.</summary>
    public bool RuntimeFencePersistenceConfirmed { get; init; }

    /// <summary>직전 6-field positional 계약과 이미 컴파일된 adapter의 binary ABI를 보존한다.</summary>
    public FdcInterlockActionReadiness(
        bool IsAvailable,
        bool CancellationFencingConfirmed,
        string? Detail,
        IReadOnlyCollection<FdcInterlockOutstandingEffect> OutstandingEffects,
        bool AggregateEffectOwnershipConfirmed = false,
        bool RuntimeFencePersistenceConfirmed = false)
        : this(IsAvailable, CancellationFencingConfirmed, Detail, OutstandingEffects)
    {
        this.AggregateEffectOwnershipConfirmed = AggregateEffectOwnershipConfirmed;
        this.RuntimeFencePersistenceConfirmed = RuntimeFencePersistenceConfirmed;
    }

    public void Deconstruct(
        out bool isAvailable,
        out bool cancellationFencingConfirmed,
        out string? detail,
        out IReadOnlyCollection<FdcInterlockOutstandingEffect> outstandingEffects,
        out bool aggregateEffectOwnershipConfirmed,
        out bool runtimeFencePersistenceConfirmed)
    {
        isAvailable = IsAvailable;
        cancellationFencingConfirmed = CancellationFencingConfirmed;
        detail = Detail;
        outstandingEffects = OutstandingEffects;
        aggregateEffectOwnershipConfirmed = AggregateEffectOwnershipConfirmed;
        runtimeFencePersistenceConfirmed = RuntimeFencePersistenceConfirmed;
    }

    /// <summary>
    /// 기본 준비 완료 결과를 만든다. cancellation/deadline fencing은 필수이지만, 공유 출력의
    /// EffectId aggregate ownership과 runtime fence 영속성은 호출자가 각각 명시적으로 확인해야 한다.
    /// 두 확인값의 기본값은 fail-closed(false)이며, 실제 controller journal/readback 및 HIL 증거 없이
    /// true로 지정하면 안 된다.
    /// </summary>
    public static FdcInterlockActionReadiness Ready(
        IReadOnlyCollection<FdcInterlockOutstandingEffect>? outstandingEffects = null) =>
        new(
            true,
            true,
            null,
            outstandingEffects ?? Array.Empty<FdcInterlockOutstandingEffect>());

    /// <summary>
    /// 기존 Ready ABI와 구분해 aggregate ownership과 runtime fencing 증거를 명시적으로 선언한다.
    /// 두 bool은 실제 controller journal/readback/HIL 증거가 있을 때만 true여야 한다.
    /// </summary>
    public static FdcInterlockActionReadiness ReadyWithEvidence(
        bool aggregateEffectOwnershipConfirmed,
        bool runtimeFencePersistenceConfirmed,
        IReadOnlyCollection<FdcInterlockOutstandingEffect>? outstandingEffects = null) =>
        new(
            true,
            true,
            null,
            outstandingEffects ?? Array.Empty<FdcInterlockOutstandingEffect>())
        {
            AggregateEffectOwnershipConfirmed = aggregateEffectOwnershipConfirmed,
            RuntimeFencePersistenceConfirmed = runtimeFencePersistenceConfirmed,
        };

    public static FdcInterlockActionReadiness Unavailable(string detail) =>
        new(
            false,
            false,
            detail,
            Array.Empty<FdcInterlockOutstandingEffect>());
}

public sealed record FdcInterlockActionResult(
    bool Acknowledged,
    bool ReadbackConfirmed,
    string? AcknowledgementId,
    string? Detail)
{
    public bool IsConfirmed =>
        Acknowledged && ReadbackConfirmed && !string.IsNullOrWhiteSpace(AcknowledgementId);

    public static FdcInterlockActionResult Confirmed(string acknowledgementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgementId);
        return new(true, true, acknowledgementId, null);
    }
}

public enum FdcInterlockResetPolicy
{
    Automatic,
    ManualRequired
}

public sealed record FdcInterlockReleaseRequest(
    string EffectId,
    string RuleId,
    string EquipmentId,
    string ParameterId,
    string Action,
    decimal NormalizedValue,
    FdcInterlockResetPolicy ResetPolicy,
    bool IsRecovery)
{
    /// <summary>선택적 runtime fence extension. 기존 positional plugin ABI는 유지한다.
    /// Controller는 fence high-water와 LeaseExpiresAt을 모두 검증해야 한다.</summary>
    public FdcRuntimeAuthority? RuntimeAuthority { get; init; }

    /// <summary>직전 runtime-authority positional 계약의 binary ABI를 보존한다.</summary>
    public FdcInterlockReleaseRequest(
        string EffectId,
        string RuleId,
        string EquipmentId,
        string ParameterId,
        string Action,
        decimal NormalizedValue,
        FdcInterlockResetPolicy ResetPolicy,
        bool IsRecovery,
        FdcRuntimeAuthority? RuntimeAuthority = null)
        : this(
            EffectId,
            RuleId,
            EquipmentId,
            ParameterId,
            Action,
            NormalizedValue,
            ResetPolicy,
            IsRecovery)
    {
        this.RuntimeAuthority = RuntimeAuthority;
    }

    public void Deconstruct(
        out string effectId,
        out string ruleId,
        out string equipmentId,
        out string parameterId,
        out string action,
        out decimal normalizedValue,
        out FdcInterlockResetPolicy resetPolicy,
        out bool isRecovery,
        out FdcRuntimeAuthority? runtimeAuthority)
    {
        effectId = EffectId;
        ruleId = RuleId;
        equipmentId = EquipmentId;
        parameterId = ParameterId;
        action = Action;
        normalizedValue = NormalizedValue;
        resetPolicy = ResetPolicy;
        isRecovery = IsRecovery;
        runtimeAuthority = RuntimeAuthority;
    }
}

public sealed record FdcInterlockReleaseResult(
    bool Acknowledged,
    bool ReadbackConfirmed,
    bool ManualResetRequired,
    string? AcknowledgementId,
    string? Detail)
{
    public bool IsConfirmed =>
        Acknowledged && ReadbackConfirmed && !ManualResetRequired
        && !string.IsNullOrWhiteSpace(AcknowledgementId);

    public static FdcInterlockReleaseResult Confirmed(string acknowledgementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgementId);
        return new(true, true, false, acknowledgementId, null);
    }
}
