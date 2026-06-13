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
        return rows.Select(r => r.ToDomain()).OfType<FdcEquipmentEndpoint>().ToList();
    }

    public async Task<IReadOnlyList<FdcEquipmentEndpoint>> GetAllActiveAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_EQUIPMENT_ENDPOINT
            WHERE IS_ACTIVE = 1
            ORDER BY EQUIPMENT_ID, ENDPOINT_ID";
        var rows = await QueryAsync<EndpointRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcEquipmentEndpoint>().ToList();
    }

    public async Task AddAsync(FdcEquipmentEndpoint endpoint, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_EQUIPMENT_ENDPOINT
            (ENDPOINT_ID, EQUIPMENT_ID, PROTOCOL, ENDPOINT_URL, SAMPLING_INTERVAL_MS, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@EndpointId, @EquipmentId, @Protocol, @EndpointUrl, @SamplingIntervalMs, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, EndpointRow.FromDomain(endpoint), ct);
    }

    public async Task UpdateAsync(FdcEquipmentEndpoint endpoint, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_EQUIPMENT_ENDPOINT SET
            PROTOCOL = @Protocol, ENDPOINT_URL = @EndpointUrl,
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
        public int    SamplingIntervalMs { get; set; }
        public bool   IsActive           { get; set; }

        public FdcEquipmentEndpoint? ToDomain()
        {
            var result = FdcEquipmentEndpoint.Create(EndpointId, EquipmentId, Protocol, EndpointUrl, SamplingIntervalMs);
            if (result.IsFailure) return null;
            var e = result.Value;
            if (!IsActive) e.Deactivate();
            return e;
        }

        public static EndpointRow FromDomain(FdcEquipmentEndpoint e) => new()
        {
            EndpointId         = e.Id,
            EquipmentId        = e.EquipmentId,
            Protocol           = e.Protocol,
            EndpointUrl        = e.EndpointUrl,
            SamplingIntervalMs = e.SamplingIntervalMs,
            IsActive           = e.IsActive
        };
    }
}
