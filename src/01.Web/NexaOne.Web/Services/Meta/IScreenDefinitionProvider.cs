namespace NexaOne.Web.Services.Meta;

/// <summary>UiId → ScreenDefinition 해석기(Phase 3). 메타 라우팅(/meta/{uiId})이 화면 정의를 조회한다.</summary>
public interface IScreenDefinitionProvider
{
    void Register(ScreenDefinition definition);
    bool TryGet(string uiId, out ScreenDefinition? definition);
    ScreenDefinition? Get(string uiId);
}
