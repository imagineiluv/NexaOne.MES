using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.Server.Gateway;

/// <summary>JWT-authenticated purchase-order status transitions owned by PRC.</summary>
[ApiController]
[Authorize]
[Route("api/v1/prc/purchase-orders/{purchaseOrderId}")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class PurchaseOrderCommandController(IPurchaseOrderCommandBridge bridge) : ControllerBase
{
    [HttpPost("order")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Order(string purchaseOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.OrderAsync(purchaseOrderId, actor, ct)).ToActionResult();
    }

    [HttpPost("close")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Close(string purchaseOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.CloseAsync(purchaseOrderId, actor, ct)).ToActionResult();
    }
}
