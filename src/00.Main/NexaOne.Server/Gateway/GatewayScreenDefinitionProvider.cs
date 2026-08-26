using System.Collections.Concurrent;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Web.Services.Meta;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NexaOne.Server.Gateway;

/// <summary>DB-backed 화면정의 제공자(Phase 5a) — SYS_SCREEN_DEFINITION을 게이트웨이(IRuleDispatcher+명명쿼리)로
/// 읽어 /meta에 제공한다. 해석 순서는 DB, 런타임 등록 캐시, 불변 코드 시드 카탈로그다. 디자이너 SAVE는
/// command 게이트웨이(SYS.UpsertScreenDefinition)로 쓰고, 다음 로드 시 이 provider가 DB에서 읽는다.</summary>
public sealed class GatewayScreenDefinitionProvider : IScreenDefinitionProvider
{
    private readonly ConcurrentDictionary<string, ScreenDefinition> _runtimeDefinitions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ICodeScreenDefinitionCatalog _codeDefinitions;
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _queries;
    private readonly ILogger<GatewayScreenDefinitionProvider> _logger;

    public GatewayScreenDefinitionProvider(
        ICodeScreenDefinitionCatalog codeDefinitions,
        IRuleDispatcher dispatcher,
        IQueryRegistry queries,
        ILogger<GatewayScreenDefinitionProvider>? logger = null)
    {
        _codeDefinitions = codeDefinitions;
        _dispatcher = dispatcher;
        _queries = queries;
        _logger = logger ?? NullLogger<GatewayScreenDefinitionProvider>.Instance;
    }

    /// <summary>런타임 오버레이만 갱신하며 canonical 코드 시드에는 쓰지 않습니다.</summary>
    public void Register(ScreenDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _runtimeDefinitions[definition.UiId] = definition;
    }

    public bool TryGet(string uiId, out ScreenDefinition? definition)
    {
        definition = GetAsync(uiId).GetAwaiter().GetResult();
        return definition is not null;
    }

    public ScreenDefinition? Get(string uiId) => GetAsync(uiId).GetAwaiter().GetResult();

    public async Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default)
    {
        var fromDb = await LoadFromDbAsync(uiId, ct);
        if (fromDb is not null) return fromDb;
        if (_runtimeDefinitions.TryGetValue(uiId, out var runtime)) return runtime;
        return _codeDefinitions.Get(uiId);
    }

    public async Task<IReadOnlySet<string>> GetKnownUiIdsAsync(CancellationToken ct = default)
    {
        var codeIds = await _codeDefinitions.GetKnownUiIdsAsync(ct);
        var known = new HashSet<string>(codeIds, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(_runtimeDefinitions.Keys);

        if (!_queries.TryGet("SYS.ListScreenDefinitions", out var def) || def is null)
            return known;

        try
        {
            // 존재 여부만 필요하므로 메뉴 잎마다 SYS.GetScreenDefinition을 호출하지 않고 카탈로그를 1회 읽는다.
            var rows = await _dispatcher.QueryAsync(
                def.Sql,
                new Dictionary<string, object> { ["targetChannel"] = string.Empty },
                ct);
            foreach (var row in rows)
            {
                if (row.TryGetValue("UI_ID", out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
                    known.Add(value!.ToString()!);
            }
        }
        catch (Exception ex)
        {
            // DB 카탈로그 장애가 메뉴 전체를 '준비 중'으로 만들지 않도록 로컬 두 계층으로 강등한다.
            _logger.LogWarning(ex, "화면 정의 ID 카탈로그 일괄 조회 실패 — 코드 시드와 런타임 등록 집합을 사용합니다.");
        }

        return known;
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
