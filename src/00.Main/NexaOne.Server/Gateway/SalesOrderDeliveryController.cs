using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sls;

namespace NexaOne.Server.Gateway;

/// <summary>수주에서 출하주문·품목으로의 원자적 인계를 SLS 모듈에 위임합니다.</summary>
[ApiController]
[Authorize]
[Route("api/v1/sls/sales-orders")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SalesOrderDeliveryController(ISalesOrderDeliveryBridge bridge) : ControllerBase
{
    [HttpPost("{salesOrderId}/delivery-request")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> RequestDelivery(
        string salesOrderId, [FromBody] DeliveryRequest request, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var result = await bridge.RequestDeliveryAsync(new SalesOrderDeliveryCommand(
            salesOrderId, request.DeliveryOrderId, request.DeliveryItemId, actor), ct);
        return result.ToActionResult();
    }

    [HttpPost("{salesOrderId}/delivery-confirmation")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> ConfirmDelivery(string salesOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var result = await bridge.ConfirmDeliveryAsync(
            new SalesOrderDeliveryConfirmationCommand(salesOrderId, actor), ct);
        return result.ToActionResult();
    }

    public sealed record DeliveryRequest(string? DeliveryOrderId, string? DeliveryItemId);
}
