using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/ivt/material-lots/splits")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class MaterialLotSplitController(IMaterialLotSplitBridge bridge) : ControllerBase
{
    [HttpGet("/api/v1/ivt/material-lots/{childLotId}/origin")]
    [RequirePermission(Permissions.IvtRead)]
    [ProducesResponseType<MaterialLotSplitOriginDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrigin(string childLotId, CancellationToken ct) =>
        (await bridge.GetOriginAsync(childLotId, ct)).ToActionResult();

    [HttpGet("/api/v1/ivt/material-lots/{parentLotId}/children")]
    [RequirePermission(Permissions.IvtRead)]
    [ProducesResponseType<MaterialLotSplitChildrenPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetChildren(
        string parentLotId, [FromQuery] string? afterSplitId = null,
        [FromQuery] int limit = 50, CancellationToken ct = default) =>
        (await bridge.GetChildrenAsync(parentLotId, afterSplitId, limit, ct)).ToActionResult();

    [HttpPost]
    [RequirePermission(Permissions.IvtManage)]
    [ProducesResponseType<MaterialLotSplitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Split(
        [FromBody] MaterialLotSplitCommand command, CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await bridge.SplitAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }
}
