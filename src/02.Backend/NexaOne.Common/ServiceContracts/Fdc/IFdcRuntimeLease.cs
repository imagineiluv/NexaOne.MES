using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Fdc;

/// <summary>
/// FDC 실시간 제어 writer의 단일 소유권 계약입니다. 구현은 DB 시간을 기준으로 lease를 판정하고,
/// 새 소유권마다 증가하는 fence token을 반환합니다. fence token은 DB writer 선출 증거일 뿐이므로
/// 실제 설비 controller도 모든 action에서 오래된 token을 거부해야 합니다.
/// </summary>
public interface IFdcRuntimeLease : INexaModuleBridge
{
    Task<FdcRuntimeLeaseAcquireResult> TryAcquireAsync(
        string ownerId,
        string configRevisionSha256,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<FdcRuntimeLeaseGrant?> TryRenewAsync(
        FdcRuntimeLeaseGrant grant,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> TryReleaseAsync(
        FdcRuntimeLeaseGrant grant,
        CancellationToken ct = default);

    Task<FdcRuntimeLeaseState> GetStateAsync(CancellationToken ct = default);
}

/// <summary>
/// GLOBAL writer 행의 공개 상태입니다. lease secret과 그 hash는 의도적으로 노출하지 않습니다.
/// <see cref="HasOwnerTuple"/>는 tuple 존재 여부일 뿐 현재 시각의 유효한 운전 권한을 뜻하지 않습니다.
/// </summary>
public sealed record FdcRuntimeLeaseState(
    string? OwnerId,
    long FenceToken,
    DateTime? LeaseExpiresAt,
    DateTime? HeartbeatAt,
    string? ConfigRevisionSha256)
{
    public bool HasOwnerTuple => OwnerId is not null;
}

/// <summary>
/// 획득 성공이면 <see cref="Grant"/>가 호출자의 새 lease이고, 실패면 Grant는 null입니다.
/// <see cref="State"/>는 비밀이 제거된 관찰용 GLOBAL 상태입니다.
/// </summary>
public sealed record FdcRuntimeLeaseAcquireResult(
    bool Acquired,
    FdcRuntimeLeaseState State,
    FdcRuntimeLeaseGrant? Grant);

/// <summary>
/// 한 번의 성공한 acquire에만 발급되는 불투명한 lease grant입니다. 공개 표면은 controller fencing에
/// 필요한 authority뿐이며, renew/release 증명에 쓰는 256-bit secret은 구현 내부에만 유지됩니다.
/// </summary>
public abstract class FdcRuntimeLeaseGrant
{
    protected FdcRuntimeLeaseGrant(FdcRuntimeAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        Authority = authority;
    }

    public FdcRuntimeAuthority Authority { get; }
}

/// <summary>
/// 현재 FDC writer가 controller/action adapter에 전달하는 lease authority입니다. Controller는
/// <see cref="FenceToken"/>의 영속 최대값보다 작은 모든 apply/release를 거부해야 합니다.
/// <see cref="ConfigRevision"/>은 canonical 설정 snapshot의 lowercase 64자리 SHA-256 hex digest입니다.
/// </summary>
public sealed record FdcRuntimeAuthority(
    string OwnerId,
    long FenceToken,
    string ConfigRevision,
    DateTime LeaseExpiresAt);
