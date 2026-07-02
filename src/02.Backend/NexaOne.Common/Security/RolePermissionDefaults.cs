namespace NexaOne.Common.Security;

/// <summary>
/// 역할명 → 기본 권한 매핑(하위호환 시드, ADR-003). <c>SYS_ROLE.PERMISSIONS</c>의 명시 권한이 비어 있어도
/// 기존 역할 사용자가 적절한 권한을 자동 보유하도록 한다(명시 권한과 합집합으로 적용).
/// <para>SEC-2: ADMIN→'*' 하드코딩은 제거됐다 — 전체 권한은 DB(SYS_ROLE.PERMISSIONS='*', V031 시드)가 단독
/// 원천이다. roleId 문자열만으로 DB 밖에서 전권이 부여되는 숨은 경로를 없앤다. OPERATOR/VIEWER 잔여 매핑은
/// V063 표준 역할 시드가 없는 레거시 DB 하위호환용이며, 제한적(fdc 조회/제어) 권한이라 유지한다.</para>
/// </summary>
public static class RolePermissionDefaults
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OPERATOR"] = new[] { Permissions.FdcControl, Permissions.FdcRead },
        ["VIEWER"]   = new[] { Permissions.FdcRead },
    };

    public static IReadOnlyList<string> For(string? roleId)
        => roleId is not null && Map.TryGetValue(roleId, out var perms) ? perms : Array.Empty<string>();
}
