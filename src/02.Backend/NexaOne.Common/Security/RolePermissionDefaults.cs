namespace NexaOne.Common.Security;

/// <summary>
/// 역할명 → 기본 권한 매핑(하위호환 시드, ADR-003). <c>SYS_ROLE.PERMISSIONS</c>의 명시 권한이 비어 있어도
/// 기존 역할 사용자가 적절한 권한을 자동 보유하도록 한다(명시 권한과 합집합으로 적용).
/// 이로써 역할 기반 <c>[Authorize(Roles=...)]</c>를 permission 정책으로 전환해도 기존 동작이 보존된다.
/// </summary>
public static class RolePermissionDefaults
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADMIN"]    = new[] { Permissions.All },
        ["OPERATOR"] = new[] { Permissions.FdcControl, Permissions.FdcRead },
        ["VIEWER"]   = new[] { Permissions.FdcRead },
    };

    public static IReadOnlyList<string> For(string? roleId)
        => roleId is not null && Map.TryGetValue(roleId, out var perms) ? perms : Array.Empty<string>();
}
