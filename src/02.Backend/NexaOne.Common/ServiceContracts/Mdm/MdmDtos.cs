namespace NexaOne.ServiceContracts.Mdm;

// 도메인 엔티티(Equipment/Plant/Area/Product/CodeClass/Code)를 직렬화 계약으로 노출하지 않는 경량 DTO
// (ALC/버전 결합 차단). 각 서비스 반환/도메인 컬럼과 1:1. ValidState 등은 원시 string으로 평탄화한다.
public record EquipmentDto(
    string EquipmentId, string EquipmentName, string Description, string PlantId, string AreaId,
    string EquipmentType, string? ParentEquipmentId, string Vendor, string Model,
    string EquipmentClassId, string ValidState);

public record PlantDto(
    string PlantId, string PlantName, string Description, string Country, string TimeZone);

public record AreaDto(
    string AreaId, string AreaName, string Description, string PlantId);

public record ProductDto(
    string ProductId, string ProductName, string Description, string ProductType, string Unit, string ValidState);

public record CodeClassDto(
    string CodeClassId, string CodeClassName, string Description);

public record CodeDto(
    string CodeId, string CodeClassId, string CodeName, int SortOrder, string ValidState);
