using System.Data.Common;
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
    private readonly ILogger<LotDispositionController> _logger;

    public LotDispositionController(ILotDispositionBridge bridge, ILogger<LotDispositionController> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    [HttpPost]
    [RequirePermission(Permissions.PomManage)]
    [ProducesResponseType<LotDispositionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Record(
        [FromBody] RecordLotDispositionDto request,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        try { return (await _bridge.RecordAsync(request, actor, ct)).ToActionResult(); }
        catch (Exception error) when (error is DbException or IOException
            || error is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException or IOException))
        {
            _logger.LogError(error, "LOT disposition persistence failed; write outcome may be unknown.");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "LOT disposition storage is unavailable.",
                detail: "A write outcome may be unknown. Once storage is available, retry with the same idempotency key and original payload. "
                    + "Do not use a new key until the result is known.");
        }
    }
}
