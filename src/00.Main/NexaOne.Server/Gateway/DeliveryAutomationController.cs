using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Collaboration;

namespace NexaOne.Server.Gateway;

/// <summary>Administration boundary for non-interactive outbound delivery authority.</summary>
[ApiController]
[Authorize]
[RequirePermission(Permissions.SysManage)]
[Route("api/v1/collaboration/delivery-automation/principals")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class DeliveryAutomationController(IDeliveryAutomationBridge automation) : ControllerBase
{
    [HttpGet("{principalId}")]
    public Task<IActionResult> GetPrincipal(string principalId, CancellationToken ct)
        => Execute(administrator => automation.GetPrincipalAsync(administrator, principalId, ct));

    [HttpPut("{principalId}")]
    public Task<IActionResult> SavePrincipal(
        string principalId, [FromBody] DeliveryServicePrincipalChange change, CancellationToken ct)
        => Execute(administrator => automation.SavePrincipalAsync(administrator, principalId, change, ct));

    [HttpGet("{principalId}/scopes/{tenantId:guid}/{organizationId:guid}")]
    public Task<IActionResult> GetScope(
        string principalId, Guid tenantId, Guid organizationId, CancellationToken ct)
        => Execute(administrator => automation.GetScopeAsync(
            administrator, principalId, tenantId, organizationId, ct));

    [HttpPut("{principalId}/scopes/{tenantId:guid}/{organizationId:guid}")]
    public Task<IActionResult> SaveScope(
        string principalId, Guid tenantId, Guid organizationId,
        [FromBody] DeliveryServicePrincipalScopeChange change, CancellationToken ct)
        => Execute(administrator => automation.SaveScopeAsync(
            administrator, principalId, tenantId, organizationId, change, ct));

    private async Task<IActionResult> Execute<T>(Func<string, Task<Result<T>>> action)
    {
        var administrator = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(administrator)) return Unauthorized();
        try { return (await action(administrator)).ToActionResult(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
}
