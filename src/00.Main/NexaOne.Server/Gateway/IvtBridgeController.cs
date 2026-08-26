using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/ivt/material-consumptions")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class IvtBridgeController : ControllerBase
{
    private readonly IMaterialBridge _bridge;

    public IvtBridgeController(IMaterialBridge bridge) => _bridge = bridge;

    [HttpPost]
    [RequirePermission(Permissions.IvtManage)]
    [ProducesResponseType<MaterialConsumptionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Consume(
        [FromBody] MaterialConsumptionCommand command,
        CancellationToken ct)
    {
        // HTTP 수동 실행은 요청 본문의 대리 사용자 값을 신뢰하지 않고 JWT 로그인 작업자로 고정한다.
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrEmpty(actor)) return Unauthorized();
        return (await _bridge.ConsumeAsync(command with { OperatorId = actor }, ct)).ToActionResult();
    }

    [HttpPost("{consumptionId}/reverse")]
    [RequirePermission(Permissions.IvtManage)]
    [ProducesResponseType<MaterialConsumptionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reverse(
        string consumptionId,
        [FromBody] ReverseMaterialConsumptionRequest request,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrEmpty(actor)) return Unauthorized();
        var command = new MaterialConsumptionReversalCommand(
            request.ReversalId, request.IdempotencyKey, consumptionId, request.Reason,
            request.OccurredAt, request.SourceSystem, actor, request.CorrelationId);
        return (await _bridge.ReverseAsync(command, ct)).ToActionResult();
    }
}

public sealed record ReverseMaterialConsumptionRequest(
    string ReversalId,
    string IdempotencyKey,
    string Reason,
    DateTime OccurredAt,
    string SourceSystem,
    string? CorrelationId = null);
