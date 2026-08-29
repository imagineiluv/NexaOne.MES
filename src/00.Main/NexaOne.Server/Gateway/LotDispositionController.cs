using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/pom/lot-dispositions")]
[Authorize]
public sealed class LotDispositionController : ControllerBase
{
    private readonly ILotDispositionBridge _bridge;

    public LotDispositionController(ILotDispositionBridge bridge) => _bridge = bridge;

    [HttpPost]
    [RequirePermission(Permissions.PomManage)]
    [ProducesResponseType<LotDispositionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Record(
        [FromBody] RecordLotDispositionDto request,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.RecordAsync(request, actor, ct)).ToActionResult();
    }
}
