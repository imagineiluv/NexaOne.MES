using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Infrastructure;

/// <summary>
/// MDM이 소유한 설비 마스터를 다른 모듈에 축소 snapshot으로 제공하는 adapter입니다.
/// </summary>
public sealed class EquipmentDirectory : QueryRepository, IEquipmentDirectory
{
    public EquipmentDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(
        string plantId,
        CancellationToken ct = default)
    {
        const string sql = "SELECT EQUIPMENT_ID FROM MDM_EQUIPMENT WHERE PLANT_ID = @plantId";
        return await QueryAsync<string>(sql, new { plantId }, ct);
    }

    public async Task<EquipmentDirectoryEntry?> GetEquipmentAsync(
        string equipmentId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT EQUIPMENT_ID AS EquipmentId,
                                    PLANT_ID AS PlantId,
                                    EQUIPMENT_CLASS_ID AS EquipmentClassId,
                                    VALID_STATE AS ValidState
                             FROM MDM_EQUIPMENT
                             WHERE EQUIPMENT_ID = @equipmentId";
        var row = await QueryFirstOrDefaultAsync<EquipmentRow>(sql, new { equipmentId }, ct);
        return row is null
            ? null
            : new EquipmentDirectoryEntry(
                row.EquipmentId,
                row.PlantId,
                row.EquipmentClassId,
                string.Equals(row.ValidState, "Valid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.ValidState, "Active", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> EquipmentClassExistsAsync(
        string equipmentClassId,
        CancellationToken ct = default)
        => await CountAsync(
            "SELECT COUNT(*) FROM MDM_EQUIPMENT_CLASS WHERE EQUIPMENT_CLASS_ID = @equipmentClassId",
            new { equipmentClassId },
            ct) > 0;

    private sealed class EquipmentRow
    {
        public string EquipmentId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentClassId { get; set; } = string.Empty;
        public string ValidState { get; set; } = string.Empty;
    }
}
