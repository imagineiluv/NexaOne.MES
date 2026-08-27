using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.Server.Gateway;

/// <summary>
/// EST 출력 서비스의 MDM 조회 어댑터입니다. Default ALC의 조립 루트만
/// MDM_EQUIPMENT/MDM_CARRIER 스키마를 알고 축소 snapshot을 반환합니다.
/// </summary>
public sealed class EquipmentOutputMasterDirectory : QueryRepository, IEquipmentOutputMasterDirectory
{
    public EquipmentOutputMasterDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<EquipmentOutputMasterScopeDto?> GetScopeAsync(
        string equipmentId,
        string? carrierId,
        CancellationToken ct = default)
    {
        var equipment = await QueryFirstOrDefaultAsync<EquipmentRow>(
            @"SELECT EQUIPMENT_ID AS EquipmentId,
                     PLANT_ID AS PlantId,
                     VALID_STATE AS ValidState
              FROM MDM_EQUIPMENT
              WHERE EQUIPMENT_ID = @equipmentId",
            new { equipmentId }, ct);
        if (equipment is null) return null;

        var carrierExists = string.IsNullOrWhiteSpace(carrierId)
            || await QueryFirstOrDefaultAsync<string>(
                "SELECT CARRIER_ID FROM MDM_CARRIER WHERE CARRIER_ID = @carrierId",
                new { carrierId }, ct) is not null;

        return new EquipmentOutputMasterScopeDto(
            equipment.EquipmentId,
            equipment.PlantId,
            string.Equals(equipment.ValidState, "Valid", StringComparison.OrdinalIgnoreCase),
            carrierExists);
    }

    private sealed class EquipmentRow
    {
        public string EquipmentId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string ValidState { get; set; } = string.Empty;
    }
}
