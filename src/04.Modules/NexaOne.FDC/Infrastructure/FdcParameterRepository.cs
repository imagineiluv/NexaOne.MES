using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcParameterRepository : QueryRepository, IFdcParameterRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcParameterRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<FdcParameter?> GetByIdAsync(string parameterId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM FDC_PARAMETER WHERE PARAMETER_ID = @parameterId";
        var row = await QueryFirstOrDefaultAsync<ParamRow>(sql, new { parameterId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<FdcParameter>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_PARAMETER
            WHERE EQUIPMENT_ID = @equipmentId
            ORDER BY PARAMETER_NAME";
        var rows = await QueryAsync<ParamRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcParameter>().ToList();
    }

    public async Task AddAsync(FdcParameter parameter, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_PARAMETER
            (PARAMETER_ID, PARAMETER_NAME, EQUIPMENT_ID, GROUP_ID, UNIT,
             LOWER_LIMIT, UPPER_LIMIT, LOWER_CONTROL_LIMIT, UPPER_CONTROL_LIMIT,
             SAMPLING_INTERVAL_MS, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@ParameterId, @ParameterName, @EquipmentId, @GroupId, @Unit,
             @LowerLimit, @UpperLimit, @LowerControlLimit, @UpperControlLimit,
             @SamplingIntervalMs, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, ParamRow.FromDomain(parameter), ct);
    }

    public async Task UpdateAsync(FdcParameter parameter, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_PARAMETER SET
            GROUP_ID = @GroupId,
            LOWER_LIMIT = @LowerLimit, UPPER_LIMIT = @UpperLimit,
            LOWER_CONTROL_LIMIT = @LowerControlLimit, UPPER_CONTROL_LIMIT = @UpperControlLimit,
            IS_ACTIVE = @IsActive,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PARAMETER_ID = @ParameterId";
        await _processor.UpdateAsync(sql, ParamRow.FromDomain(parameter), ct);
    }

    private sealed class ParamRow
    {
        public string  ParameterId        { get; set; } = "";
        public string  ParameterName      { get; set; } = "";
        public string  EquipmentId        { get; set; } = "";
        public string? GroupId            { get; set; }
        public string  Unit               { get; set; } = "";
        public decimal LowerLimit         { get; set; }
        public decimal UpperLimit         { get; set; }
        public decimal? LowerControlLimit { get; set; }
        public decimal? UpperControlLimit { get; set; }
        public int     SamplingIntervalMs { get; set; }
        public bool    IsActive           { get; set; }

        public FdcParameter? ToDomain()
        {
            var result = FdcParameter.Create(ParameterId, ParameterName, EquipmentId, Unit, LowerLimit, UpperLimit);
            if (result.IsFailure) return null;
            var p = result.Value;
            p.AssignToGroup(GroupId);   // DB의 GROUP_ID를 도메인에 복원 (round-trip 보존)
            if (LowerControlLimit.HasValue && UpperControlLimit.HasValue)
                p.SetControlLimits(LowerControlLimit.Value, UpperControlLimit.Value);
            if (!IsActive) p.Deactivate();
            return p;
        }

        public static ParamRow FromDomain(FdcParameter p) => new()
        {
            ParameterId        = p.Id,
            ParameterName      = p.ParameterName,
            EquipmentId        = p.EquipmentId,
            GroupId            = p.GroupId,
            Unit               = p.Unit,
            LowerLimit         = p.LowerLimit,
            UpperLimit         = p.UpperLimit,
            LowerControlLimit  = p.LowerControlLimit,
            UpperControlLimit  = p.UpperControlLimit,
            SamplingIntervalMs = p.SamplingIntervalMs,
            IsActive           = p.IsActive
        };
    }
}
