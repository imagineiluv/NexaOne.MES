using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using NexaOne.API.Security;
using NexaOne.Common.Security;

namespace NexaOne.UnitTests.Security;

/// <summary>ADR-003 — 권한 기반 PEP: 핸들러(권한/와일드카드 집행)와 PolicyProvider(perm: 동적 생성·위임)를 검증.</summary>
public sealed class PermissionAuthorizationTests
{
    private static AuthorizationHandlerContext ContextWith(string requiredPerm, params string[] grantedPerms)
    {
        var requirement = new PermissionRequirement(requiredPerm);
        var claims = grantedPerms.Select(p => new Claim(Permissions.ClaimType, p));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return new AuthorizationHandlerContext(new[] { requirement }, user, resource: null);
    }

    [Fact]
    public async Task Handler_grants_when_user_has_exact_permission()
    {
        var ctx = ContextWith("fdc:control", "fdc:control", "fdc:read");
        await new PermissionAuthorizationHandler().HandleAsync(ctx);
        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Handler_grants_when_user_has_wildcard()
    {
        var ctx = ContextWith("fdc:control", Permissions.All);
        await new PermissionAuthorizationHandler().HandleAsync(ctx);
        ctx.HasSucceeded.Should().BeTrue("ADMIN의 전체 권한(*)은 모든 요건을 통과한다");
    }

    [Fact]
    public async Task Handler_denies_when_user_lacks_permission()
    {
        var ctx = ContextWith("fdc:control", "fdc:read");
        await new PermissionAuthorizationHandler().HandleAsync(ctx);
        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task PolicyProvider_creates_requirement_for_perm_prefix()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync("perm:fdc:control");

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<PermissionRequirement>().Should().ContainSingle()
              .Which.Permission.Should().Be("fdc:control");
    }

    [Fact]
    public async Task PolicyProvider_delegates_non_perm_policies()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync("SomeUndefinedPolicy");

        policy.Should().BeNull("비-perm 정책은 기본 제공자로 위임(미정의 시 null)");
    }
}
