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

    /// <summary>영속 행에서 도메인을 검증 없이 재구성한다(읽기경로 Restore 패턴).
    /// Create+뮤테이터 재구성은 영속 감사필드(CreatedBy/At·UpdatedBy/At)를 유실시키고,
    /// 검증 실패 시 행을 드롭하므로 읽기경로에서는 이 팩토리를 사용한다.</summary>
    public static FdcParameterGroup Restore(
        string groupId,
        string groupName,
        string equipmentId,
        string? description,
        int displayOrder,
        bool isActive,
        string? createdBy = null,
        DateTime? createdAt = null,
        string? updatedBy = null,
        DateTime? updatedAt = null)
    {
        var group = new FdcParameterGroup(groupId)
        {
            GroupName = groupName,
            EquipmentId = equipmentId,
            Description = description,
            DisplayOrder = displayOrder,
            IsActive = isActive
        };
        group.RestoreAudit(createdBy ?? group.CreatedBy, createdAt ?? group.CreatedAt, updatedBy, updatedAt);
        return group;
    }
}
