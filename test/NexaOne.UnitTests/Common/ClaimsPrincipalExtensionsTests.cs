using System.Security.Claims;
using NexaOne.Common.Security;

namespace NexaOne.UnitTests.Common;

/// <summary>ADR-003 — 게이트웨이 컨트롤러에서 끌어올린 권한/신원 클레임 판정 단일 원천
/// (<see cref="ClaimsPrincipalExtensions"/>)을 검증한다. HasPermission(정확 일치·와일드카드·미보유 거부)과
/// CurrentUserId(클레임 순회·부재 시 null)를 다룬다 — 폴백 문자열은 호출부 책임이라 여기서 검증하지 않는다.</summary>
public sealed class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal UserWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    private static Claim Perm(string value) => new(Permissions.ClaimType, value);

    // ── HasPermission ────────────────────────────────────────────────────────

    [Fact]
    public void HasPermission_exact_match_grants()
    {
        var user = UserWith(Perm("est:manage"));
        user.HasPermission("est:manage").Should().BeTrue();
    }

    [Fact]
    public void HasPermission_is_case_insensitive()
    {
        var user = UserWith(Perm("EST:Manage"));
        user.HasPermission("est:manage").Should().BeTrue("권한 비교는 OrdinalIgnoreCase");
    }

    [Fact]
    public void HasPermission_wildcard_grants_everything()
    {
        var user = UserWith(Perm(Permissions.All)); // "*"
        user.HasPermission("est:manage").Should().BeTrue();
        user.HasPermission("any:thing").Should().BeTrue();
    }

    [Fact]
    public void HasPermission_missing_permission_denies()
    {
        var user = UserWith(Perm("fdc:read"));
        user.HasPermission("est:manage").Should().BeFalse("미보유 권한은 거부");
    }

    [Fact]
    public void HasPermission_module_manage_includes_same_module_read()
    {
        var user = UserWith(Perm("mdm:manage"));

        user.HasPermission("mdm:read").Should().BeTrue("관리자는 같은 모듈 조회도 수행할 수 있어야 한다");
        user.HasPermission("qms:read").Should().BeFalse("다른 모듈 조회 권한까지 확장되면 안 된다");
    }

    [Fact]
    public void HasPermission_module_read_does_not_include_manage()
    {
        var user = UserWith(Perm("mdm:read"));

        user.HasPermission("mdm:manage").Should().BeFalse("조회 권한은 쓰기 권한을 포함하지 않는다");
    }

    [Fact]
    public void HasPermission_no_permission_claims_denies()
    {
        var user = UserWith(new Claim(ClaimTypes.NameIdentifier, "u1"));
        user.HasPermission("est:manage").Should().BeFalse();
    }

    // ── CurrentUserId ─────────────────────────────────────────────────────────

    [Fact]
    public void CurrentUserId_returns_name_identifier_claim()
    {
        var user = UserWith(new Claim(ClaimTypes.NameIdentifier, "operator-7"));
        user.CurrentUserId().Should().Be("operator-7");
    }

    [Fact]
    public void CurrentUserId_falls_back_to_sub_claim()
    {
        var user = UserWith(new Claim("sub", "from-sub"));
        user.CurrentUserId().Should().Be("from-sub", "NameIdentifier 부재 시 unmapped sub로 순회");
    }

    [Fact]
    public void CurrentUserId_prefers_name_identifier_over_sub()
    {
        var user = UserWith(
            new Claim(ClaimTypes.NameIdentifier, "primary"),
            new Claim("sub", "secondary"));
        user.CurrentUserId().Should().Be("primary");
    }

    [Fact]
    public void CurrentUserId_returns_null_when_no_id_claim()
    {
        var user = UserWith(Perm("est:manage")); // 권한만 있고 신원 클레임 없음
        user.CurrentUserId().Should().BeNull("식별자 클레임이 없으면 null — 폴백은 호출부 책임");
    }
}
