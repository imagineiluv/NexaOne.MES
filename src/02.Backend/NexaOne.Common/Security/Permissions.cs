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
    /// <summary>설비 기동/정지/비상정지 + 운영 데이터 수집(collect-data). OPERATOR 기본 보유.</summary>
    public const string FdcControl = "fdc:control";
    /// <summary>FDC 데이터 조회.</summary>
    public const string FdcRead = "fdc:read";
    /// <summary>FDC 구성 관리(인터록 규칙/파라미터/파라미터그룹/알람설정 등록·변경). 운영 제어보다 높은 권한.</summary>
    public const string FdcManage = "fdc:manage";

    // ── 모듈별 쓰기(manage) 권한 (ADR-003 — module:action). 기본은 ADMIN(*)만 보유하며,
    //    관리자가 SYS_ROLE.PERMISSIONS로 역할에 부여한다(명시 권한 ∪ 역할 기본 매핑). ──
    public const string MdmManage = "mdm:manage";          // 기준정보(설비/플랜트/제품/코드) CRUD
    public const string CmmsManage = "cmms:manage";        // 보전(작업지시/보전계획/예비품) 쓰기
    public const string PomManage = "pom:manage";          // 생산(계획/오더/로트추적) 쓰기
    public const string ShpManage = "shp:manage";          // 출하(주문/품목/실적) 쓰기
    public const string EstManage = "est:manage";          // 설비상태(상태매트릭스/상태변경/알람) 쓰기
    public const string QmsManage = "qms:manage";          // 품질(불량/검사/SPC) 쓰기
    public const string RmsManage = "rms:manage";          // 레시피(생성/승인/배포/파라미터) 쓰기

    // ── 관리자 영역(역할 하드코딩 → 정책 전환) ──
    public const string SysManage = "sys:manage";          // 시스템 관리(사용자/역할/권한/언어 CRUD)
    public const string SysUserManage = "sys:user.manage"; // 사용자 가입 승인
    public const string DeployManage = "deploy:manage";    // 배포 파일 업로드/활성
    public const string WorkflowExecute = "workflow:execute"; // 워크플로우(*.workflow) 실행 — 등록 노드 호출 가능, 권한 필요
}
