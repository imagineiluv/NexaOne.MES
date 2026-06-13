using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>설비↔설비-프로토콜 엔드포인트 매핑 (FDC_EQUIPMENT_ENDPOINT, design 10.4.2).
/// NexusLogic 설비 프로토콜(OPC-UA 등) 연결 정보를 FDC bounded context가 소유한다.</summary>
public sealed class FdcEquipmentEndpoint : AuditableEntity<string>
{
    /// <summary>NexusLogic PlcDriverKind와 매핑되는 허용 프로토콜.</summary>
    private static readonly HashSet<string> AllowedProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "OpcUa", "ModbusTcp", "ModbusRtu", "SiemensS7", "MitsubishiMc", "EtherNetIp", "OmronFins"
    };

    private FdcEquipmentEndpoint(string endpointId) : base(endpointId) { }

    public string EquipmentId { get; private set; } = string.Empty;
    public string Protocol { get; private set; } = string.Empty;
    public string EndpointUrl { get; private set; } = string.Empty;
    public int SamplingIntervalMs { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<FdcEquipmentEndpoint> Create(
        string endpointId,
        string equipmentId,
        string protocol,
        string endpointUrl,
        int samplingIntervalMs = 1000)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(endpointId), "Endpoint ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (!AllowedProtocols.Contains(protocol))
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(protocol),
                $"Protocol must be one of: {string.Join(", ", AllowedProtocols)}."));
        if (string.IsNullOrWhiteSpace(endpointUrl))
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(endpointUrl), "Endpoint URL is required."));
        if (samplingIntervalMs <= 0)
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(samplingIntervalMs), "Sampling interval must be positive."));

        var endpoint = new FdcEquipmentEndpoint(endpointId)
        {
            EquipmentId = equipmentId,
            Protocol = protocol,
            EndpointUrl = endpointUrl,
            SamplingIntervalMs = samplingIntervalMs,
            IsActive = true
        };
        return endpoint;
    }

    public void UpdateUrl(string endpointUrl)
    {
        if (!string.IsNullOrWhiteSpace(endpointUrl))
            EndpointUrl = endpointUrl;
    }

    public void SetSamplingInterval(int samplingIntervalMs)
    {
        if (samplingIntervalMs > 0)
            SamplingIntervalMs = samplingIntervalMs;
    }

    public void Deactivate() => IsActive = false;
}
