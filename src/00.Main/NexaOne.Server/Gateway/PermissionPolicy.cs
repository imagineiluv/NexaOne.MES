using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>ADR-003 권한 정책 선언화(CQ-3) — 액션별 수동 가드(<c>if (!User.HasPermission(...)) return Forbid();</c>)를
/// <c>[RequirePermission(Permissions.X)]</c> 특성으로 대체한다. 정책명은 <c>perm:{value}</c>로 동적 생성되며
/// 판정은 기존 <see cref="ClaimsPrincipalExtensions.HasPermission"/>(정확 일치 또는 '*')를 그대로 사용해
/// 수동 가드와 의미가 1:1 동일하다(403). 데이터 구동 권한(QueryGateway의 registry requiredPermission)은
/// 런타임 값이라 특성화 대상이 아니며 수동 검사로 남는다.</summary>
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public RequirePermissionAttribute(string permission) => Policy = PolicyPrefix + permission;
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasPermission(requirement.Permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>"perm:" 접두 정책을 동적으로 구성하고, 그 외 정책명은 기본 제공자로 위임한다.</summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var permission = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }
        return _fallback.GetPolicyAsync(policyName);
    }
}
