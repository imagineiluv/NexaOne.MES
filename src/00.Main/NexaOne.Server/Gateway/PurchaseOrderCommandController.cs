using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.Server.Gateway;

/// <summary>JWT-authenticated purchase-order Draft writes and status transitions owned by PRC.</summary>
[ApiController]
[Authorize]
[Route("api/v1/prc/purchase-orders")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class PurchaseOrderCommandController(IPurchaseOrderCommandBridge bridge) : ControllerBase
{
    [HttpPost]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> SaveDraft([FromBody] SaveDraftRequest request, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.SaveDraftAsync(new PurchaseOrderDraftCommand(
            request.PurchaseOrderId, request.PlantId, request.PurchaseOrderName,
            request.VendorId, request.OrderQuantity, actor), ct)).ToActionResult();
    }

    [HttpDelete("{purchaseOrderId}")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> DeleteDraft(string purchaseOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.DeleteDraftAsync(purchaseOrderId, actor, ct)).ToActionResult();
    }

    [HttpPost("{purchaseOrderId}/order")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Order(string purchaseOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.OrderAsync(purchaseOrderId, actor, ct)).ToActionResult();
    }

    [HttpPost("{purchaseOrderId}/close")]
    [RequirePermission(Permissions.PrcManage)]
    public async Task<IActionResult> Close(string purchaseOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.CloseAsync(purchaseOrderId, actor, ct)).ToActionResult();
    }

    public sealed record SaveDraftRequest(
        string? PurchaseOrderId, string? PlantId, string? PurchaseOrderName,
        string? VendorId, decimal OrderQuantity);
}
