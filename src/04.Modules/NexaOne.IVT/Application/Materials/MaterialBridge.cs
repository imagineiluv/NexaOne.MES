using NexaOne.Common;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

public sealed class MaterialBridge : IMaterialBridge
{
    private readonly ConsumptionService _service;

    public MaterialBridge(ConsumptionService service) => _service = service;

    public Task<Result<MaterialConsumptionDto>> ConsumeAsync(
        MaterialConsumptionCommand command,
        CancellationToken ct = default)
        => _service.ConsumeAsync(command, ct);

    public Task<Result<MaterialConsumptionDto>> ReverseAsync(
        MaterialConsumptionReversalCommand command,
        CancellationToken ct = default)
        => _service.ReverseAsync(command, ct);
}
