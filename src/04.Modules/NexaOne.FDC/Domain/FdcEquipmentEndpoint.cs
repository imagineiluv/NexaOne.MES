using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>설비↔설비-프로토콜 엔드포인트 매핑 (FDC_EQUIPMENT_ENDPOINT, design 10.4.2).
/// NexaLogic 설비 프로토콜(OPC-UA 등) 연결 정보를 FDC bounded context가 소유한다.</summary>
public sealed class FdcEquipmentEndpoint : AuditableEntity<string>
{
    /// <summary>NexaLogic PlcDriverKind와 매핑되는 허용 프로토콜.</summary>
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

    /// <summary>영속된 행을 검증 없이 도메인으로 복원한다(읽기경로 Restore 패턴 — FdcAlarmConfig와 동일).
    /// Create 재구성과 달리 (1) AllowedProtocols 검증 실패로 인한 행 드롭이 없고(영속됐다 프로토콜 표기가 드리프트한
    /// 엔드포인트가 조회에서 사라져 수집이 무음 중단되는 것을 방지), (2) 감사 메타데이터(CreatedBy/CreatedAt/
    /// UpdatedBy/UpdatedAt)를 보존해 매 읽기마다 CreatedAt이 UtcNow로 재생성되거나 CreatedBy=""로 리셋되는 상태손실을 막는다.</summary>
    public static FdcEquipmentEndpoint Restore(
        string endpointId,
        string equipmentId,
        string protocol,
        string endpointUrl,
        int samplingIntervalMs,
        bool isActive,
        string? createdBy = null,
        DateTime? createdAt = null,
        string? updatedBy = null,
        DateTime? updatedAt = null)
    {
        var endpoint = new FdcEquipmentEndpoint(endpointId)
        {
            EquipmentId = equipmentId,
            Protocol = protocol,
            EndpointUrl = endpointUrl,
            SamplingIntervalMs = samplingIntervalMs,
            IsActive = isActive
        };
        endpoint.RestoreAudit(createdBy ?? endpoint.CreatedBy, createdAt ?? endpoint.CreatedAt, updatedBy, updatedAt);
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
