using NexaOne.Common;

namespace NexaOne.MDM.Domain;

public sealed class Equipment : AuditableEntity<string>
{
    private Equipment(string equipmentId) : base(equipmentId) { }

    public string EquipmentName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;
    public string AreaId { get; private set; } = string.Empty;
    public string EquipmentType { get; private set; } = string.Empty;
    public string? ParentEquipmentId { get; private set; }
    public string Vendor { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string ValidState { get; private set; } = "Valid";
    public string EquipmentClassId { get; private set; } = string.Empty;

    public static Result<Equipment> Create(
        string equipmentId,
        string equipmentName,
        string plantId,
        string areaId,
        string equipmentType,
        string? parentEquipmentId = null,
        string vendor = "",
        string model = "",
        string equipmentClassId = "")
    {
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<Equipment>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentName))
            return Result.Failure<Equipment>(Error.Validation(nameof(equipmentName), "Equipment name is required."));

        var equipment = new Equipment(equipmentId)
        {
            EquipmentName = equipmentName,
            PlantId = plantId,
            AreaId = areaId,
            EquipmentType = equipmentType,
            ParentEquipmentId = parentEquipmentId,
            Vendor = vendor,
            Model = model,
            EquipmentClassId = equipmentClassId,
            ValidState = "Valid"
        };
        return equipment;
    }

    public void Deactivate() => ValidState = "Invalid";
    public void Activate() => ValidState = "Valid";
    public void ChangeParent(string? parentId) => ParentEquipmentId = parentId;

    public void UpdateInfo(string name, string description, string equipmentType, string vendor, string model)
    {
        EquipmentName = name;
        Description = description;
        EquipmentType = equipmentType;
        Vendor = vendor;
        Model = model;
    }
}
