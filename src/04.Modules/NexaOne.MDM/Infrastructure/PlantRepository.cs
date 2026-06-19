using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Infrastructure;

public sealed class PlantRepository : QueryRepository, IPlantRepository
{
    private readonly ServiceObjectProcessor _processor;

    public PlantRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<Plant?> GetByIdAsync(string plantId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_PLANT WHERE PLANT_ID = @plantId";
        var row = await QueryFirstOrDefaultAsync<PlantRow>(sql, new { plantId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Plant>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_PLANT ORDER BY PLANT_NAME";
        var rows = await QueryAsync<PlantRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Plant>().ToList();
    }

    public async Task AddAsync(Plant plant, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO MDM_PLANT
            (PLANT_ID, PLANT_NAME, DESCRIPTION, COUNTRY, TIME_ZONE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PlantId, @PlantName, @Description, @Country, @TimeZone,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, PlantRow.FromDomain(plant), ct);
    }

    public async Task UpdateAsync(Plant plant, CancellationToken ct = default)
    {
        const string sql = @"UPDATE MDM_PLANT SET
            PLANT_NAME = @PlantName, DESCRIPTION = @Description,
            COUNTRY = @Country, TIME_ZONE = @TimeZone,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PLANT_ID = @PlantId";
        await _processor.UpdateAsync(sql, PlantRow.FromDomain(plant), ct);
    }

    private sealed class PlantRow
    {
        public string PlantId     { get; set; } = "";
        public string PlantName   { get; set; } = "";
        public string Description { get; set; } = "";
        public string Country     { get; set; } = "";
        public string TimeZone    { get; set; } = "";

        public string CreatedBy   { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy  { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public Plant? ToDomain() =>
            Plant.Restore(PlantId, PlantName, Description, Country, TimeZone,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static PlantRow FromDomain(Plant p) => new()
        {
            PlantId = p.Id, PlantName = p.PlantName,
            Description = p.Description, Country = p.Country, TimeZone = p.TimeZone
        };
    }
}
