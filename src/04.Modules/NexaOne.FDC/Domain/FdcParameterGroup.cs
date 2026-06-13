using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>FDC 파라미터 그룹 마스터 (FDC_PARAMETER_GROUP, design 10.4.1).
/// 설비별 파라미터(FdcParameter.GroupId)를 묶어 화면에서 그룹 단위로 관리한다.</summary>
public sealed class FdcParameterGroup : AuditableEntity<string>
{
    private FdcParameterGroup(string groupId) : base(groupId) { }

    public string GroupName { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public int DisplayOrder { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<FdcParameterGroup> Create(
        string groupId,
        string groupName,
        string equipmentId,
        string? description = null,
        int displayOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return Result.Failure<FdcParameterGroup>(Error.Validation(nameof(groupId), "Group ID is required."));
        if (string.IsNullOrWhiteSpace(groupName))
            return Result.Failure<FdcParameterGroup>(Error.Validation(nameof(groupName), "Group name is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcParameterGroup>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (displayOrder < 0)
            return Result.Failure<FdcParameterGroup>(Error.Validation(nameof(displayOrder), "Display order must not be negative."));

        var group = new FdcParameterGroup(groupId)
        {
            GroupName = groupName,
            EquipmentId = equipmentId,
            Description = description,
            DisplayOrder = displayOrder,
            IsActive = true
        };
        return group;
    }

    public void Rename(string groupName)
    {
        if (!string.IsNullOrWhiteSpace(groupName))
            GroupName = groupName;
    }

    public void SetDescription(string? description) => Description = description;

    public void SetDisplayOrder(int displayOrder)
    {
        if (displayOrder >= 0)
            DisplayOrder = displayOrder;
    }

    public void Deactivate() => IsActive = false;
}
