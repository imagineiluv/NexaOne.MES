using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/est/utilities")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class UtilityController : ControllerBase
{
    private readonly IUtilityBridge _bridge;
    public UtilityController(IUtilityBridge bridge) => _bridge = bridge;

    [HttpPost("meters")]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> SaveMeter([FromBody] UtilityMeterCommand command, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.SaveMeterAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }

    [HttpPost("readings")]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> RecordReading([FromBody] UtilityReadingCommand command, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.RecordReadingAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }

    [HttpPost("summaries")]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> Summarize([FromBody] UtilitySummaryCommand command, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.SummarizeAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }
}
