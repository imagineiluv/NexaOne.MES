using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

public sealed class InspectionResultRepository : QueryRepository, IInspectionResultRepository
{
    private readonly ServiceObjectProcessor _processor;

    public InspectionResultRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<IReadOnlyList<InspectionResult>> GetByLotAsync(string lotId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_INSPECTION_RESULT WHERE LOT_ID = @lotId ORDER BY INSPECTED_AT DESC";
        var rows = await QueryAsync<Row>(sql, new { lotId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<InspectionResult>().ToList();
    }

    public async Task<IReadOnlyList<InspectionResult>> GetBySpecAsync(string specId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_INSPECTION_RESULT WHERE SPEC_ID = @specId ORDER BY INSPECTED_AT DESC";
        var rows = await QueryAsync<Row>(sql, new { specId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<InspectionResult>().ToList();
    }

    public async Task AddAsync(InspectionResult result, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO QMS_INSPECTION_RESULT
            (RESULT_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, MEASURED_VALUE, ATTRIBUTE_RESULT,
             INSPECTED_AT, INSPECTOR_ID, IS_PASS, REMARK,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@ResultId, @SpecId, @LotId, @EquipmentId, @MeasuredValue, @AttributeResult,
             @InspectedAt, @InspectorId, @IsPass, @Remark,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, Row.FromDomain(result), ct);
    }

    private sealed class Row
    {
        public string   ResultId        { get; set; } = "";
        public string   SpecId          { get; set; } = "";
        public string   LotId           { get; set; } = "";
        public string   EquipmentId     { get; set; } = "";
        public decimal? MeasuredValue   { get; set; }
        public string?  AttributeResult { get; set; }
        public DateTime InspectedAt     { get; set; }
        public string   InspectorId     { get; set; } = "";
        public bool     IsPass          { get; set; }
        public string?  Remark          { get; set; }
        public string   CreatedBy       { get; set; } = "";
        public DateTime  CreatedAt       { get; set; }
        public string?  UpdatedBy       { get; set; }
        public DateTime? UpdatedAt       { get; set; }

        public InspectionResult ToDomain() =>
            InspectionResult.Restore(ResultId, SpecId, LotId, EquipmentId, MeasuredValue, AttributeResult,
                InspectedAt, InspectorId, IsPass, Remark,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static Row FromDomain(InspectionResult r) => new()
        {
            ResultId = r.Id, SpecId = r.SpecId, LotId = r.LotId, EquipmentId = r.EquipmentId,
            MeasuredValue = r.MeasuredValue, AttributeResult = r.AttributeResult,
            InspectedAt = r.InspectedAt, InspectorId = r.InspectorId,
            IsPass = r.IsPass, Remark = r.Remark
        };
    }
}
