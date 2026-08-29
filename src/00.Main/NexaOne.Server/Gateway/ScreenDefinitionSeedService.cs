using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>코드 화면 시드의 조회와 insert-only 가져오기를 담당하는 재사용 가능한 경계입니다.</summary>
public interface IScreenDefinitionSeedService
{
    Task<IReadOnlyList<ScreenSeedSummary>> ListAsync(CancellationToken ct = default);
    Task<ScreenSeedPreview?> GetAsync(string uiId, CancellationToken ct = default);
    Task<ScreenSeedImportResult> ImportAsync(
        string uiId, string importedBy, CancellationToken ct = default);
}

/// <summary>Designer 시드 목록에 필요한 경량 진단 요약입니다.</summary>
public sealed record ScreenSeedSummary(
    string UiId,
    string Title,
    string Purpose,
    bool DatabaseExists,
    bool CanImport,
    int ErrorCount,
    int AdvisoryCount);

/// <summary>클라이언트가 표시할 수 있는 화면 capability 진단입니다.</summary>
public sealed record ScreenSeedDiagnostic(string Code, string Severity, string Message);

/// <summary>
/// 코드 시드 미리보기입니다. DefinitionJson은 아직 DB에 저장되지 않은 원본이며,
/// TargetChannel/EntryPath는 가져오기 시 적용할 서버 기본값입니다.
/// </summary>
public sealed record ScreenSeedPreview(
    string UiId,
    string Title,
    string Purpose,
    string DefinitionJson,
    string TargetChannel,
    string EntryPath,
    bool DatabaseExists,
    bool CanImport,
    IReadOnlyList<ScreenSeedDiagnostic> Diagnostics);

public enum ScreenSeedImportStatus
{
    Imported,
    NotFound,
    AlreadyExists,
    CapabilityInvalid,
}

/// <summary>가져오기 결과와, 실패 시 UI가 그대로 표시할 수 있는 시드 진단을 함께 전달합니다.</summary>
public sealed record ScreenSeedImportResult(
    ScreenSeedImportStatus Status,
    ScreenSeedPreview? Preview = null);

/// <summary>
/// 렌더링 provider와 별도로 코드 원본을 읽고, 새 DB 행만 만드는 전용 명명 command를 실행합니다.
/// 존재 확인은 UX 힌트일 뿐이며 최종 중복 방지는 DB의 원자적 insert-only 문장이 담당합니다.
/// </summary>
public sealed class ScreenDefinitionSeedService : IScreenDefinitionSeedService
{
    private const string GetQueryId = "SYS.GetScreenDefinition";
    private const string ListQueryId = "SYS.ListScreenDefinitions";
    private const string ImportCommandId = "SYS.ImportSeedScreenDefinition";

    private readonly ICodeScreenDefinitionCatalog _seeds;
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _queries;
    private readonly IScreenDefinitionBindingValidator _bindings;
    private readonly IMetaCommandDriverCatalog _commands;

    public ScreenDefinitionSeedService(
        ICodeScreenDefinitionCatalog seeds,
        IRuleDispatcher dispatcher,
        IQueryRegistry queries,
        IScreenDefinitionBindingValidator bindings,
        IMetaCommandDriverCatalog commands)
    {
        _seeds = seeds;
        _dispatcher = dispatcher;
        _queries = queries;
        _bindings = bindings;
        _commands = commands;
    }

    /// <summary>모든 canonical 코드 시드를 한 번의 DB 카탈로그 조회와 결합합니다.</summary>
    public async Task<IReadOnlyList<ScreenSeedSummary>> ListAsync(CancellationToken ct = default)
    {
        var definitions = await _seeds.ListAsync(ct);
        var storedIds = await LoadStoredIdsAsync(ct);
        return definitions
            .Select(definition =>
            {
                var diagnostics = Diagnostics(definition);
                var databaseExists = storedIds.Contains(definition.UiId);
                var errorCount = diagnostics.Count(item => item.Severity == "Error");
                return new ScreenSeedSummary(
                    definition.UiId,
                    definition.Title,
                    definition.Purpose.ToString(),
                    databaseExists,
                    CanImport: !databaseExists && errorCount == 0,
                    ErrorCount: errorCount,
                    AdvisoryCount: diagnostics.Count(item => item.Severity == "Advisory"));
            })
            .ToArray();
    }

    /// <summary>DB 우선 해석을 거치지 않은 코드 원본과 저장 가능 여부를 반환합니다.</summary>
    public async Task<ScreenSeedPreview?> GetAsync(string uiId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uiId)) return null;
        var definition = _seeds.Get(uiId.Trim());
        if (definition is null) return null;

        var databaseExists = await DatabaseExistsAsync(definition.UiId, ct);
        return CreatePreview(definition, databaseExists);
    }

    /// <summary>
    /// capability 오류가 없는 시드만 저장합니다. 사전 조회 후 다른 요청이 먼저 저장하더라도
    /// SYS.ImportSeedScreenDefinition이 기존 행을 갱신하지 않고 0행을 반환하므로 충돌로 종료됩니다.
    /// </summary>
    public async Task<ScreenSeedImportResult> ImportAsync(
        string uiId, string importedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uiId))
            return new ScreenSeedImportResult(ScreenSeedImportStatus.NotFound);

        var definition = _seeds.Get(uiId.Trim());
        if (definition is null)
            return new ScreenSeedImportResult(ScreenSeedImportStatus.NotFound);

        var preview = CreatePreview(definition, databaseExists: false);
        if (preview.Diagnostics.Any(item => item.Severity == "Error"))
            return new ScreenSeedImportResult(ScreenSeedImportStatus.CapabilityInvalid, preview);

        var target = ScreenTargetRoutes.Resolve(definition.UiId);
        var command = RequiredQuery(ImportCommandId);
        var affected = await _dispatcher.ExecuteAsync(
            command.Sql,
            new Dictionary<string, object>
            {
                ["uiId"] = definition.UiId,
                ["title"] = definition.Title,
                ["definitionJson"] = ScreenDefinitionJson.Serialize(definition),
                ["targetChannel"] = target.TargetChannel,
                ["entryPath"] = target.EntryPath,
                ["currentUser"] = string.IsNullOrWhiteSpace(importedBy) ? "SYSTEM" : importedBy,
                ["utcNow"] = DateTime.UtcNow,
            },
            ct);

        if (affected <= 0)
        {
            return new ScreenSeedImportResult(
                ScreenSeedImportStatus.AlreadyExists,
                CreatePreview(definition, databaseExists: true));
        }

        return new ScreenSeedImportResult(
            ScreenSeedImportStatus.Imported,
            CreatePreview(definition, databaseExists: true));
    }

    private async Task<HashSet<string>> LoadStoredIdsAsync(CancellationToken ct)
    {
        var query = RequiredQuery(ListQueryId);
        var rows = await _dispatcher.QueryAsync(
            query.Sql,
            new Dictionary<string, object> { ["targetChannel"] = string.Empty },
            ct);
        return rows
            .Select(row => Value(row, "UI_ID"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> DatabaseExistsAsync(string uiId, CancellationToken ct)
    {
        var query = RequiredQuery(GetQueryId);
        var rows = await _dispatcher.QueryAsync(
            query.Sql,
            new Dictionary<string, object> { ["uiId"] = uiId },
            ct);
        return rows.Count > 0;
    }

    private ScreenSeedPreview CreatePreview(ScreenDefinition definition, bool databaseExists)
    {
        var diagnostics = Diagnostics(definition);
        var target = ScreenTargetRoutes.Resolve(definition.UiId);
        var hasErrors = diagnostics.Any(item => item.Severity == "Error");
        return new ScreenSeedPreview(
            definition.UiId,
            definition.Title,
            definition.Purpose.ToString(),
            ScreenDefinitionJson.Serialize(definition),
            target.TargetChannel,
            target.EntryPath,
            databaseExists,
            CanImport: !databaseExists && !hasErrors,
            diagnostics);
    }

    private ScreenSeedDiagnostic[] Diagnostics(ScreenDefinition definition)
        => ScreenDefinitionCapabilityValidator.Validate(definition, _commands)
            .Select(item => new ScreenSeedDiagnostic(
                item.Code,
                item.Severity.ToString(),
                item.Message))
            .Concat(_bindings.Validate(definition).Select(item => new ScreenSeedDiagnostic(
                item.Code,
                item.Severity.ToString(),
                $"{item.BindingPath}: {item.Message}")))
            .ToArray();

    private QueryDefinition RequiredQuery(string id)
    {
        if (_queries.TryGet(id, out var query) && query is not null) return query;
        throw new InvalidOperationException($"Required screen-definition query '{id}' is not registered.");
    }

    private static string Value(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}
