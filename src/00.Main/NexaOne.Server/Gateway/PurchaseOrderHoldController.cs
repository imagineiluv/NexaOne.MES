using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.Server.Gateway;

/// <summary>JWT-authenticated PRC purchase-order hold transitions.</summary>
[ApiController]
[Authorize]
[Route("api/v1/prc/purchase-orders/{purchaseOrderId}")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class PurchaseOrderHoldController(IPurchaseOrderHoldBridge bridge) : ControllerBase
{
    [HttpPost("hold")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Hold(string purchaseOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.HoldAsync(purchaseOrderId, actor, ct)).ToActionResult();
    }

    [HttpPost("release")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Release(string purchaseOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.ReleaseAsync(purchaseOrderId, actor, ct)).ToActionResult();
    }
}
