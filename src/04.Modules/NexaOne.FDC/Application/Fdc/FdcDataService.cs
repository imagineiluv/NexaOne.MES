using NexaOne.Common;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public sealed class FdcDataService
{
    private readonly IFdcParameterRepository _paramRepository;
    private readonly IFdcCollectDataRepository _dataRepository;

    public FdcDataService(IFdcParameterRepository paramRepository, IFdcCollectDataRepository dataRepository)
    {
        _paramRepository = paramRepository;
        _dataRepository  = dataRepository;
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FdcParameter>> GetParametersAsync(
        string equipmentId, CancellationToken ct = default)
        => await _paramRepository.GetByEquipmentAsync(equipmentId, ct);

    public async Task<Result<FdcParameter>> CreateParameterAsync(
        string parameterId,
        string parameterName,
        string equipmentId,
        string unit,
        decimal lowerLimit,
        decimal upperLimit,
        CancellationToken ct = default)
    {
        var result = FdcParameter.Create(parameterId, parameterName, equipmentId, unit, lowerLimit, upperLimit);
        if (result.IsFailure) return result;

        await _paramRepository.AddAsync(result.Value, ct);
        return result;
    }

    // ── Collect Data ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FdcCollectData>> GetCollectDataAsync(
        string parameterId, DateTime from, DateTime to, CancellationToken ct = default)
        => await _dataRepository.GetByParameterAsync(parameterId, from, to, ct);

    public async Task<IReadOnlyList<FdcCollectData>> GetLatestDataAsync(
        string parameterId, int limit = 50, CancellationToken ct = default)
        => await _dataRepository.GetLatestAsync(parameterId, limit, ct);

    public async Task<Result<FdcCollectData>> RecordDataAsync(
        string collectId,
        string equipmentId,
        string parameterId,
        decimal value,
        string quality,
        CancellationToken ct = default)
    {
        var param = await _paramRepository.GetByIdAsync(parameterId, ct);
        if (param is null)
            return Result.Failure<FdcCollectData>(Error.NotFound(nameof(FdcParameter), parameterId));

        var result = FdcCollectData.Create(
            collectId, equipmentId, parameterId, value, DateTime.UtcNow,
            quality, param.LowerLimit, param.UpperLimit);
        if (result.IsFailure) return result;

        await _dataRepository.AddAsync(result.Value, ct);
        return result;
    }
}
