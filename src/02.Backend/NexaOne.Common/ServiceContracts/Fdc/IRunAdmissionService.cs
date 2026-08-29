using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Fdc;

/// <summary>
/// 설비의 자동운전 시작 권한을 FDC의 현재 안전 snapshot에 묶어 발급하는 짧은 원격 계약입니다.
/// 물리 인터락 action이나 PLC 제어를 대신하지 않으며, 발급된 lease는 keep-alive 실패, FDC authority 교체,
/// 인터락 발생, 서버 재시작 또는 hard expiry 중 하나만 발생해도 더 이상 current가 아닙니다.
/// </summary>
public interface IRunAdmissionService : INexaModuleBridge
{
    Task<RunAdmissionDecisionDto> AcquireAsync(
        RunAdmissionAcquireDto request,
        CancellationToken ct = default);

    Task<RunAdmissionStatusDto> KeepAliveAsync(
        RunAdmissionLeaseProofDto request,
        CancellationToken ct = default);

    Task<RunAdmissionReleaseDto> ReleaseAsync(
        RunAdmissionLeaseProofDto request,
        CancellationToken ct = default);
}

public sealed record RunAdmissionAcquireDto(
    string EquipmentId,
    string ClientId,
    string RequestId);

/// <summary>
/// AccessToken은 lease 소유 증명용 opaque secret이다. TLS 밖으로 보내거나 로그·감사 payload에 기록하면 안 된다.
/// </summary>
public sealed record RunAdmissionLeaseDto(
    string EquipmentId,
    string ClientId,
    string LeaseId,
    string AuthorityGeneration,
    long Fence,
    DateTimeOffset ObservedAt,
    DateTimeOffset HardExpiresAt,
    DateTimeOffset KeepAliveExpiresAt,
    long HardLeaseTtlMilliseconds,
    long KeepAliveTtlMilliseconds,
    string AccessToken)
{
    // record의 기본 ToString은 opaque capability를 평문으로 출력하므로 명시적으로 마스킹한다.
    public override string ToString() =>
        $"{nameof(RunAdmissionLeaseDto)} {{ EquipmentId = {EquipmentId}, ClientId = {ClientId}, "
        + $"LeaseId = {LeaseId}, AuthorityGeneration = {AuthorityGeneration}, Fence = {Fence}, "
        + $"ObservedAt = {ObservedAt:O}, HardExpiresAt = {HardExpiresAt:O}, "
        + $"KeepAliveExpiresAt = {KeepAliveExpiresAt:O}, HardLeaseTtlMilliseconds = {HardLeaseTtlMilliseconds}, "
        + $"KeepAliveTtlMilliseconds = {KeepAliveTtlMilliseconds}, AccessToken = [REDACTED] }}";
}

public sealed record RunAdmissionLeaseProofDto(
    string EquipmentId,
    string ClientId,
    string LeaseId,
    string AuthorityGeneration,
    long Fence,
    string AccessToken)
{
    public override string ToString() =>
        $"{nameof(RunAdmissionLeaseProofDto)} {{ EquipmentId = {EquipmentId}, ClientId = {ClientId}, "
        + $"LeaseId = {LeaseId}, AuthorityGeneration = {AuthorityGeneration}, Fence = {Fence}, "
        + "AccessToken = [REDACTED] }";
}

public sealed record RunAdmissionDecisionDto(
    bool IsAdmitted,
    string Code,
    string Message,
    RunAdmissionLeaseDto? Lease);

public sealed record RunAdmissionStatusDto(
    bool IsCurrent,
    string Code,
    string Message,
    DateTimeOffset ObservedAt,
    DateTimeOffset? KeepAliveExpiresAt,
    long? KeepAliveTtlMilliseconds,
    bool IsAbsent);

public sealed record RunAdmissionReleaseDto(
    bool Released,
    string Code,
    string Message);
