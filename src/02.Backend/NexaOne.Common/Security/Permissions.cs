namespace NexaOne.Common.Security;

/// <summary>
/// 권한 분류(<c>module:action</c>). <c>[Authorize(Policy="perm:{value}")]</c>로 집행한다(ADR-003).
/// 토큰의 <see cref="ClaimType"/> 클레임으로 발급되며, <c>"*"</c>(<see cref="All"/>)는 전체 권한으로 통과한다.
/// </summary>
public static class Permissions
{
    /// <summary>permission 클레임 타입.</summary>
    public const string ClaimType = "permission";

    /// <summary>전체 권한(ADMIN). 핸들러가 와일드카드로 통과시킨다.</summary>
    public const string All = "*";

    // ── FDC (대표 슬라이스 — §10.4.4 설비 제어) ──
    /// <summary>설비 기동/정지/비상정지.</summary>
    public const string FdcControl = "fdc:control";
    /// <summary>FDC 데이터 조회.</summary>
    public const string FdcRead = "fdc:read";

    // ── 점진 채택 예시(나머지 컨트롤러 전환 시 확장) ──
    public const string MdmManage = "mdm:manage";
    public const string SysUserManage = "sys:user.manage";
    public const string DeployManage = "deploy:manage";
}
