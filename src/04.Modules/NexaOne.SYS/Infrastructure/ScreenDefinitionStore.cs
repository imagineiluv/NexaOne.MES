using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Application.Screens;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.SYS.Infrastructure;

/// <summary>SYS_SCREEN_DEFINITION 저장소(Phase 4 후속). 읽기는 QueryRepository(게이트웨이 ADR-001),
/// 쓰기는 ServiceObjectProcessor(트랜잭션)로 위임. 업서트는 방언 추상화(INexaOneEESDbCapability)로
/// MSSQL(MERGE/HOLDLOCK)·SQLite(ON CONFLICT) 양쪽에서 동작한다.</summary>
public sealed class ScreenDefinitionStore : QueryRepository, IScreenDefinitionStore
{
    private const string SelectCols =
        "D.UI_ID AS UiId, D.TITLE AS Title, D.DEFINITION_JSON AS DefinitionJson, " +
        "T.TARGET_CHANNEL AS TargetChannel, T.ENTRY_PATH AS EntryPath";
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public ScreenDefinitionStore(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task<IReadOnlyList<ScreenDefinitionRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await QueryAsync<StoredScreenRow>(
            $"SELECT {SelectCols} FROM SYS_SCREEN_DEFINITION D " +
            "LEFT JOIN SYS_SCREEN_TARGET T ON T.UI_ID = D.UI_ID ORDER BY D.UI_ID", null, ct);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<ScreenDefinitionRecord?> GetAsync(string uiId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<StoredScreenRow>(
            $"SELECT {SelectCols} FROM SYS_SCREEN_DEFINITION D " +
            "LEFT JOIN SYS_SCREEN_TARGET T ON T.UI_ID = D.UI_ID WHERE D.UI_ID = @uiId", new { uiId }, ct);
        return row is null ? null : ToRecord(row);
    }

    public Task UpsertAsync(ScreenDefinitionRecord record, CancellationToken ct = default)
    {
        // KEY = PK(UI_ID), DATA = UPDATE SET 대상. BuildUpsertSql은 @<COLUMN_NAME>(SNAKE_CASE) 플레이스홀더를
        // 생성하므로 컬럼명 키로 파라미터를 바인딩한다. 감사 컬럼은 ServiceObjectProcessor.InjectAudit가
        // PascalCase 키만 생성해 @SNAKE_CASE에 바인딩되지 않으므로, 여기서 직접 값을 채워 넣고
        // 감사 주입이 없는 실행 경로(_processor.ExecuteAsync: 트랜잭션 + 순수 Dapper passthrough)로 실행한다.
        // CREATED_BY/CREATED_AT은 insert-only — 최초 등록 시점/등록자를 갱신(재저장) 때 보존한다.
        var definitionSql = _dialect.BuildUpsertSql(
            "SYS_SCREEN_DEFINITION",
            new[] { "UI_ID" },
            new[] { "TITLE", "DEFINITION_JSON", "UPDATED_BY", "UPDATED_AT" },
            insertOnlyColumns: new[] { "CREATED_BY", "CREATED_AT" });

        var targetSql = _dialect.BuildUpsertSql(
            "SYS_SCREEN_TARGET",
            new[] { "UI_ID" },
            new[] { "TARGET_CHANNEL", "ENTRY_PATH", "UPDATED_BY", "UPDATED_AT" },
            insertOnlyColumns: new[] { "CREATED_BY", "CREATED_AT" });

        var now = DateTime.UtcNow;
        var definition = new Dapper.DynamicParameters();
        definition.Add("UI_ID", record.UiId);
        definition.Add("TITLE", record.Title);
        definition.Add("DEFINITION_JSON", record.DefinitionJson);
        definition.Add("CREATED_BY", "SYSTEM");
        definition.Add("CREATED_AT", now);
        definition.Add("UPDATED_BY", "SYSTEM");
        definition.Add("UPDATED_AT", now);

        var (channel, entryPath) = NormalizeTarget(record);
        var target = new Dapper.DynamicParameters();
        target.Add("UI_ID", record.UiId);
        target.Add("TARGET_CHANNEL", channel);
        target.Add("ENTRY_PATH", entryPath);
        target.Add("CREATED_BY", "SYSTEM");
        target.Add("CREATED_AT", now);
        target.Add("UPDATED_BY", "SYSTEM");
        target.Add("UPDATED_AT", now);

        return _processor.ExecuteManyAsync(ct, (definitionSql, definition), (targetSql, target));
    }

    private static (string Channel, string EntryPath) NormalizeTarget(ScreenDefinitionRecord record)
    {
        var target = ScreenTargetRoutes.Resolve(record.UiId, record.TargetChannel, record.EntryPath);
        return (target.TargetChannel, target.EntryPath);
    }

    private static ScreenDefinitionRecord ToRecord(StoredScreenRow row)
    {
        var channel = string.IsNullOrWhiteSpace(row.TargetChannel) ? "MES" : row.TargetChannel;
        var path = string.IsNullOrWhiteSpace(row.EntryPath) ? $"/meta/{row.UiId}" : row.EntryPath;
        return new ScreenDefinitionRecord(row.UiId, row.Title, row.DefinitionJson, channel, path);
    }

    private sealed record StoredScreenRow(
        string UiId,
        string Title,
        string DefinitionJson,
        string? TargetChannel,
        string? EntryPath);
}
