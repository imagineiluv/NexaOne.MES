using NexaOne.Common;

namespace NexaOne.SYS.Domain;

public sealed class Role : AuditableEntity<string>
{
    private readonly List<string> _permissions = [];

    private Role(string roleId) : base(roleId) { }

    public string RoleName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public IReadOnlyList<string> Permissions => _permissions.AsReadOnly();

    public static Role Create(string roleId, string roleName, string description = "")
    {
        return new Role(roleId) { RoleName = roleName, Description = description };
    }

    /// <summary>영속된 행을 검증 없이 도메인으로 복원한다(읽기경로 Restore 패턴 — FdcAlarmConfig와 동일).
    /// 구버전 RoleRow.ToDomain은 Create + AddPermission으로 재구성해 (1) 감사 메타데이터(CreatedBy/CreatedAt/
    /// UpdatedBy/UpdatedAt)를 매 읽기마다 UtcNow/""로 리셋했고, (2) 소프트삭제 상태(IsDeleted)를 도메인에
    /// 복원하지 않아 삭제된 역할이 비삭제로 둔갑할 수 있었다. Restore는 권한·삭제상태·감사값을 행값 그대로 복원한다.</summary>
    public static Role Restore(
        string roleId,
        string roleName,
        string description,
        IEnumerable<string> permissions,
        bool isDeleted,
        string? createdBy = null,
        DateTime? createdAt = null,
        string? updatedBy = null,
        DateTime? updatedAt = null)
    {
        var role = new Role(roleId) { RoleName = roleName, Description = description };
        foreach (var p in permissions)
            if (!role._permissions.Contains(p))
                role._permissions.Add(p);
        role.RestoreSoftDelete(isDeleted, null);
        role.RestoreAudit(createdBy ?? role.CreatedBy, createdAt ?? role.CreatedAt, updatedBy, updatedAt);
        return role;
    }

    public void AddPermission(string permission)
    {
        if (!_permissions.Contains(permission))
            _permissions.Add(permission);
    }

    public void RemovePermission(string permission) =>
        _permissions.Remove(permission);
}
