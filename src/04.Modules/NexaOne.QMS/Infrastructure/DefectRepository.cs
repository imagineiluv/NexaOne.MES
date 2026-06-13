using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

public sealed class DefectRepository : QueryRepository, IDefectRepository
{
    private readonly ServiceObjectProcessor _processor;

    public DefectRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<Defect?> GetByIdAsync(string defectId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_DEFECT WHERE DEFECT_ID = @defectId";
        var row = await QueryFirstOrDefaultAsync<DefectRow>(sql, new { defectId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Defect>> GetByLotAsync(string lotId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_DEFECT WHERE LOT_ID = @lotId";
        var rows = await QueryAsync<DefectRow>(sql, new { lotId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Defect>().ToList();
    }

    public async Task<IReadOnlyList<Defect>> GetByEquipmentAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM QMS_DEFECT
            WHERE EQUIPMENT_ID = @equipmentId AND INSPECTED_AT BETWEEN @from AND @to";
        var rows = await QueryAsync<DefectRow>(sql, new { equipmentId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Defect>().ToList();
    }

    public async Task AddAsync(Defect defect, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO QMS_DEFECT
            (DEFECT_ID, LOT_ID, EQUIPMENT_ID, DEFECT_CLASS_ID, DEFECT_COUNT, DEFECT_RATE,
             INSPECTED_AT, INSPECTOR_ID, REMARK, IS_CONFIRMED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@DefectId, @LotId, @EquipmentId, @DefectClassId, @DefectCount, @DefectRate,
             @InspectedAt, @InspectorId, @Remark, @IsConfirmed,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, DefectRow.FromDomain(defect), ct);
    }

    public async Task UpdateAsync(Defect defect, CancellationToken ct = default)
    {
        const string sql = @"UPDATE QMS_DEFECT SET
            DEFECT_COUNT = @DefectCount, DEFECT_RATE = @DefectRate,
            IS_CONFIRMED = @IsConfirmed, CONFIRMED_AT = @ConfirmedAt, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE DEFECT_ID = @DefectId";
        await _processor.UpdateAsync(sql, DefectRow.FromDomain(defect), ct);
    }

    private sealed class DefectRow
    {
        public string DefectId { get; set; } = "";
        public string LotId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string DefectClassId { get; set; } = "";
        public int DefectCount { get; set; }
        public decimal DefectRate { get; set; }
        public DateTime InspectedAt { get; set; }
        public string InspectorId { get; set; } = "";
        public string? Remark { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime? ConfirmedAt { get; set; }

        public Defect? ToDomain() =>
            Defect.Create(DefectId, LotId, EquipmentId, DefectClassId, DefectCount, DefectRate, InspectedAt, InspectorId, Remark).Value;

        public static DefectRow FromDomain(Defect d) => new()
        {
            DefectId = d.Id,
            LotId = d.LotId,
            EquipmentId = d.EquipmentId,
            DefectClassId = d.DefectClassId,
            DefectCount = d.DefectCount,
            DefectRate = d.DefectRate,
            InspectedAt = d.InspectedAt,
            InspectorId = d.InspectorId,
            Remark = d.Remark,
            IsConfirmed = d.IsConfirmed,
            ConfirmedAt = d.ConfirmedAt
        };
    }
}
