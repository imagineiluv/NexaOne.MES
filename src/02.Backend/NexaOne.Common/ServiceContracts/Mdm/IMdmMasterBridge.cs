using NexaOne.Common;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — MDM 마스터(Plant/Area/Product/CodeClass/Code) 생성. 각 팩토리
/// 불변식 검증을 거치며 CreateCode는 CodeClass 존재를 검사한다(미존재→NotFound). 순수 조회는 게이트웨이가 커버.
/// plugin(MDM)이 구현하고 호스트가 GetBean→캐스트로 Default-ALC DI에 등록한다.</summary>
public interface IMdmMasterBridge : INexaModuleBridge
{
    Task<Result<PlantDto>> CreatePlantAsync(
        string plantId, string plantName, string country, string timeZone, CancellationToken ct = default);

    Task<Result<AreaDto>> CreateAreaAsync(
        string areaId, string areaName, string plantId, CancellationToken ct = default);

    Task<Result<ProductDto>> CreateProductAsync(
        string productId, string productName, string productType, string unit, CancellationToken ct = default);

    Task<Result<CodeClassDto>> CreateCodeClassAsync(
        string codeClassId, string codeClassName, CancellationToken ct = default);

    Task<Result<CodeDto>> CreateCodeAsync(
        string codeId, string codeClassId, string codeName, int sortOrder = 0, CancellationToken ct = default);
}
