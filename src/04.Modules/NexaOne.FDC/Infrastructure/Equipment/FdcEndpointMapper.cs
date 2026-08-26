using NexaOne.FDC.Domain;
using NexusLogic.Plc.Abstractions.Models;

namespace NexaOne.FDC.Infrastructure.Equipment;

/// <summary>FDC 설비-엔드포인트 매핑(FdcEquipmentEndpoint)을 NexusLogic 수집 계약
/// (<see cref="PlcEndpoint"/>·<see cref="PlcSubscription"/>)으로 변환한다. 순수 변환 — 부수효과 없음.</summary>
public static class FdcEndpointMapper
{
    private static readonly IReadOnlyDictionary<string, PlcDriverKind> ProtocolKinds =
        new Dictionary<string, PlcDriverKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpcUa"] = PlcDriverKind.OpcUa,
            ["ModbusTcp"] = PlcDriverKind.ModbusTcp,
            ["ModbusRtu"] = PlcDriverKind.ModbusRtu,
            ["SiemensS7"] = PlcDriverKind.SiemensS7,
            ["MitsubishiMc"] = PlcDriverKind.MitsubishiMc,
            ["EtherNetIp"] = PlcDriverKind.EtherNetIp,
            ["OmronFins"] = PlcDriverKind.OmronFins,
        };

    /// <summary>설비 엔드포인트 → NexusLogic PlcEndpoint. Protocol 문자열은 PlcDriverKind로 파싱한다.</summary>
    public static PlcEndpoint ToPlcEndpoint(FdcEquipmentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!ProtocolKinds.TryGetValue(endpoint.Protocol, out var kind))
            throw new InvalidOperationException(
                $"FDC endpoint '{endpoint.Id}' has unsupported PLC protocol '{endpoint.Protocol}'. "
                + $"Supported protocols: {string.Join(", ", ProtocolKinds.Keys)}.");

        var (host, port) = ParseHostAndPort(endpoint);
        IReadOnlyDictionary<string, string>? options = kind == PlcDriverKind.OpcUa
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // OPC UA는 discovery path까지 포함한 원문 URL을 보존해야 한다.
                ["endpointUrl"] = endpoint.EndpointUrl,
            }
            : null;

        return new PlcEndpoint(endpoint.Id, kind, host, port, Options: options);
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

    private static (string Host, int? Port) ParseHostAndPort(FdcEquipmentEndpoint endpoint)
    {
        var value = endpoint.EndpointUrl.Trim();
        if (TryParseEndpointUri(value, out var uri)
            || TryParseEndpointUri($"tcp://{value}", out uri))
        {
            return (uri.Host, uri.IsDefaultPort ? null : uri.Port);
        }

        throw new InvalidOperationException(
            $"FDC endpoint '{endpoint.Id}' has invalid endpoint URL '{endpoint.EndpointUrl}'.");
    }

    private static bool TryParseEndpointUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && !string.IsNullOrWhiteSpace(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
