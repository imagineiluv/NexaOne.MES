namespace NexaOne.SYS.Application.Screens;

/// <summary>화면 정의 영속 레코드(Phase 4 후속). 정의 구조(JSON)는 프론트(NexaOne.Web)가 소유하고
/// 백엔드는 UiId/Title/Json과 별도 라우팅 메타(TargetChannel/EntryPath)를 저장한다.</summary>
public sealed record ScreenDefinitionRecord(
    string UiId,
    string Title,
    string DefinitionJson,
    string TargetChannel,
    string EntryPath)
{
    public ScreenDefinitionRecord(string uiId, string title, string definitionJson)
        : this(uiId, title, definitionJson, "MES", $"/meta/{uiId}") { }
}

/// <summary>화면 정의 저장소(Phase 4 후속). Low-Code 화면 디자이너 산출물을 영속화·조회한다.</summary>
public interface IScreenDefinitionStore
{
    Task<IReadOnlyList<ScreenDefinitionRecord>> GetAllAsync(CancellationToken ct = default);
    Task<ScreenDefinitionRecord?> GetAsync(string uiId, CancellationToken ct = default);
    Task UpsertAsync(ScreenDefinitionRecord record, CancellationToken ct = default);
}
