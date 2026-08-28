using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Ems;

/// <summary>
/// 인증 사용자와 보전 작업자 마스터의 현재 유효한 연결을 해석하는 directory 계약입니다.
/// SYS adapter가 사용자/작업자 매핑을 소유하고 EMS에는 축소된 identity만 반환합니다.
/// </summary>
public interface IMaintenanceIdentityDirectory : INexaModuleBridge
{
    /// <summary>
    /// 지정 시각에 활성 상태인 로그인 사용자를 반환합니다. 활성 작업자 매핑이 없으면
    /// <see cref="MaintenanceIdentityEntry.WorkerId"/>는 <see langword="null"/>입니다.
    /// 비활성·삭제·미등록 사용자는 <see langword="null"/>을 반환합니다.
    /// </summary>
    Task<MaintenanceIdentityEntry?> GetActiveIdentityAsync(
        string userId,
        DateTime at,
        CancellationToken ct = default);
}

public sealed record MaintenanceIdentityEntry(string UserId, string? WorkerId);
