using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Infrastructure;

public sealed class EquipmentRepository : QueryRepository, IEquipmentRepository
{
    private readonly ServiceObjectProcessor _processor;

    public EquipmentRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<Equipment?> GetByIdAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID = @equipmentId";
        var row = await QueryFirstOrDefaultAsync<EquipmentRow>(sql, new { equipmentId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Equipment>> GetAllByPlantAsync(string plantId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_EQUIPMENT WHERE PLANT_ID = @plantId";
        var rows = await QueryAsync<EquipmentRow>(sql, new { plantId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Equipment>().ToList();
    }

    public async Task<bool> ExistsAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(1) FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID = @equipmentId";
        var count = await CountAsync(sql, new { equipmentId }, ct);
        return count > 0;
    }

    public async Task AddAsync(Equipment equipment, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO MDM_EQUIPMENT
            (EQUIPMENT_ID, EQUIPMENT_NAME, DESCRIPTION, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
             PARENT_EQUIPMENT_ID, VENDOR, MODEL, EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@EquipmentId, @EquipmentName, @Description, @PlantId, @AreaId, @EquipmentType,
             @ParentEquipmentId, @Vendor, @Model, @EquipmentClassId, @ValidState, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, EquipmentRow.FromDomain(equipment), ct);
    }

    public async Task UpdateAsync(Equipment equipment, CancellationToken ct = default)
    {
        const string sql = @"UPDATE MDM_EQUIPMENT SET
            EQUIPMENT_NAME = @EquipmentName, DESCRIPTION = @Description, EQUIPMENT_TYPE = @EquipmentType,
            VENDOR = @Vendor, MODEL = @Model, VALID_STATE = @ValidState, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE EQUIPMENT_ID = @EquipmentId";
        await _processor.UpdateAsync(sql, EquipmentRow.FromDomain(equipment), ct);
    }

    private sealed class EquipmentRow
    {
        public string EquipmentId { get; set; } = "";
        public string EquipmentName { get; set; } = "";
        public string Description { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string AreaId { get; set; } = "";
        public string EquipmentType { get; set; } = "";
        public string? ParentEquipmentId { get; set; }
        public string Vendor { get; set; } = "";
        public string Model { get; set; } = "";
        public string EquipmentClassId { get; set; } = "";
        public string ValidState { get; set; } = "Valid";

        public Equipment? ToDomain() =>
            Equipment.Create(EquipmentId, EquipmentName, PlantId, AreaId, EquipmentType,
                ParentEquipmentId, Vendor, Model, EquipmentClassId).Value;

        public static EquipmentRow FromDomain(Equipment e) => new()
        {
            EquipmentId = e.Id,
            EquipmentName = e.EquipmentName,
            Description = e.Description,
            PlantId = e.PlantId,
            AreaId = e.AreaId,
            EquipmentType = e.EquipmentType,
            ParentEquipmentId = e.ParentEquipmentId,
            Vendor = e.Vendor,
            Model = e.Model,
            EquipmentClassId = e.EquipmentClassId,
            ValidState = e.ValidState
        };
    }
}
