using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/ems/maintenance-schedules")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class MaintenanceScheduleController : ControllerBase
{
    private readonly IMaintenanceScheduleBridge _bridge;

    public MaintenanceScheduleController(IMaintenanceScheduleBridge bridge) => _bridge = bridge;

    [HttpPost]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> Create(
        [FromBody] MaintenanceScheduleCreateCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.CreateAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }

    [HttpPut("{scheduleId}")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> Update(
        string scheduleId,
        [FromBody] MaintenanceScheduleUpdateCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.UpdateAsync(
            command with { ScheduleId = scheduleId, ActorId = actor }, ct)).ToActionResult();
    }

    [HttpPost("{scheduleId}/acknowledgements")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> Acknowledge(
        string scheduleId,
        [FromBody] MaintenanceScheduleAcknowledgeCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.AcknowledgeAsync(
            command with { ScheduleId = scheduleId, ActorId = actor }, ct)).ToActionResult();
    }
}
