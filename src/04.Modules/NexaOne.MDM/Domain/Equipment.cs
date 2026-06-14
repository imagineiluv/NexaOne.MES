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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 ValidState를 항상 "Valid"로 하드코딩하고 Description 인자가 없어, 비활성화된(Invalid) 설비가
    /// 읽기경로에서 다시 "Valid"로 되살아나고 Description이 유실되는 상태손실을 막는다.</summary>
    public static Equipment Restore(
        string equipmentId, string equipmentName, string description, string plantId, string areaId,
        string equipmentType, string? parentEquipmentId, string vendor, string model,
        string equipmentClassId, string validState)
        => new(equipmentId)
        {
            EquipmentName = equipmentName,
            Description = description,
            PlantId = plantId,
            AreaId = areaId,
            EquipmentType = equipmentType,
            ParentEquipmentId = parentEquipmentId,
            Vendor = vendor,
            Model = model,
            EquipmentClassId = equipmentClassId,
            ValidState = validState
        };

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
