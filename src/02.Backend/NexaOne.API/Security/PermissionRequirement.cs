using Microsoft.AspNetCore.Authorization;

namespace NexaOne.API.Security;

/// <summary>특정 권한(<c>module:action</c>)을 요구하는 인가 요건 (ADR-003).</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission) => Permission = permission;
}
