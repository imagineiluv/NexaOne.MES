using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>설비↔설비-프로토콜 엔드포인트 매핑 (FDC_EQUIPMENT_ENDPOINT, design 10.4.2).
/// NexaLogic polling 설비 프로토콜 연결 정보를 FDC bounded context가 소유한다.</summary>
public sealed class FdcEquipmentEndpoint : AuditableEntity<string>
{
    /// <summary>NexaLogic PlcDriverKind와 매핑되는 허용 프로토콜.</summary>
    private static readonly HashSet<string> AllowedProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModbusTcp", "SiemensS7", "MitsubishiMc", "EtherNetIp"
    };

    private FdcEquipmentEndpoint(string endpointId) : base(endpointId) { }

    public string EquipmentId { get; private set; } = string.Empty;
    public string Protocol { get; private set; } = string.Empty;
    public string EndpointUrl { get; private set; } = string.Empty;
    public string? TagMapPath { get; private set; }
    public FdcPlcEndpointSettings PlcSettings { get; private set; } = new();
    public int SamplingIntervalMs { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<FdcEquipmentEndpoint> Create(
        string endpointId,
        string equipmentId,
        string protocol,
        string endpointUrl,
        int samplingIntervalMs = 1000,
        string? tagMapPath = null,
        FdcPlcEndpointSettings? plcSettings = null)
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
        if (!TryParseConnectionEndpoint(endpointUrl, out _, out _, out var endpointValidationError))
            return Result.Failure<FdcEquipmentEndpoint>(
                Error.Validation(nameof(endpointUrl), endpointValidationError));
        if (samplingIntervalMs <= 0)
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(samplingIntervalMs), "Sampling interval must be positive."));
        if (tagMapPath is not null && string.IsNullOrWhiteSpace(tagMapPath))
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(tagMapPath), "Tag map path cannot be blank."));

        var settings = plcSettings ?? new FdcPlcEndpointSettings();
        var settingsError = settings.GetValidationError(protocol);
        if (settingsError is not null)
            return Result.Failure<FdcEquipmentEndpoint>(Error.Validation(nameof(plcSettings), settingsError));

        var endpoint = new FdcEquipmentEndpoint(endpointId)
        {
            EquipmentId = equipmentId,
            Protocol = protocol,
            EndpointUrl = endpointUrl,
            TagMapPath = tagMapPath,
            PlcSettings = settings,
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
        DateTime? updatedAt = null,
        string? tagMapPath = null,
        FdcPlcEndpointSettings? plcSettings = null)
    {
        var endpoint = new FdcEquipmentEndpoint(endpointId)
        {
            EquipmentId = equipmentId,
            Protocol = protocol,
            EndpointUrl = endpointUrl,
            TagMapPath = tagMapPath,
            PlcSettings = plcSettings ?? new FdcPlcEndpointSettings(),
            SamplingIntervalMs = samplingIntervalMs,
            IsActive = isActive
        };
        endpoint.RestoreAudit(createdBy ?? endpoint.CreatedBy, createdAt ?? endpoint.CreatedAt, updatedBy, updatedAt);
        return endpoint;
    }

    public void UpdateUrl(string endpointUrl)
    {
        if (TryParseConnectionEndpoint(endpointUrl, out _, out _, out _))
            EndpointUrl = endpointUrl;
    }

    public void SetSamplingInterval(int samplingIntervalMs)
    {
        if (samplingIntervalMs > 0)
            SamplingIntervalMs = samplingIntervalMs;
    }

    public Result SetTagMapPath(string tagMapPath)
    {
        if (string.IsNullOrWhiteSpace(tagMapPath))
            return Result.Failure(Error.Validation(nameof(tagMapPath), "Tag map path is required."));

        TagMapPath = tagMapPath;
        return Result.Success();
    }

    /// <summary>드라이버별 구조화 연결 값과 공통 timeout/recovery 정책을 교체한다.
    /// 임의 options 사전은 받지 않아 비밀값 또는 드라이버가 소비하지 않는 키가 DB에 저장되지 않는다.</summary>
    public Result ConfigurePlc(FdcPlcEndpointSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validationError = settings.GetValidationError(Protocol);
        if (validationError is not null)
            return Result.Failure(Error.Validation(nameof(settings), validationError));

        PlcSettings = settings;
        return Result.Success();
    }

    public void Deactivate() => IsActive = false;

    /// <summary>FDC polling endpoint는 scheme-less host[:port] 또는 tcp://host[:port]만 허용한다.
    /// 파싱된 port는 URI scheme의 default 여부와 무관하게 보존한다.</summary>
    internal static bool TryParseConnectionEndpoint(
        string endpointUrl,
        out string host,
        out int? port,
        out string validationError)
    {
        host = string.Empty;
        port = null;
        validationError = string.Empty;
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            validationError = "Endpoint URL is required.";
            return false;
        }

        var value = endpointUrl.Trim();
        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        var hasScheme = schemeSeparator >= 0;
        var authorityStart = hasScheme ? schemeSeparator + 3 : 0;
        var hasExplicitPath = value.IndexOf('/', authorityStart) >= 0
                              || value.IndexOf('\\', authorityStart) >= 0;
        if (value.IndexOfAny(['@', '?', '#']) >= 0 || hasExplicitPath)
        {
            validationError = "Endpoint URL must contain only the PLC host and port; "
                              + "inline credentials, query strings, fragments, and paths are not allowed.";
            return false;
        }

        var candidate = hasScheme ? value : $"tcp://{value}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            validationError = "Endpoint URL must be a valid PLC host and optional port.";
            return false;
        }

        if (!uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            validationError = "Endpoint URL must use tcp:// or a scheme-less PLC host and optional port.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || hasExplicitPath)
        {
            validationError = "Endpoint URL must contain only the PLC host and port; "
                              + "inline credentials, query strings, fragments, and paths are not allowed.";
            return false;
        }

        host = uri.Host;
        // tcp is deliberately the only accepted scheme. Use the parsed port itself instead of
        // Uri.IsDefaultPort so an explicitly configured protocol port can never collapse to null.
        port = uri.Port < 0 ? null : uri.Port;
        return true;
    }
}

/// <summary>FDC가 영속하는 비밀 없는 PLC endpoint 설정 allowlist.
/// 프로토콜별 주소 필드와 NexaLogic 공통 timeout/polling recovery 값만 표현한다.</summary>
public sealed record FdcPlcEndpointSettings(
    int? ModbusUnitId = null,
    int? S7Rack = null,
    int? S7Slot = null,
    int? MitsubishiStationNo = null,
    int? MitsubishiNetworkNo = null,
    int? MitsubishiPcNo = null,
    int? MitsubishiIoNo = null,
    string? MitsubishiFrameFormat = null,
    int ConnectionTimeoutMs = 5_000,
    int ReadWriteTimeoutMs = 5_000,
    int HeartbeatTimeoutMs = 5_000,
    int PollingDisconnectBackoffMs = 100,
    int PollingMaxDisconnectBackoffMs = 1_000)
{
    internal string? GetValidationError(string protocol)
    {
        if (ConnectionTimeoutMs <= 0)
            return "Connection timeout must be positive.";
        if (ReadWriteTimeoutMs <= 0)
            return "Read/write timeout must be positive.";
        if (HeartbeatTimeoutMs <= 0)
            return "Heartbeat timeout must be positive.";
        if (PollingDisconnectBackoffMs <= 0)
            return "Polling disconnect backoff must be positive.";
        if (PollingMaxDisconnectBackoffMs < PollingDisconnectBackoffMs)
            return "Polling max disconnect backoff must be greater than or equal to disconnect backoff.";

        if (ModbusUnitId is < 0 or > byte.MaxValue)
            return "Modbus unit ID must be between 0 and 255.";
        if (S7Rack is < 0 or > 7)
            return "Siemens S7 rack must be between 0 and 7.";
        if (S7Slot is < 0 or > 31)
            return "Siemens S7 slot must be between 0 and 31.";
        if (MitsubishiStationNo is < 0 or > byte.MaxValue)
            return "Mitsubishi station number must be between 0 and 255.";
        if (MitsubishiNetworkNo is < 0 or > byte.MaxValue)
            return "Mitsubishi network number must be between 0 and 255.";
        if (MitsubishiPcNo is < 0 or > byte.MaxValue)
            return "Mitsubishi PC number must be between 0 and 255.";
        if (MitsubishiIoNo is < 0 or > ushort.MaxValue)
            return "Mitsubishi I/O number must be between 0 and 65535.";
        if (MitsubishiFrameFormat is not null
            && !MitsubishiFrameFormat.Equals("Binary", StringComparison.OrdinalIgnoreCase)
            && !MitsubishiFrameFormat.Equals("Ascii", StringComparison.OrdinalIgnoreCase))
            return "Mitsubishi frame format must be Binary or Ascii.";

        if (ModbusUnitId is not null && !protocol.Equals("ModbusTcp", StringComparison.OrdinalIgnoreCase))
            return "Modbus unit ID can only be configured for ModbusTcp.";
        if ((S7Rack is not null || S7Slot is not null)
            && !protocol.Equals("SiemensS7", StringComparison.OrdinalIgnoreCase))
            return "S7 rack/slot can only be configured for SiemensS7.";
        if (HasMitsubishiSettings()
            && !protocol.Equals("MitsubishiMc", StringComparison.OrdinalIgnoreCase))
            return "Mitsubishi station/routing/frame settings can only be configured for MitsubishiMc.";

        return null;
    }

    private bool HasMitsubishiSettings() =>
        MitsubishiStationNo is not null
        || MitsubishiNetworkNo is not null
        || MitsubishiPcNo is not null
        || MitsubishiIoNo is not null
        || MitsubishiFrameFormat is not null;
}
