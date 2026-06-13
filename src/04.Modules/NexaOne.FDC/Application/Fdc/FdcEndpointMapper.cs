using NexaOne.FDC.Domain;
using NexusLogic.Plc.Abstractions.Models;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>FDC 설비-엔드포인트 매핑(FdcEquipmentEndpoint)을 NexusLogic 수집 계약
/// (<see cref="PlcEndpoint"/>·<see cref="PlcSubscription"/>)으로 변환한다. 순수 변환 — 부수효과 없음.</summary>
public static class FdcEndpointMapper
{
    /// <summary>설비 엔드포인트 → NexusLogic PlcEndpoint. Protocol 문자열은 PlcDriverKind로 파싱한다.</summary>
    public static PlcEndpoint ToPlcEndpoint(FdcEquipmentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Enum.TryParse<PlcDriverKind>(endpoint.Protocol, ignoreCase: true, out var kind))
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Id}' has unsupported protocol '{endpoint.Protocol}'.");

        return new PlcEndpoint(endpoint.Id, kind, endpoint.EndpointUrl);
    }

    /// <summary>설비의 활성 파라미터들 → 단일 구독(PlcSubscription). 태그명은 파라미터 ID,
    /// 폴링 주기는 엔드포인트의 SamplingIntervalMs를 사용한다.</summary>
    public static PlcSubscription ToSubscription(
        FdcEquipmentEndpoint endpoint,
        IEnumerable<FdcParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(parameters);

        var tags = parameters.Where(p => p.IsActive).Select(p => p.Id).ToList();
        return new PlcSubscription(
            SubscriptionId: $"{endpoint.Id}::sub",
            EndpointId: endpoint.Id,
            TagNames: tags,
            PollingInterval: TimeSpan.FromMilliseconds(endpoint.SamplingIntervalMs));
    }
}
