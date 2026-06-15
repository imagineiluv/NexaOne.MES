using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

public sealed class SpcParamRepository : QueryRepository, ISpcParamRepository
{
    private readonly ServiceObjectProcessor _processor;

    public SpcParamRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<SpcParam?> GetByIdAsync(string paramId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_SPC_PARAM WHERE PARAM_ID = @paramId";
        var row = await QueryFirstOrDefaultAsync<Row>(sql, new { paramId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<SpcParam>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_SPC_PARAM WHERE EQUIPMENT_ID = @equipmentId AND IS_ACTIVE = 1 ORDER BY PARAM_NAME";
        var rows = await QueryAsync<Row>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<SpcParam>().ToList();
    }

    public async Task AddAsync(SpcParam param, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO QMS_SPC_PARAM
            (PARAM_ID, PARAM_NAME, EQUIPMENT_ID, PROCESS_ID, MEAN, UCL, LCL, USL, LSL,
             SAMPLE_SIZE, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@ParamId, @ParamName, @EquipmentId, @ProcessId, @Mean, @Ucl, @Lcl, @Usl, @Lsl,
             @SampleSize, @IsActive, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, Row.FromDomain(param), ct);
    }

    public async Task UpdateAsync(SpcParam param, CancellationToken ct = default)
    {
        const string sql = @"UPDATE QMS_SPC_PARAM SET
            MEAN = @Mean, UCL = @Ucl, LCL = @Lcl, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PARAM_ID = @ParamId";
        await _processor.UpdateAsync(sql, Row.FromDomain(param), ct);
    }

    private sealed class Row
    {
        public string   ParamId     { get; set; } = "";
        public string   ParamName   { get; set; } = "";
        public string   EquipmentId { get; set; } = "";
        public string   ProcessId   { get; set; } = "";
        public decimal  Mean        { get; set; }
        public decimal  Ucl         { get; set; }
        public decimal  Lcl         { get; set; }
        public decimal? Usl         { get; set; }
        public decimal? Lsl         { get; set; }
        public int      SampleSize  { get; set; }
        public bool     IsActive    { get; set; }

        public SpcParam ToDomain() =>
            SpcParam.Restore(ParamId, ParamName, EquipmentId, ProcessId, Mean, Ucl, Lcl, Usl, Lsl,
                SampleSize, IsActive);

        public static Row FromDomain(SpcParam p) => new()
        {
            ParamId = p.Id, ParamName = p.ParamName, EquipmentId = p.EquipmentId, ProcessId = p.ProcessId,
            Mean = p.Mean, Ucl = p.Ucl, Lcl = p.Lcl, Usl = p.Usl, Lsl = p.Lsl,
            SampleSize = p.SampleSize, IsActive = p.IsActive
        };
    }
}
