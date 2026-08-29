using NexaOne.Common;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

public sealed class MaterialLotBridge : IMaterialLotBridge
{
    private readonly MaterialLotService _service;

    public MaterialLotBridge(MaterialLotService service) => _service = service;

    public Task<Result<MaterialLotEventDto>> ExecuteAsync(
        MaterialLotCommand command,
        CancellationToken ct = default) => _service.ExecuteAsync(command, ct);
}
