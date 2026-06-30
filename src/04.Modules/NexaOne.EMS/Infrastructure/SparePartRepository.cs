using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EMS.Infrastructure;

public sealed class SparePartRepository : QueryRepository, ISparePartRepository
{
    private readonly ServiceObjectProcessor _processor;

    public SparePartRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<SparePart?> GetByIdAsync(string partId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_SPARE_PART WHERE PART_ID = @partId";
        var row = await QueryFirstOrDefaultAsync<PartRow>(sql, new { partId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<SparePart>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_SPARE_PART ORDER BY PART_NAME";
        var rows = await QueryAsync<PartRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<SparePart>().ToList();
    }

    public async Task<IReadOnlyList<SparePart>> GetLowStockAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_SPARE_PART WHERE CURRENT_STOCK <= MIN_STOCK ORDER BY PART_NAME";
        var rows = await QueryAsync<PartRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<SparePart>().ToList();
    }

    public async Task AddAsync(SparePart part, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO EMS_SPARE_PART
            (PART_ID, PART_NAME, PART_NUMBER, DESCRIPTION, UNIT_OF_MEASURE,
             CURRENT_STOCK, MIN_STOCK, MAX_STOCK, LOCATION, EQUIPMENT_CLASS_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PartId, @PartName, @PartNumber, @Description, @UnitOfMeasure,
             @CurrentStock, @MinStock, @MaxStock, @Location, @EquipmentClassId,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, PartRow.FromDomain(part), ct);
    }

    public async Task UpdateAsync(SparePart part, CancellationToken ct = default)
    {
        const string sql = @"UPDATE EMS_SPARE_PART SET
            CURRENT_STOCK = @CurrentStock, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PART_ID = @PartId";
        await _processor.UpdateAsync(sql, PartRow.FromDomain(part), ct);
    }

    private sealed class PartRow
    {
        public string  PartId           { get; set; } = "";
        public string  PartName         { get; set; } = "";
        public string  PartNumber       { get; set; } = "";
        public string  Description      { get; set; } = "";
        public string  UnitOfMeasure    { get; set; } = "";
        public decimal CurrentStock     { get; set; }
        public decimal MinStock         { get; set; }
        public decimal MaxStock         { get; set; }
        public string  Location         { get; set; } = "";
        public string? EquipmentClassId { get; set; }
        public string   CreatedBy       { get; set; } = "";
        public DateTime  CreatedAt       { get; set; }
        public string?   UpdatedBy       { get; set; }
        public DateTime? UpdatedAt       { get; set; }

        public SparePart ToDomain() =>
            SparePart.Restore(PartId, PartName, PartNumber, Description, UnitOfMeasure,
                CurrentStock, MinStock, MaxStock, Location, EquipmentClassId,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static PartRow FromDomain(SparePart p) => new()
        {
            PartId = p.Id, PartName = p.PartName, PartNumber = p.PartNumber,
            Description = p.Description, UnitOfMeasure = p.UnitOfMeasure,
            CurrentStock = p.CurrentStock, MinStock = p.MinStock, MaxStock = p.MaxStock,
            Location = p.Location, EquipmentClassId = p.EquipmentClassId,
            CreatedBy = p.CreatedBy, CreatedAt = p.CreatedAt,
            UpdatedBy = p.UpdatedBy, UpdatedAt = p.UpdatedAt
        };
    }
}
