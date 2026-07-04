using NexaOne.Common;
using NexaOne.Common.Caching;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public sealed class FdcDataService
{
    private readonly IFdcParameterRepository _paramRepository;
    private readonly IFdcCollectDataRepository _dataRepository;
    private readonly ICacheService? _cache;

    // 수집 핫패스(RecordDataAsync)는 태그 변경마다 파라미터 한도(LowerLimit/UpperLimit)를 읽는다.
    // 파라미터 마스터는 갱신이 드물어 parameterId별로 캐시한다(쓰기 경로에서 무효화). cache 미주입 시 직접 조회.
    public FdcDataService(
        IFdcParameterRepository paramRepository,
        IFdcCollectDataRepository dataRepository,
        ICacheService? cache = null)
    {
        _paramRepository = paramRepository;
        _dataRepository  = dataRepository;
        _cache           = cache;
    }

    private static string ParamCacheKey(string parameterId) => $"fdc:param:{parameterId}";

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

    /// <summary>파라미터를 그룹(FDC_PARAMETER_GROUP)에 배정한다(groupId=null이면 해제). GROUP_ID 쓰기 경로.</summary>
    public async Task<Result> AssignParameterToGroupAsync(
        string parameterId, string? groupId, CancellationToken ct = default)
    {
        // 변형 경로는 캐시를 거치지 않고 직접 조회한다(캐시된 공유 참조를 변형하지 않기 위해).
        var param = await _paramRepository.GetByIdAsync(parameterId, ct);
        if (param is null)
            return Result.Failure(Error.NotFound(nameof(FdcParameter), $"FdcParameter '{parameterId}'을(를) 찾을 수 없습니다."));

        param.AssignToGroup(groupId);
        await _paramRepository.UpdateAsync(param, ct);
        // 갱신된 파라미터가 핫패스 캐시에 남지 않도록 무효화한다(다음 조회가 최신을 읽음).
        if (_cache is not null) await _cache.RemoveAsync(ParamCacheKey(parameterId), ct);
        return Result.Success();
    }

    // ── Collect Data ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FdcCollectData>> GetCollectDataAsync(
        string parameterId, DateTime from, DateTime to, int limit, CancellationToken ct = default)
        => await _dataRepository.GetByParameterAsync(parameterId, from, to, limit, ct);

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
        // 핫패스: 파라미터 한도를 캐시로 조회한다(읽기 전용 사용 — 캐시된 참조를 변형하지 않는다).
        var param = _cache is null
            ? await _paramRepository.GetByIdAsync(parameterId, ct)
            : await _cache.GetOrCreateAsync(ParamCacheKey(parameterId),
                () => _paramRepository.GetByIdAsync(parameterId, ct), ct: ct);
        if (param is null)
            return Result.Failure<FdcCollectData>(Error.NotFound(nameof(FdcParameter), $"FdcParameter '{parameterId}'을(를) 찾을 수 없습니다."));

        var result = FdcCollectData.Create(
            collectId, equipmentId, parameterId, value, DateTime.UtcNow,
            quality, param.LowerLimit, param.UpperLimit);
        if (result.IsFailure) return result;

        await _dataRepository.AddAsync(result.Value, ct);
        return result;
    }
}
