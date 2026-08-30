using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Server.Security;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 설비 client의 local-first WorkScope snapshot을 durable POM inbox에만 접수합니다.
/// 이 endpoint는 작업 상태 전이 또는 수량 추정을 수행하지 않습니다.
/// </summary>
[ApiController]
[Route("api/v1/pom/work-scope-projections")]
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesErrorResponseType(typeof(Error))]
public sealed class WorkScopeProjectionController : ControllerBase
{
    private readonly IWorkScopeProjectionBridge _bridge;
    private readonly IEquipmentClientAuthenticator _clientAuthenticator;

    public WorkScopeProjectionController(
        IWorkScopeProjectionBridge bridge,
        IEquipmentClientAuthenticator clientAuthenticator)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _clientAuthenticator = clientAuthenticator ?? throw new ArgumentNullException(nameof(clientAuthenticator));
    }

    [HttpPost]
    [ProducesResponseType<WorkScopeProjectionReceiptDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<WorkScopeProjectionReceiptDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status426UpgradeRequired)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ingest(
        [FromBody] WorkScopeProjectionCommand command,
        CancellationToken ct)
    {
        if (command is null)
            return BadRequest(Error.Validation(nameof(command), "Projection command is required."));

        var authentication = _clientAuthenticator.Authenticate(
            Request,
            command.ClientId,
            command.EquipmentId);
        if (authentication.Rejection is { } rejection)
            return rejection.ToActionResult();

        var sourceClientId = authentication.Identity!.ClientId;
        var result = await _bridge.IngestAsync(sourceClientId, command, ct);
        return result.ToActionResult(receipt => receipt.Replay
            ? Ok(receipt)
            : StatusCode(StatusCodes.Status202Accepted, receipt));
    }
}
