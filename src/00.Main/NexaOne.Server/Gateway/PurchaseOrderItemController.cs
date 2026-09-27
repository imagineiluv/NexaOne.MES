using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.Server.Gateway;

/// <summary>JWT-authenticated PRC Draft line commands and readback.</summary>
[ApiController]
[Authorize]
[Route("api/v1/prc/purchase-orders/{purchaseOrderId}/items")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class PurchaseOrderItemController(IPurchaseOrderItemBridge bridge) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.PrcRead)]
    public async Task<IActionResult> List(string purchaseOrderId, CancellationToken ct) =>
        (await bridge.ListAsync(purchaseOrderId, ct)).ToActionResult();

    [HttpPut("{productId}")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Save(
        string purchaseOrderId, string productId, [FromBody] SaveItemRequest request,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.SaveDraftItemAsync(
            purchaseOrderId, productId, request.Quantity, actor, ct)).ToActionResult();
    }

    [HttpDelete("{productId}")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Delete(string purchaseOrderId, string productId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.DeleteDraftItemAsync(
            purchaseOrderId, productId, actor, ct)).ToActionResult();
    }

    public sealed record SaveItemRequest(decimal Quantity);
}
