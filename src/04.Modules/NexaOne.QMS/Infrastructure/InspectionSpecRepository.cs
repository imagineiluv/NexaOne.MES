using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

public sealed class InspectionSpecRepository : QueryRepository, IInspectionSpecRepository
{
    private readonly ServiceObjectProcessor _processor;

    public InspectionSpecRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<InspectionSpec?> GetByIdAsync(string specId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_INSPECTION_SPEC WHERE SPEC_ID = @specId";
        var row = await QueryFirstOrDefaultAsync<Row>(sql, new { specId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<InspectionSpec>> GetByProcessAsync(string processId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_INSPECTION_SPEC WHERE PROCESS_ID = @processId AND IS_ACTIVE = 1 ORDER BY SPEC_NAME";
        var rows = await QueryAsync<Row>(sql, new { processId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<InspectionSpec>().ToList();
    }

    public async Task<IReadOnlyList<InspectionSpec>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_INSPECTION_SPEC WHERE IS_ACTIVE = 1 ORDER BY SPEC_NAME";
        var rows = await QueryAsync<Row>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<InspectionSpec>().ToList();
    }

    public async Task AddAsync(InspectionSpec spec, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO QMS_INSPECTION_SPEC
            (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE,
             NOMINAL_VALUE, TOLERANCE_PLUS, TOLERANCE_MINUS, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@SpecId, @SpecName, @ProcessId, @ItemName, @MeasureType,
             @NominalValue, @TolerancePlus, @ToleranceMinus, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, Row.FromDomain(spec), ct);
    }

    private sealed class Row
    {
        public string   SpecId         { get; set; } = "";
        public string   SpecName       { get; set; } = "";
        public string   ProcessId      { get; set; } = "";
        public string   ItemName       { get; set; } = "";
        public string   MeasureType    { get; set; } = "";
        public decimal? NominalValue   { get; set; }
        public decimal? TolerancePlus  { get; set; }
        public decimal? ToleranceMinus { get; set; }
        public bool     IsActive       { get; set; }

        public InspectionSpec? ToDomain() =>
            InspectionSpec.Create(SpecId, SpecName, ProcessId, ItemName, MeasureType,
                NominalValue, TolerancePlus, ToleranceMinus).Value;

        public static Row FromDomain(InspectionSpec s) => new()
        {
            SpecId = s.Id, SpecName = s.SpecName, ProcessId = s.ProcessId,
            ItemName = s.ItemName, MeasureType = s.MeasureType,
            NominalValue = s.NominalValue, TolerancePlus = s.TolerancePlus,
            ToleranceMinus = s.ToleranceMinus, IsActive = s.IsActive
        };
    }
}
