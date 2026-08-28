namespace NexaOne.ServiceContracts.Fdc;

/// <summary>
/// 프로젝트가 opaque FDC action key를 실제 PLC/STO 명령으로 해석하는 필수 seam이다.
/// 구현은 같은 <see cref="FdcInterlockActionRequest.EffectId"/> 재호출을 멱등하게 처리해야 하며,
/// 명령을 adapter 자체 durable journal/controller에 EffectId로 수락한 뒤 실제 출력/상태 readback까지
/// 확인해야 성공을 반환한다. FDC DB의 Prepared 기록이 실패해도 물리 STOP을 억제하지 않기 때문에,
/// 이 durable acceptance와 Reconcile inventory가 action→MES DB crash window의 최종 fail-safe 경계다.
/// 구현은 전달된 cancellation을 장치 명령의 deadline/fence로 강제해야 한다. 특히 Release는 timeout 뒤
/// 늦게 물리 해제를 완료해서는 안 되며, 이 보장은 caller의 WaitAsync만으로 대신할 수 없다.
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
    string Message);

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
    /// <summary>호출자는 이 factory로 장치 명령의 cancellation/deadline fencing까지 확인했음을 선언한다.</summary>
    public static FdcInterlockActionReadiness Ready(
        IReadOnlyCollection<FdcInterlockOutstandingEffect>? outstandingEffects = null) =>
        new(true, true, null, outstandingEffects ?? Array.Empty<FdcInterlockOutstandingEffect>());

    public static FdcInterlockActionReadiness Unavailable(string detail) =>
        new(false, false, detail, Array.Empty<FdcInterlockOutstandingEffect>());
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
    bool IsRecovery);

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
