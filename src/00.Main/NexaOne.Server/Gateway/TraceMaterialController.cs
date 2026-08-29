using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/ivt/trace-material")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class TraceMaterialController : ControllerBase
{
    private readonly ITraceMaterialBridge _bridge;

    public TraceMaterialController(ITraceMaterialBridge bridge)
        => _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    [HttpPost("bindings/events")]
    [RequirePermission(Permissions.IvtManage)]
    [ProducesResponseType<TraceBindingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ExecuteBinding(
        [FromBody] TraceBindingCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.ExecuteBindingAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }

    [HttpPost("feed-sessions/events")]
    [RequirePermission(Permissions.IvtManage)]
    [ProducesResponseType<FeedSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ExecuteFeedSession(
        [FromBody] FeedSessionCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.ExecuteFeedSessionAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }
}
