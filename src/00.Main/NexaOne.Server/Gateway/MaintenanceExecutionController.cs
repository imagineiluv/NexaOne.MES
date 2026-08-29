using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/ems/maintenance-execution")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class MaintenanceExecutionController : ControllerBase
{
    private readonly IMaintenanceExecutionBridge _bridge;

    public MaintenanceExecutionController(IMaintenanceExecutionBridge bridge) => _bridge = bridge;

    [HttpPost("work-orders/{workOrderId}/checks")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> RecordCheck(
        string workOrderId,
        [FromBody] MaintenanceCheckRequest request,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var command = new MaintenanceCheckCommand(
            request.CheckResultId, workOrderId, request.ItemSequence, request.CheckName,
            request.RecordedAt, Context(actor, request.Command), request.ItemId,
            request.MeasuredValue, request.AttributeValue, request.Unit, request.IsPass,
            request.Finding);
        return (await _bridge.RecordCheckAsync(command, ct)).ToActionResult();
    }

    [HttpPost("work-orders/{workOrderId}/labor")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> StartLabor(
        string workOrderId,
        [FromBody] MaintenanceLaborStartRequest request,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var command = new MaintenanceLaborStartCommand(
            request.LaborId, workOrderId, request.LaborType, request.StartedAt,
            Context(actor, request.Command), request.WorkerId, request.Remark);
        return (await _bridge.StartLaborAsync(command, ct)).ToActionResult();
    }

    [HttpPost("labor/{laborId}/complete")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CompleteLabor(
        string laborId,
        [FromBody] MaintenanceLaborCompleteRequest request,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var command = new MaintenanceLaborCompleteCommand(
            laborId, request.ExpectedVersion, request.EndedAt,
            Context(actor, request.Command), request.Remark);
        return (await _bridge.CompleteLaborAsync(command, ct)).ToActionResult();
    }

    private static EmsCommandContextDto Context(string actor, MaintenanceCommandRequest request)
        => new(actor, request.IdempotencyKey, request.ClientChannel,
            request.DeviceId, request.CorrelationId);
}

public sealed record MaintenanceCommandRequest(
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null);

public sealed record MaintenanceCheckRequest(
    string CheckResultId,
    int ItemSequence,
    string CheckName,
    DateTime RecordedAt,
    MaintenanceCommandRequest Command,
    string? ItemId = null,
    decimal? MeasuredValue = null,
    string? AttributeValue = null,
    string? Unit = null,
    bool? IsPass = null,
    string? Finding = null);

public sealed record MaintenanceLaborStartRequest(
    string LaborId,
    string LaborType,
    DateTime StartedAt,
    MaintenanceCommandRequest Command,
    string? WorkerId = null,
    string? Remark = null);

public sealed record MaintenanceLaborCompleteRequest(
    int ExpectedVersion,
    DateTime EndedAt,
    MaintenanceCommandRequest Command,
    string? Remark = null);
