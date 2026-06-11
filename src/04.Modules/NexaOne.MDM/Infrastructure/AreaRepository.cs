using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Infrastructure;

public sealed class AreaRepository : QueryRepository, IAreaRepository
{
    private readonly ServiceObjectProcessor _processor;

    public AreaRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<Area?> GetByIdAsync(string areaId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_AREA WITH(NOLOCK) WHERE AREA_ID = @areaId";
        var row = await QueryFirstOrDefaultAsync<AreaRow>(sql, new { areaId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Area>> GetByPlantAsync(string plantId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_AREA WITH(NOLOCK) WHERE PLANT_ID = @plantId ORDER BY AREA_NAME";
        var rows = await QueryAsync<AreaRow>(sql, new { plantId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Area>().ToList();
    }

    public async Task AddAsync(Area area, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO MDM_AREA
            (AREA_ID, AREA_NAME, DESCRIPTION, PLANT_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@AreaId, @AreaName, @Description, @PlantId,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, AreaRow.FromDomain(area), ct);
    }

    public async Task UpdateAsync(Area area, CancellationToken ct = default)
    {
        const string sql = @"UPDATE MDM_AREA SET
            AREA_NAME = @AreaName, DESCRIPTION = @Description,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE AREA_ID = @AreaId";
        await _processor.UpdateAsync(sql, AreaRow.FromDomain(area), ct);
    }

    private sealed class AreaRow
    {
        public string AreaId      { get; set; } = "";
        public string AreaName    { get; set; } = "";
        public string Description { get; set; } = "";
        public string PlantId     { get; set; } = "";

        public Area? ToDomain() => Area.Create(AreaId, AreaName, PlantId).Value;

        public static AreaRow FromDomain(Area a) => new()
        {
            AreaId = a.Id, AreaName = a.AreaName,
            Description = a.Description, PlantId = a.PlantId
        };
    }
}
