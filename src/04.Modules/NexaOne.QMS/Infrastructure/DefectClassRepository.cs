using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

public sealed class DefectClassRepository : QueryRepository, IDefectClassRepository
{
    private readonly ServiceObjectProcessor _processor;

    public DefectClassRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<DefectClass?> GetByIdAsync(string defectClassId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_DEFECT_CLASS WHERE DEFECT_CLASS_ID = @defectClassId";
        var row = await QueryFirstOrDefaultAsync<Row>(sql, new { defectClassId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<DefectClass>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_DEFECT_CLASS WHERE IS_DELETED = 0 ORDER BY DEFECT_CLASS_NAME";
        var rows = await QueryAsync<Row>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<DefectClass>().ToList();
    }

    public async Task AddAsync(DefectClass defectClass, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO QMS_DEFECT_CLASS
            (DEFECT_CLASS_ID, DEFECT_CLASS_NAME, DESCRIPTION, SEVERITY, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@DefectClassId, @DefectClassName, @Description, @Severity, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, Row.FromDomain(defectClass), ct);
    }

    public async Task UpdateAsync(DefectClass defectClass, CancellationToken ct = default)
    {
        const string sql = @"UPDATE QMS_DEFECT_CLASS SET
            IS_ACTIVE = @IsActive, IS_DELETED = @IsDeleted, DELETED_AT = @DeletedAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE DEFECT_CLASS_ID = @DefectClassId";
        await _processor.UpdateAsync(sql, Row.FromDomain(defectClass), ct);
    }

    private sealed class Row
    {
        public string DefectClassId   { get; set; } = "";
        public string DefectClassName { get; set; } = "";
        public string Description     { get; set; } = "";
        public string Severity        { get; set; } = "";
        public bool   IsActive        { get; set; }
        public bool   IsDeleted       { get; set; }
        public DateTime? DeletedAt    { get; set; }

        public DefectClass? ToDomain()
        {
            var r = DefectClass.Create(DefectClassId, DefectClassName, Description, Severity);
            if (r.IsFailure) return null;
            if (!IsActive) r.Value.Deactivate();
            return r.Value;
        }

        public static Row FromDomain(DefectClass d) => new()
        {
            DefectClassId = d.Id, DefectClassName = d.DefectClassName,
            Description = d.Description, Severity = d.Severity,
            IsActive = d.IsActive, IsDeleted = d.IsDeleted, DeletedAt = d.DeletedAt
        };
    }
}
