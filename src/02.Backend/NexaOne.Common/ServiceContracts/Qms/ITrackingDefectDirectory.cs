using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Qms;

/// <summary>
/// TrackOut 불량 코드가 활성 상태인지 판정하는 QMS 소유 조회 계약입니다.
/// </summary>
public interface ITrackingDefectDirectory : INexaModuleBridge
{
    Task<bool> IsValidAsync(string defectCode, CancellationToken ct = default);
}
