using System.Globalization;
using NexaOne.FDC.Domain;
using NexaLogic.Plc.Abstractions.Models;

namespace NexaOne.FDC.Infrastructure.Equipment;

/// <summary>FDC 설비-엔드포인트 매핑(FdcEquipmentEndpoint)을 NexaLogic 수집 계약
/// (<see cref="PlcEndpoint"/>·<see cref="PlcSubscription"/>)으로 변환한다. 순수 변환 — 부수효과 없음.</summary>
public static class FdcEndpointMapper
{
    private static readonly IReadOnlyDictionary<string, PlcDriverKind> ProtocolKinds =
        new Dictionary<string, PlcDriverKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["ModbusTcp"] = PlcDriverKind.ModbusTcp,
            ["SiemensS7"] = PlcDriverKind.SiemensS7,
            ["MitsubishiMc"] = PlcDriverKind.MitsubishiMc,
            ["EtherNetIp"] = PlcDriverKind.EtherNetIp,
        };

    /// <summary>설비 엔드포인트 → NexaLogic PlcEndpoint. Protocol 문자열은 PlcDriverKind로 파싱한다.</summary>
    public static PlcEndpoint ToPlcEndpoint(FdcEquipmentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!ProtocolKinds.TryGetValue(endpoint.Protocol, out var kind))
            throw new InvalidOperationException(
                $"FDC endpoint '{endpoint.Id}' has unsupported PLC protocol '{endpoint.Protocol}'. "
                + $"Supported protocols: {string.Join(", ", ProtocolKinds.Keys)}.");

        var settingsError = endpoint.PlcSettings.GetValidationError(endpoint.Protocol);
        if (settingsError is not null)
            throw new InvalidOperationException(
                $"FDC endpoint '{endpoint.Id}' has invalid PLC settings: {settingsError}");

        var (host, port) = ParseHostAndPort(endpoint);
        var tagMapPath = ResolveTagMapPath(endpoint);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tagMapPath"] = tagMapPath,
            ["polling.disconnectBackoffMs"] = endpoint.PlcSettings.PollingDisconnectBackoffMs
                .ToString(CultureInfo.InvariantCulture),
            ["polling.maxDisconnectBackoffMs"] = endpoint.PlcSettings.PollingMaxDisconnectBackoffMs
                .ToString(CultureInfo.InvariantCulture),
        };
        AddMitsubishiOptions(endpoint.PlcSettings, options);

        return new PlcEndpoint(
            endpoint.Id,
            kind,
            host,
            port,
            Station: endpoint.PlcSettings.MitsubishiStationNo?.ToString(CultureInfo.InvariantCulture),
            Rack: endpoint.PlcSettings.S7Rack?.ToString(CultureInfo.InvariantCulture),
            Slot: endpoint.PlcSettings.S7Slot?.ToString(CultureInfo.InvariantCulture),
            UnitId: endpoint.PlcSettings.ModbusUnitId?.ToString(CultureInfo.InvariantCulture),
            Timeouts: new PlcTimeoutSettings(
                TimeSpan.FromMilliseconds(endpoint.PlcSettings.ConnectionTimeoutMs),
                TimeSpan.FromMilliseconds(endpoint.PlcSettings.ReadWriteTimeoutMs),
                TimeSpan.FromMilliseconds(endpoint.PlcSettings.HeartbeatTimeoutMs)),
            Options: options);
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
        if (FdcEquipmentEndpoint.TryParseConnectionEndpoint(
                endpoint.EndpointUrl, out var host, out var port, out var validationError))
            return (host, port);

        throw new InvalidOperationException(
            $"FDC endpoint '{endpoint.Id}' has invalid endpoint URL configuration: {validationError}");
    }

    private static void AddMitsubishiOptions(
        FdcPlcEndpointSettings settings,
        IDictionary<string, string> options)
    {
        if (settings.MitsubishiNetworkNo is not null)
            options["networkNo"] = settings.MitsubishiNetworkNo.Value.ToString(CultureInfo.InvariantCulture);
        if (settings.MitsubishiPcNo is not null)
            options["pcNo"] = settings.MitsubishiPcNo.Value.ToString(CultureInfo.InvariantCulture);
        if (settings.MitsubishiIoNo is not null)
        {
            // NexaLogic Mitsubishi parser treats ioNo as hexadecimal first. Emit canonical four-digit
            // hexadecimal so a persisted numeric 1023 is transported as 0x03FF, not misread as 0x1023.
            options["ioNo"] = settings.MitsubishiIoNo.Value.ToString("X4", CultureInfo.InvariantCulture);
        }
        if (settings.MitsubishiFrameFormat is not null)
            options["frameFormat"] = settings.MitsubishiFrameFormat.ToLowerInvariant();
    }

    private static string ResolveTagMapPath(FdcEquipmentEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.TagMapPath))
            throw new InvalidOperationException(
                $"FDC endpoint '{endpoint.Id}' requires TAG_MAP_PATH before collection can be enabled.");

        var configured = endpoint.TagMapPath.Trim();
        var resolved = Path.IsPathFullyQualified(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        if (!File.Exists(resolved))
            throw new FileNotFoundException(
                $"FDC endpoint '{endpoint.Id}' tag map '{resolved}' was not found.", resolved);

        return resolved;
    }

}
