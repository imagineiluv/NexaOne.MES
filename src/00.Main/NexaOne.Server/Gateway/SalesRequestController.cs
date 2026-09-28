using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sls;

namespace NexaOne.Server.Gateway;

/// <summary>판매요청 쓰기는 SLS 모듈에 위임합니다. 기존 명명 조회/수주 화면 명령은 유지합니다.</summary>
[ApiController]
[Authorize]
[Route("api/v1/sls/sales-requests")]
[ProducesErrorResponseType(typeof(Error))]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
public sealed class SalesRequestController(
    ISalesRequestBridge bridge, ILogger<SalesRequestController> logger) : ControllerBase
{
    [HttpPost]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> Create([FromBody] CreateRequest request, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return await SlsWriteRecovery.Execute(this, logger, () => bridge.CreateDraftAsync(new SalesRequestDraftCommand(
            request.SalesRequestId, request.SalesRequestName, request.CustomerId,
            request.ProductId, request.RequestDate, request.RequestQty, actor), ct));
    }

    [HttpPost("{salesRequestId}/receipt")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> Receive(
        string salesRequestId, [FromBody] ReceiveRequest request, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return await SlsWriteRecovery.Execute(this, logger, () => bridge.ReceiveAsync(new SalesRequestReceiptCommand(
            salesRequestId, request.SalesOrderId, request.PlantId, request.SalesOrderName,
            request.PlanStartDate, request.PlanEndDate, actor), ct));
    }

    [HttpPost("{salesRequestId}/withdraw")]
    [RequirePermission(Permissions.SlsManage)]
    public async Task<IActionResult> Withdraw(string salesRequestId, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return await SlsWriteRecovery.Execute(this, logger, () =>
            bridge.WithdrawAsync(new SalesRequestWithdrawCommand(salesRequestId, actor), ct));
    }

    public sealed record CreateRequest(
        string? SalesRequestId, string? SalesRequestName, string? CustomerId,
        string? ProductId, DateTime RequestDate, decimal RequestQty);

    public sealed record ReceiveRequest(
        string? SalesOrderId, string? PlantId, string? SalesOrderName,
        DateTime PlanStartDate, DateTime PlanEndDate);
}
