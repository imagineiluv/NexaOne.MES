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
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
public sealed class SalesOrderCommandController(
    ISalesOrderCommandBridge bridge, ILogger<SalesOrderCommandController> logger) : ControllerBase
{
    [HttpPost]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> SaveDraft([FromBody] SaveDraftRequest request, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return await SlsWriteRecovery.Execute(this, logger, () => bridge.SaveDraftAsync(new SalesOrderDraftCommand(
            request.SalesOrderId, request.SalesOrderName, request.PlantId,
            request.CustomerId, request.ProductId, request.PlanStartDate,
            request.PlanEndDate, request.PlanQty, actor), ct));
    }

    [HttpDelete("{salesOrderId}")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> DeleteDraft(string salesOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return await SlsWriteRecovery.Execute(this, logger, () =>
            bridge.DeleteDraftAsync(salesOrderId, actor, ct));
    }

    [HttpPost("{salesOrderId}/confirm")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> Confirm(string salesOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return await SlsWriteRecovery.Execute(this, logger, () =>
            bridge.ConfirmAsync(salesOrderId, actor, ct));
    }

    [HttpPost("{salesOrderId}/close")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> Close(string salesOrderId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return await SlsWriteRecovery.Execute(this, logger, () =>
            bridge.CloseAsync(salesOrderId, actor, ct));
    }

    public sealed record SaveDraftRequest(
        string? SalesOrderId, string? SalesOrderName, string? PlantId,
        string? CustomerId, string? ProductId, DateTime? PlanStartDate,
        DateTime? PlanEndDate, decimal PlanQty);
}
