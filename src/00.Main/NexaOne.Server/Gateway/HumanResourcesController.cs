using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Hr;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/hr/{tenantId:guid}/{organizationId:guid}")]
public sealed class HumanResourcesController(IHumanResourcesBridge bridge,
    ILogger<HumanResourcesController> logger) : ControllerBase
{
    [HttpPost("tasks")]
    public Task<IActionResult> CreateTask(Guid tenantId, Guid organizationId,
        [FromBody] HrTaskInput input, CancellationToken ct)
        => Execute(user => bridge.CreateTaskAsync(user, tenantId, organizationId, input, ct));

    [HttpPost("time/start")]
    public Task<IActionResult> Start(Guid tenantId, Guid organizationId,
        [FromBody] StartTimer command, CancellationToken ct)
        => Execute(user => bridge.StartTimerAsync(user, tenantId, organizationId,
            command.ProjectId, command.TaskId, command.Description, ct));

    [HttpPost("time/{entryId:guid}/stop")]
    public Task<IActionResult> Stop(Guid tenantId, Guid organizationId, Guid entryId,
        [FromBody] Versioned command, CancellationToken ct)
        => Execute(user => bridge.StopTimerAsync(user, tenantId, organizationId,
            entryId, command.Version, ct));

    [HttpPost("time")]
    public Task<IActionResult> Record(Guid tenantId, Guid organizationId,
        [FromBody] ManualTimeInput input, CancellationToken ct)
        => Execute(user => bridge.RecordTimeAsync(user, tenantId, organizationId, input, ct));

    [HttpPut("time/{entryId:guid}")]
    public Task<IActionResult> Correct(Guid tenantId, Guid organizationId, Guid entryId,
        [FromBody] CorrectTime command, CancellationToken ct)
        => Execute(user => bridge.CorrectTimeAsync(user, tenantId, organizationId, entryId,
            command.Version, command.Start, command.End, command.Reason, ct));

    [HttpGet("time/{entryId:guid}")]
    public Task<IActionResult> GetTime(Guid tenantId, Guid organizationId, Guid entryId,
        CancellationToken ct)
        => Execute(user => bridge.GetTimeEntryAsync(user, tenantId, organizationId, entryId, ct));

    [HttpPost("timesheets")]
    public Task<IActionResult> Submit(Guid tenantId, Guid organizationId,
        [FromBody] SubmitSheet command, CancellationToken ct)
        => Execute(user => bridge.SubmitTimesheetAsync(user, tenantId, organizationId,
            command.Start, command.End, ct));

    [HttpPost("timesheets/{timesheetId:guid}/review")]
    public Task<IActionResult> Review(Guid tenantId, Guid organizationId, Guid timesheetId,
        [FromBody] ReviewSheet command, CancellationToken ct)
        => Execute(user => bridge.ReviewTimesheetAsync(user, tenantId, organizationId,
            timesheetId, command.Version, command.Approve, command.Reason, ct));

    [HttpGet("timesheets/{timesheetId:guid}")]
    public Task<IActionResult> GetSheet(Guid tenantId, Guid organizationId, Guid timesheetId,
        CancellationToken ct)
        => Execute(user => bridge.GetTimesheetAsync(user, tenantId, organizationId, timesheetId, ct));

    private async Task<IActionResult> Execute<T>(Func<string, Task<T>> action)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try { return Ok(await action(userId)); }
        catch (BusinessException error)
        {
            if (error.Code == "BUSINESS_ACCESS_DENIED") return Forbid();
            var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404
                : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) ? 400 : 409;
            return StatusCode(status, new { code = error.Code });
        }
        catch (DBConcurrencyException) { return Conflict(new { code = "BUSINESS_VERSION_CONFLICT" }); }
        catch (Exception error) when (error is DbException)
        {
            logger.LogError(error, "HR persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "HR storage is unavailable.");
        }
    }

    public sealed record StartTimer(Guid? ProjectId = null, Guid? TaskId = null,
        string Description = "");
    public sealed record Versioned(Guid Version);
    public sealed record SubmitSheet(DateTimeOffset Start, DateTimeOffset End);
    public sealed record CorrectTime(Guid Version, DateTimeOffset Start, DateTimeOffset End,
        string Reason);
    public sealed record ReviewSheet(Guid Version, bool Approve, string Reason);
}
