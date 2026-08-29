namespace NexaOne.Common.Security;

/// <summary>
/// 레거시 SQLite/설치 스크립트가 표준 역할 행을 DB에 보정할 때 참고하는 최소 호환 카탈로그(ADR-003).
/// 런타임 토큰 발급에서는 이 값을 <c>SYS_ROLE.PERMISSIONS</c>와 합성하지 않는다.
/// <para>SEC-2: ADMIN→'*' 하드코딩은 제거됐다 — 전체 권한은 DB(SYS_ROLE.PERMISSIONS='*', V031 시드)가 단독
/// 원천이다. OPERATOR/VIEWER 값만 V063 이전 bootstrap 호환을 위해 남긴다. V118에서 새로 도입한
/// MAINTENANCE는 반드시 DB 시드를 사용하므로 코드 기본값을 두지 않는다.</para>
/// </summary>
public static class RolePermissionDefaults
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OPERATOR"] = new[]
        {
            Permissions.FdcControl, Permissions.FdcRead,
            Permissions.MdmRead, Permissions.EstRead, Permissions.PomRead, Permissions.PomExecute,
            Permissions.PomRoutingRequest,
            Permissions.RmsRead,
        },
        ["VIEWER"] = new[] { Permissions.FdcRead },
    };

    public static IReadOnlyList<string> For(string? roleId)
        => roleId is not null && Map.TryGetValue(roleId, out var perms) ? perms : Array.Empty<string>();
}
