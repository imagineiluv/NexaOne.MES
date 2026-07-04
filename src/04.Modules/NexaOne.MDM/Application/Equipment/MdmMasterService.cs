using NexaOne.Common;
using NexaOne.Common.Caching;
using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Application.Equipments;

public sealed class MdmMasterService
{
    private readonly IPlantRepository   _plantRepo;
    private readonly IAreaRepository    _areaRepo;
    private readonly IProductRepository _productRepo;
    private readonly ICodeRepository    _codeRepo;
    private readonly ICacheService      _cache;

    public MdmMasterService(
        IPlantRepository plantRepo,
        IAreaRepository areaRepo,
        IProductRepository productRepo,
        ICodeRepository codeRepo,
        ICacheService cache)
    {
        _plantRepo   = plantRepo;
        _areaRepo    = areaRepo;
        _productRepo = productRepo;
        _codeRepo    = codeRepo;
        _cache       = cache;
    }

    private static string CodesCacheKey(string codeClassId) => $"mdm:codes:{codeClassId}";

    // ── Plant ─────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Plant>> GetPlantsAsync(CancellationToken ct = default)
        => _plantRepo.GetAllAsync(ct);

    public async Task<Result<Plant>> CreatePlantAsync(
        string plantId, string plantName, string country, string timeZone,
        CancellationToken ct = default)
    {
        var result = Plant.Create(plantId, plantName, country, timeZone);
        if (result.IsFailure) return result;
        await _plantRepo.AddAsync(result.Value, ct);
        return result;
    }

    // ── Area ──────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Area>> GetAreasByPlantAsync(string plantId, CancellationToken ct = default)
        => _areaRepo.GetByPlantAsync(plantId, ct);

    public async Task<Result<Area>> CreateAreaAsync(
        string areaId, string areaName, string plantId,
        CancellationToken ct = default)
    {
        var result = Area.Create(areaId, areaName, plantId);
        if (result.IsFailure) return result;
        await _areaRepo.AddAsync(result.Value, ct);
        return result;
    }

    // ── Product ───────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken ct = default)
        => _productRepo.GetAllAsync(ct);

    public async Task<Result<Product>> CreateProductAsync(
        string productId, string productName, string productType, string unit,
        CancellationToken ct = default)
    {
        var result = Product.Create(productId, productName, productType, unit);
        if (result.IsFailure) return result;
        await _productRepo.AddAsync(result.Value, ct);
        return result;
    }

    // ── Code / CodeClass ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<CodeClass>> GetCodeClassesAsync(CancellationToken ct = default)
        => _codeRepo.GetAllClassesAsync(ct);

    public async Task<Result<CodeClass>> CreateCodeClassAsync(
        string codeClassId, string codeClassName,
        CancellationToken ct = default)
    {
        var result = CodeClass.Create(codeClassId, codeClassName);
        if (result.IsFailure) return result;
        await _codeRepo.AddClassAsync(result.Value, ct);
        return result;
    }

    // 코드 조회는 드롭다운/룩업으로 읽기 빈도가 높고 갱신이 드물어 클래스별로 캐시한다(쓰기 시 무효화).
    public Task<IReadOnlyList<Code>> GetCodesByClassAsync(string codeClassId, CancellationToken ct = default)
        => _cache.GetOrCreateAsync(CodesCacheKey(codeClassId),
            () => _codeRepo.GetByClassAsync(codeClassId, ct), ct: ct);

    public async Task<Result<Code>> CreateCodeAsync(
        string codeId, string codeClassId, string codeName, int sortOrder = 0,
        CancellationToken ct = default)
    {
        var classExists = await _codeRepo.GetClassByIdAsync(codeClassId, ct);
        if (classExists is null)
            return Result.Failure<Code>(Error.NotFoundOf(nameof(CodeClass), codeClassId));

        var result = Code.Create(codeId, codeClassId, codeName, sortOrder);
        if (result.IsFailure) return result;
        await _codeRepo.AddCodeAsync(result.Value, ct);
        // 해당 클래스의 코드 목록 캐시를 무효화해 다음 조회가 최신 결과를 읽도록 한다.
        await _cache.RemoveAsync(CodesCacheKey(codeClassId), ct);
        return result;
    }
}
