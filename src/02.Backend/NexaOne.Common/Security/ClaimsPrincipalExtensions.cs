using System.Security.Claims;

namespace NexaOne.Common.Security;

/// <summary>
/// <see cref="ClaimsPrincipal"/> 기반 권한/신원 클레임 판정의 단일 원천(ADR-003). 게이트웨이 컨트롤러마다
/// 복제돼 있던 <c>HasPermission</c>(보안 임계 PEP)·<c>CurrentUserId</c>(신원 클레임 순회)를 한 곳으로 끌어올린다 —
/// 권한 모델이 바뀌면 여기 한 군데만 고치면 되고, 누락 복제로 모듈별 인가가 조용히 분기하는 사고를 막는다.
/// 폴백 문자열(예: "SYSTEM" / "")은 호출부마다 다르므로 의도적으로 여기에 굽지 않는다 —
/// <see cref="CurrentUserId"/>는 클레임이 없으면 <c>null</c>을 반환하고, 각 호출부가 자기 폴백을 유지한다.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// 사용자가 <paramref name="requiredPermission"/> 권한을 보유하는지 판정한다. <see cref="Permissions.ClaimType"/>
    /// 클레임을 모두 훑어 와일드카드(<see cref="Permissions.All"/> = <c>"*"</c>, 전체 권한)이거나 대소문자 무시로
    /// 일치하면 통과한다. 게이트웨이 컨트롤러 12곳에 동일하게 복제돼 있던 로직을 그대로 끌어올린 것이다.
    /// </summary>
    public static bool HasPermission(this ClaimsPrincipal user, string requiredPermission) =>
        user.FindAll(Permissions.ClaimType)
            .Any(c => c.Value == Permissions.All
                   || string.Equals(c.Value, requiredPermission, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 현재 사용자의 식별자를 신원 클레임에서 순회 조회한다: <see cref="ClaimTypes.NameIdentifier"/>(JwtBearer가
    /// 인바운드 매핑으로 <c>sub</c>를 옮겨 담는 자리) 우선, 없으면 매핑되지 않은 <c>"sub"</c> 클레임을 본다.
    /// 식별자 클레임이 전혀 없으면 <c>null</c>을 반환한다 — 폴백("SYSTEM"/"") 결정은 각 호출부의 책임이다(클래스 설명 참조).
    /// </summary>
    public static string? CurrentUserId(this ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
}
