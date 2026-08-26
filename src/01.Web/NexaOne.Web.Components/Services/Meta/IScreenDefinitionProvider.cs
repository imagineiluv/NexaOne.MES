namespace NexaOne.Web.Services.Meta;

/// <summary>UiId → ScreenDefinition 해석기(Phase 3). 메타 라우팅(/meta/{uiId})이 화면 정의를 조회한다.</summary>
public interface IScreenDefinitionProvider
{
    void Register(ScreenDefinition definition);
    bool TryGet(string uiId, out ScreenDefinition? definition);
    ScreenDefinition? Get(string uiId);

    /// <summary>비동기 조회(DB-backed 구현용). 인메모리는 동기 결과를 래핑한다.</summary>
    Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default);

    /// <summary>
    /// 현재 공급자가 해석할 수 있는 화면 ID 집합을 한 번에 조회한다.
    /// 메뉴처럼 다수 화면의 존재 여부만 필요한 소비자가 화면마다 DB를 왕복하지 않도록 제공한다.
    /// </summary>
    Task<IReadOnlySet<string>> GetKnownUiIdsAsync(CancellationToken ct = default);
}
