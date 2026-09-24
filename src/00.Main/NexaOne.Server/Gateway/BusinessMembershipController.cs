using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/sys/business-memberships/{tenantId:guid}/{organizationId:guid}")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class BusinessMembershipController(IBusinessMembershipBridge memberships) : ControllerBase
{
    [HttpGet("~/api/v1/sys/business-membership-permissions")]
    [RequirePermission(Permissions.SysManage)]
    public ActionResult<IReadOnlyList<string>> GetSupportedPermissions()
        => Ok(BusinessOperationPermissionCatalog.All);

    [HttpGet("me")]
    public async Task<IActionResult> GetAccess(Guid tenantId, Guid organizationId, CancellationToken ct)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        var membership = await memberships.GetAccessAsync(userId, tenantId, organizationId, ct);
        return membership is null ? Forbid() : Ok(membership);
    }

    [HttpGet("users/{userId}")]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> GetMembership(
        Guid tenantId, Guid organizationId, string userId, CancellationToken ct)
    {
        var administrator = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(administrator)) return Unauthorized();
        try
        {
            return (await memberships.GetMembershipAsync(administrator, tenantId, organizationId, userId, ct))
                .ToActionResult();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPut("users/{userId}")]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> SaveMembership(
        Guid tenantId, Guid organizationId, string userId,
        [FromBody] BusinessMembershipChange change, CancellationToken ct)
    {
        var administrator = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(administrator)) return Unauthorized();
        try
        {
            return (await memberships.SaveMembershipAsync(administrator, tenantId, organizationId, userId, change, ct))
                .ToActionResult();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
}
