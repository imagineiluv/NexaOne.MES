using NexaOne.Common;
using NexaOne.POM.Application.Lots;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.Pom;

public sealed class LotDispositionBridge : ILotDispositionBridge
{
    private readonly LotDispositionService _service;

    public LotDispositionBridge(LotDispositionService service) => _service = service;

    public async Task<Result<LotDispositionDto>> RecordAsync(
        RecordLotDispositionDto command,
        string actorId,
        CancellationToken ct = default)
    {
        var result = await _service.RecordAsync(new LotDispositionCommand(
            command.PlantId, command.LotId, command.WorkOrderId, command.ProcessId,
            command.DefectExecutionId, command.DefectCode, command.DispositionType,
            command.Quantity, command.ReasonCode, command.Reason, actorId,
            command.IdempotencyKey, command.ClientChannel, command.DeviceId,
            command.SourceExecutionId), ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<LotDispositionDto>(result.Error);
    }

    private static LotDispositionDto ToDto(LotDispositionRecord record) => new(
        record.DispositionId, record.PlantId, record.LotId, record.WorkOrderId,
        record.ProcessId, record.DefectExecutionId, record.DefectCode,
        record.DispositionType, record.Quantity, record.ReasonCode, record.Reason,
        record.DecidedBy, record.DecidedAt, record.SourceExecutionId,
        record.IdempotencyKey, record.ClientChannel, record.DeviceId);
}
