using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>DB-backed 화면정의 제공자(Phase 5a) — SYS_SCREEN_DEFINITION을 게이트웨이(IRuleDispatcher+명명쿼리)로
/// 읽어 /meta에 제공한다. DB에 없으면 InMemory 시드(DEMO_*) 폴백. 디자이너 SAVE는 command 게이트웨이
/// (SYS.UpsertScreenDefinition)로 쓰고, 다음 로드 시 이 provider가 DB에서 읽는다.</summary>
public sealed class GatewayScreenDefinitionProvider : IScreenDefinitionProvider
{
    private readonly InMemoryScreenDefinitionProvider _seed = new();   // 시드(DEMO_*) + Register 캐시
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _queries;

    public GatewayScreenDefinitionProvider(IRuleDispatcher dispatcher, IQueryRegistry queries)
    {
        _dispatcher = dispatcher;
        _queries = queries;
    }

    public void Register(ScreenDefinition definition) => _seed.Register(definition);

    public bool TryGet(string uiId, out ScreenDefinition? definition)
    {
        definition = GetAsync(uiId).GetAwaiter().GetResult();
        return definition is not null;
    }

    public ScreenDefinition? Get(string uiId) => GetAsync(uiId).GetAwaiter().GetResult();

    public async Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default)
    {
        var fromDb = await LoadFromDbAsync(uiId, ct);   // DB 우선(사용자 편집 정의)
        return fromDb ?? _seed.Get(uiId);               // 없으면 시드/캐시 폴백
    }

    private async Task<ScreenDefinition?> LoadFromDbAsync(string uiId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uiId)) return null;
        if (!_queries.TryGet("SYS.GetScreenDefinition", out var def) || def is null) return null;
        var rows = await _dispatcher.QueryAsync(def.Sql, new Dictionary<string, object> { ["uiId"] = uiId }, ct);
        if (rows.Count == 0) return null;
        var json = rows[0].TryGetValue("DEFINITION_JSON", out var v) ? v?.ToString() : null;
        return string.IsNullOrWhiteSpace(json) ? null : ScreenDefinitionJson.Deserialize(json);
    }
}
