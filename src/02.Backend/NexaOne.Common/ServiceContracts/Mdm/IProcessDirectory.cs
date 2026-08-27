using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// MDM 공정 마스터의 존재 여부를 제공하는 directory 계약입니다.
/// 소비 모듈은 MDM_PROCESS 물리 스키마 대신 이 계약을 사용합니다.
/// </summary>
[NexaModuleBridge("Mdm", "processDirectory")]
public interface IProcessDirectory : INexaModuleBridge
{
    Task<bool> ProcessExistsAsync(string processId, CancellationToken ct = default);
}
