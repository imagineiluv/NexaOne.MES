using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Sys;

/// <summary>
/// SYS가 소유한 사용자의 활성·미삭제 상태를 제공하는 directory 계약입니다.
/// 소비 모듈은 SYS_USER 물리 스키마나 자격증명 컬럼을 조회하지 않습니다.
/// </summary>
public interface IUserDirectory : INexaModuleBridge
{
    Task<bool> IsActiveAsync(string userId, CancellationToken ct = default);
}
