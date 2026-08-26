using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.MaintenanceExecution;

public sealed class MaintenanceExecutionBridge : IMaintenanceExecutionBridge
{
    private readonly MaintenanceExecutionService _service;

    public MaintenanceExecutionBridge(MaintenanceExecutionService service) => _service = service;

    public async Task<Result<MaintenanceCheckDto>> RecordCheckAsync(
        MaintenanceCheckCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.RecordCheckAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<MaintenanceCheckDto>(result.Error);
    }

    public async Task<Result<MaintenanceLaborDto>> StartLaborAsync(
        MaintenanceLaborStartCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.StartLaborAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<MaintenanceLaborDto>(result.Error);
    }

    public async Task<Result<MaintenanceLaborDto>> CompleteLaborAsync(
        MaintenanceLaborCompleteCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.CompleteLaborAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<MaintenanceLaborDto>(result.Error);
    }

    private static MaintenanceCheckDto ToDto(MaintenanceCheckRecord value) => new(
        value.CheckResultId, value.WorkOrderId, value.ItemId, value.ItemSequence,
        value.CheckName, value.MeasuredValue, value.AttributeValue, value.Unit,
        value.IsPass, value.Finding, value.RecordedBy, value.RecordedAt,
        value.CorrelationId);

    private static MaintenanceLaborDto ToDto(MaintenanceLaborRecord value) => new(
        value.LaborId, value.WorkOrderId, value.UserId, value.WorkerId, value.LaborType,
        value.StartedAt, value.EndedAt, value.EndedBy, value.LaborHours, value.Remark,
        value.CorrelationId, value.Version);
}
