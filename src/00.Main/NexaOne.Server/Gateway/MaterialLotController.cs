using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/ivt/material-lots/events")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class MaterialLotController : ControllerBase
{
    private readonly IMaterialLotBridge _bridge;

    public MaterialLotController(IMaterialLotBridge bridge) => _bridge = bridge;

    [HttpPost]
    [RequirePermission(Permissions.IvtManage)]
    [ProducesResponseType<MaterialLotEventDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Execute(
        [FromBody] MaterialLotCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.ExecuteAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }
}
