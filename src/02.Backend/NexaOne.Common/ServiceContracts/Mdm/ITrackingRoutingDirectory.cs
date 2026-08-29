using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// POM LOT 실행 검증에 필요한 제품 라우팅의 축소 조회 계약입니다.
/// MDM이 구현하며 소비자는 MDM_ROUTING 물리 스키마를 알지 않습니다.
/// </summary>
public interface ITrackingRoutingDirectory : INexaModuleBridge
{
    /// <summary>
    /// 라우팅이 없으면 null, 공정 매핑이 누락된 스텝은 빈 ProcessId로 반환합니다.
    /// </summary>
    Task<TrackingProductRouting?> GetProductRoutingAsync(
        string routingId,
        CancellationToken ct = default);
}
