namespace NexaOne.ServiceContracts.Rms;

// 도메인 엔티티 비노출 경량 DTO. ApprovalState는 enum 비노출 위해 string(enum 이름)으로 표현.
public record RecipeDto(
    string RecipeId, string RecipeName, string Description, string EquipmentClassId,
    int Version, string ApprovalState, string? FirstApproverId, string? SecondApproverId, DateTime? ReleasedAt);

public record RecipeParamDto(
    string ParamId, string RecipeId, string ParamName, string ParamValue, string Unit, int SortOrder,
    int Version);

/// <summary>승인 상태 전이의 감사 주체와 HTTP 멱등 키를 함께 전달한다.</summary>
public sealed record RecipeCommandContext(string ActorId, string IdempotencyKey);

/// <summary>레시피 생성에 필요한 업무 값과 비-부인 명령 문맥을 하나의 경계로 묶는다.</summary>
public sealed record RecipeCreateCommand(
    string RecipeId,
    string Name,
    string Description,
    string EquipmentClassId,
    string IdempotencyKey,
    string ActorId);

/// <summary>Released 레시피로부터 새 Draft 버전을 만드는 멱등 명령.</summary>
public sealed record RecipeVersionCreateCommand(
    string SourceRecipeId,
    string NewRecipeId,
    string IdempotencyKey,
    string ActorId);

/// <summary>Draft 레시피에 파라미터를 추가하는 멱등 명령.</summary>
public sealed record RecipeParamAddCommand(
    string ParamId,
    string RecipeId,
    string ParamName,
    string ParamValue,
    string Unit,
    int SortOrder,
    string IdempotencyKey,
    string ActorId);

/// <summary>레시피 파라미터 값 변경의 낙관적 버전과 멱등 경계를 묶는다.</summary>
public sealed record RecipeParamUpdateCommand(
    string ParamId,
    string NewValue,
    int ExpectedVersion,
    string IdempotencyKey,
    string ActorId);

/// <summary>Draft 파라미터 삭제의 낙관적 버전과 멱등 경계를 묶는다.</summary>
public sealed record RecipeParamDeleteCommand(
    string ParamId,
    int ExpectedVersion,
    string IdempotencyKey,
    string ActorId);
