using NexaOne.Common;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Application.Est;

public sealed class EquipmentOutputBridge : IEquipmentOutputBridge
{
    private readonly EquipmentOutputService _service;

    public EquipmentOutputBridge(EquipmentOutputService service) => _service = service;

    public async Task<Result<EquipmentOutputDto>> RecordAsync(
        EquipmentOutputCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.RecordAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<EquipmentOutputDto>(result.Error);
    }

    private static EquipmentOutputDto ToDto(EquipmentOutputRecord r) => new(
        r.OutputEventId,
        r.IdempotencyKey,
        r.PlantId,
        r.EquipmentId,
        r.OutputType,
        r.TotalQuantity,
        r.GoodQuantity,
        r.DefectQuantity,
        r.Unit,
        r.OccurredAt,
        r.Source,
        r.ActorId,
        r.CarrierId,
        r.ProcessLotId,
        r.WorkOrderId,
        r.CorrelationId,
        r.IsLotOutput,
        r.WorkScopeId);
}
