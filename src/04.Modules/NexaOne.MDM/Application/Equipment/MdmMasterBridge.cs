using NexaOne.Common;
using NexaOne.MDM.Domain;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Application.Equipments;

/// <summary>ADR-008 얇은 브리지 어댑터 — MdmMasterService에 위임하고 Plant/Area/Product/CodeClass/Code를
/// 계약 DTO로 매핑. plugin ALC에서 생성되며 호스트(Default ALC)가 IMdmMasterBridge로 캐스트해 DI에 등록한다.
/// 팩토리 불변식 검증이 있는 생성만 노출한다(CreateCode는 CodeClass 존재 검사 — 미존재→NotFound).</summary>
public sealed class MdmMasterBridge : IMdmMasterBridge
{
    private readonly MdmMasterService _service;
    public MdmMasterBridge(MdmMasterService service) => _service = service;

    public async Task<Result<PlantDto>> CreatePlantAsync(
        string plantId, string plantName, string country, string timeZone, CancellationToken ct = default)
    {
        var r = await _service.CreatePlantAsync(plantId, plantName, country, timeZone, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<PlantDto>(r.Error);
    }

    public async Task<Result<AreaDto>> CreateAreaAsync(
        string areaId, string areaName, string plantId, CancellationToken ct = default)
    {
        var r = await _service.CreateAreaAsync(areaId, areaName, plantId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<AreaDto>(r.Error);
    }

    public async Task<Result<ProductDto>> CreateProductAsync(
        string productId, string productName, string productType, string unit, CancellationToken ct = default)
    {
        var r = await _service.CreateProductAsync(productId, productName, productType, unit, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ProductDto>(r.Error);
    }

    public async Task<Result<CodeClassDto>> CreateCodeClassAsync(
        string codeClassId, string codeClassName, CancellationToken ct = default)
    {
        var r = await _service.CreateCodeClassAsync(codeClassId, codeClassName, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<CodeClassDto>(r.Error);
    }

    public async Task<Result<CodeDto>> CreateCodeAsync(
        string codeId, string codeClassId, string codeName, int sortOrder = 0, CancellationToken ct = default)
    {
        var r = await _service.CreateCodeAsync(codeId, codeClassId, codeName, sortOrder, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<CodeDto>(r.Error);
    }

    private static PlantDto ToDto(Plant p) => new(p.Id, p.PlantName, p.Description, p.Country, p.TimeZone);
    private static AreaDto ToDto(Area a) => new(a.Id, a.AreaName, a.Description, a.PlantId);
    private static ProductDto ToDto(Product p) => new(p.Id, p.ProductName, p.Description, p.ProductType, p.Unit, p.ValidState);
    private static CodeClassDto ToDto(CodeClass c) => new(c.Id, c.CodeClassName, c.Description);
    private static CodeDto ToDto(Code c) => new(c.Id, c.CodeClassId, c.CodeName, c.SortOrder, c.ValidState);
}
