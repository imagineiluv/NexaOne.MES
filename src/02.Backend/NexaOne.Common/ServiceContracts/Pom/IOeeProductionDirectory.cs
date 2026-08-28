using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// OEE 집계가 사용하는 LOT TrackOut 생산 증거의 POM 소유 조회 계약입니다.
/// </summary>
public interface IOeeProductionDirectory : INexaModuleBridge
{
    Task<OeeProductionWindowDto> LoadProductionAsync(
        string plantId,
        string equipmentId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default);
}
