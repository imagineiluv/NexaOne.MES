namespace NexaOne.ServiceContracts.Rms;

// 도메인 엔티티 비노출 경량 DTO. ApprovalState는 enum 비노출 위해 string(enum 이름)으로 표현.
public record RecipeDto(
    string RecipeId, string RecipeName, string Description, string EquipmentClassId,
    int Version, string ApprovalState, string? FirstApproverId, string? SecondApproverId, DateTime? ReleasedAt);

public record RecipeParamDto(
    string ParamId, string RecipeId, string ParamName, string ParamValue, string Unit, int SortOrder);
