using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcEquipmentEndpointRepository : QueryRepository, IFdcEquipmentEndpointRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcEquipmentEndpointRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<FdcEquipmentEndpoint?> GetByIdAsync(string endpointId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM FDC_EQUIPMENT_ENDPOINT WHERE ENDPOINT_ID = @endpointId";
        var row = await QueryFirstOrDefaultAsync<EndpointRow>(sql, new { endpointId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<FdcEquipmentEndpoint>> GetActiveByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_EQUIPMENT_ENDPOINT
            WHERE EQUIPMENT_ID = @equipmentId AND IS_ACTIVE = 1
            ORDER BY ENDPOINT_ID";
        var rows = await QueryAsync<EndpointRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<FdcEquipmentEndpoint>> GetAllActiveAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_EQUIPMENT_ENDPOINT
            WHERE IS_ACTIVE = 1
            ORDER BY EQUIPMENT_ID, ENDPOINT_ID";
        var rows = await QueryAsync<EndpointRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(FdcEquipmentEndpoint endpoint, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_EQUIPMENT_ENDPOINT
            (ENDPOINT_ID, EQUIPMENT_ID, PROTOCOL, ENDPOINT_URL, TAG_MAP_PATH,
             MODBUS_UNIT_ID, S7_RACK, S7_SLOT,
             MITSUBISHI_STATION_NO, MITSUBISHI_NETWORK_NO, MITSUBISHI_PC_NO,
             MITSUBISHI_IO_NO, MITSUBISHI_FRAME_FORMAT,
             CONNECTION_TIMEOUT_MS, READ_WRITE_TIMEOUT_MS, HEARTBEAT_TIMEOUT_MS,
             POLLING_DISCONNECT_BACKOFF_MS, POLLING_MAX_DISCONNECT_BACKOFF_MS,
             SAMPLING_INTERVAL_MS, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@EndpointId, @EquipmentId, @Protocol, @EndpointUrl, @TagMapPath,
             @ModbusUnitId, @S7Rack, @S7Slot,
             @MitsubishiStationNo, @MitsubishiNetworkNo, @MitsubishiPcNo,
             @MitsubishiIoNo, @MitsubishiFrameFormat,
             @ConnectionTimeoutMs, @ReadWriteTimeoutMs, @HeartbeatTimeoutMs,
             @PollingDisconnectBackoffMs, @PollingMaxDisconnectBackoffMs,
             @SamplingIntervalMs, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, EndpointRow.FromDomain(endpoint), ct);
    }

    public async Task UpdateAsync(FdcEquipmentEndpoint endpoint, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_EQUIPMENT_ENDPOINT SET
            PROTOCOL = @Protocol, ENDPOINT_URL = @EndpointUrl, TAG_MAP_PATH = @TagMapPath,
            MODBUS_UNIT_ID = @ModbusUnitId, S7_RACK = @S7Rack, S7_SLOT = @S7Slot,
            MITSUBISHI_STATION_NO = @MitsubishiStationNo,
            MITSUBISHI_NETWORK_NO = @MitsubishiNetworkNo,
            MITSUBISHI_PC_NO = @MitsubishiPcNo,
            MITSUBISHI_IO_NO = @MitsubishiIoNo,
            MITSUBISHI_FRAME_FORMAT = @MitsubishiFrameFormat,
            CONNECTION_TIMEOUT_MS = @ConnectionTimeoutMs,
            READ_WRITE_TIMEOUT_MS = @ReadWriteTimeoutMs,
            HEARTBEAT_TIMEOUT_MS = @HeartbeatTimeoutMs,
            POLLING_DISCONNECT_BACKOFF_MS = @PollingDisconnectBackoffMs,
            POLLING_MAX_DISCONNECT_BACKOFF_MS = @PollingMaxDisconnectBackoffMs,
            SAMPLING_INTERVAL_MS = @SamplingIntervalMs, IS_ACTIVE = @IsActive,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ENDPOINT_ID = @EndpointId";
        await _processor.UpdateAsync(sql, EndpointRow.FromDomain(endpoint), ct);
    }

    private sealed class EndpointRow
    {
        public string EndpointId         { get; set; } = "";
        public string EquipmentId        { get; set; } = "";
        public string Protocol           { get; set; } = "";
        public string EndpointUrl        { get; set; } = "";
        public string? TagMapPath        { get; set; }
        public int? ModbusUnitId          { get; set; }
        public int? S7Rack                { get; set; }
        public int? S7Slot                { get; set; }
        public int? MitsubishiStationNo   { get; set; }
        public int? MitsubishiNetworkNo   { get; set; }
        public int? MitsubishiPcNo        { get; set; }
        public int? MitsubishiIoNo        { get; set; }
        public string? MitsubishiFrameFormat { get; set; }
        public int ConnectionTimeoutMs    { get; set; }
        public int ReadWriteTimeoutMs     { get; set; }
        public int HeartbeatTimeoutMs     { get; set; }
        public int PollingDisconnectBackoffMs    { get; set; }
        public int PollingMaxDisconnectBackoffMs { get; set; }
        public int    SamplingIntervalMs { get; set; }
        public bool   IsActive           { get; set; }
        // 읽기경로 감사 메타데이터 복원용(MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑, SELECT *).
        public string    CreatedBy { get; set; } = "";
        public DateTime  CreatedAt { get; set; }
        public string?   UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // Restore로 복원 — Create 재구성과 달리 검증 탈락으로 인한 행 드롭이 없고 감사 메타데이터를 보존한다(읽기경로 무손실).
        public FdcEquipmentEndpoint ToDomain() =>
            FdcEquipmentEndpoint.Restore(
                EndpointId, EquipmentId, Protocol, EndpointUrl, SamplingIntervalMs, IsActive,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, TagMapPath,
                new FdcPlcEndpointSettings(
                    ModbusUnitId,
                    S7Rack,
                    S7Slot,
                    MitsubishiStationNo,
                    MitsubishiNetworkNo,
                    MitsubishiPcNo,
                    MitsubishiIoNo,
                    MitsubishiFrameFormat,
                    ConnectionTimeoutMs,
                    ReadWriteTimeoutMs,
                    HeartbeatTimeoutMs,
                    PollingDisconnectBackoffMs,
                    PollingMaxDisconnectBackoffMs));

        public static EndpointRow FromDomain(FdcEquipmentEndpoint e) => new()
        {
            EndpointId         = e.Id,
            EquipmentId        = e.EquipmentId,
            Protocol           = e.Protocol,
            EndpointUrl        = e.EndpointUrl,
            TagMapPath         = e.TagMapPath,
            ModbusUnitId       = e.PlcSettings.ModbusUnitId,
            S7Rack             = e.PlcSettings.S7Rack,
            S7Slot             = e.PlcSettings.S7Slot,
            MitsubishiStationNo = e.PlcSettings.MitsubishiStationNo,
            MitsubishiNetworkNo = e.PlcSettings.MitsubishiNetworkNo,
            MitsubishiPcNo       = e.PlcSettings.MitsubishiPcNo,
            MitsubishiIoNo       = e.PlcSettings.MitsubishiIoNo,
            MitsubishiFrameFormat = e.PlcSettings.MitsubishiFrameFormat,
            ConnectionTimeoutMs = e.PlcSettings.ConnectionTimeoutMs,
            ReadWriteTimeoutMs = e.PlcSettings.ReadWriteTimeoutMs,
            HeartbeatTimeoutMs = e.PlcSettings.HeartbeatTimeoutMs,
            PollingDisconnectBackoffMs = e.PlcSettings.PollingDisconnectBackoffMs,
            PollingMaxDisconnectBackoffMs = e.PlcSettings.PollingMaxDisconnectBackoffMs,
            SamplingIntervalMs = e.SamplingIntervalMs,
            IsActive           = e.IsActive
        };
    }
}
