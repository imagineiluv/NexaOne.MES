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

    public void AddPermission(string permission)
    {
        if (!_permissions.Contains(permission))
            _permissions.Add(permission);
    }

    public void RemovePermission(string permission) =>
        _permissions.Remove(permission);
}
