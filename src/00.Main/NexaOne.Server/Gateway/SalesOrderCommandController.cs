using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sls;

namespace NexaOne.Server.Gateway;

/// <summary>수주 초안·단순 상태 전이의 JWT 실행자를 SLS 모듈에 위임합니다.</summary>
[ApiController]
[Authorize]
[Route("api/v1/sls/sales-orders")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SalesOrderCommandController(ISalesOrderCommandBridge bridge) : ControllerBase
{
    [HttpPost]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> SaveDraft([FromBody] SaveDraftRequest request, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.SaveDraftAsync(new SalesOrderDraftCommand(
            request.SalesOrderId, request.SalesOrderName, request.PlantId,
            request.CustomerId, request.ProductId, request.PlanStartDate,
            request.PlanEndDate, request.PlanQty, actor), ct)).ToActionResult();
    }

    [HttpDelete("{salesOrderId}")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> DeleteDraft(string salesOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.DeleteDraftAsync(salesOrderId, actor, ct)).ToActionResult();
    }

    [HttpPost("{salesOrderId}/confirm")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> Confirm(string salesOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.ConfirmAsync(salesOrderId, actor, ct)).ToActionResult();
    }

    [HttpPost("{salesOrderId}/close")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> Close(string salesOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.CloseAsync(salesOrderId, actor, ct)).ToActionResult();
    }

    public sealed record SaveDraftRequest(
        string? SalesOrderId, string? SalesOrderName, string? PlantId,
        string? CustomerId, string? ProductId, DateTime? PlanStartDate,
        DateTime? PlanEndDate, decimal PlanQty);
}
