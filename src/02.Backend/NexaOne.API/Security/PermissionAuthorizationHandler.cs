using Microsoft.AspNetCore.Authorization;
using NexaOne.Common.Security;

namespace NexaOne.API.Security;

/// <summary>
/// permission 클레임으로 <see cref="PermissionRequirement"/>를 집행하는 단일 정책 집행점(PEP, ADR-003).
/// 사용자가 요구 권한을 보유하거나 전체 권한(<see cref="Permissions.All"/>="*")을 보유하면 통과한다.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var granted = context.User.FindAll(Permissions.ClaimType).Select(c => c.Value);
        if (granted.Any(p => p == Permissions.All
                || string.Equals(p, requirement.Permission, StringComparison.OrdinalIgnoreCase)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
