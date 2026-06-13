using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Screens;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.SYS.Infrastructure;

/// <summary>SYS_SCREEN_DEFINITION 저장소(Phase 4 후속). 읽기는 QueryRepository(게이트웨이 ADR-001),
/// 쓰기는 ServiceObjectProcessor(트랜잭션)로 위임. 업서트는 방언 추상화(INexaOneEESDbCapability)로
/// MSSQL(MERGE/HOLDLOCK)·SQLite(ON CONFLICT) 양쪽에서 동작한다.</summary>
public sealed class ScreenDefinitionStore : QueryRepository, IScreenDefinitionStore
{
    private const string SelectCols = "UI_ID AS UiId, TITLE AS Title, DEFINITION_JSON AS DefinitionJson";
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public ScreenDefinitionStore(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public Task<IReadOnlyList<ScreenDefinitionRecord>> GetAllAsync(CancellationToken ct = default)
        => QueryAsync<ScreenDefinitionRecord>(
            $"SELECT {SelectCols} FROM SYS_SCREEN_DEFINITION ORDER BY UI_ID", null, ct);

    public Task<ScreenDefinitionRecord?> GetAsync(string uiId, CancellationToken ct = default)
        => QueryFirstOrDefaultAsync<ScreenDefinitionRecord>(
            $"SELECT {SelectCols} FROM SYS_SCREEN_DEFINITION WHERE UI_ID = @uiId", new { uiId }, ct);

    public Task UpsertAsync(ScreenDefinitionRecord record, CancellationToken ct = default)
    {
        // KEY = PK(UI_ID), DATA = UPDATE SET 대상. BuildUpsertSql은 @<COLUMN_NAME>(SNAKE_CASE) 플레이스홀더를
        // 생성하므로 컬럼명 키로 파라미터를 바인딩한다. 감사 컬럼은 ServiceObjectProcessor.InjectAudit가
        // PascalCase 키만 생성해 @SNAKE_CASE에 바인딩되지 않으므로, 여기서 직접 값을 채워 넣고
        // 감사 주입이 없는 실행 경로(_processor.ExecuteAsync: 트랜잭션 + 순수 Dapper passthrough)로 실행한다.
        var sql = _dialect.BuildUpsertSql(
            "SYS_SCREEN_DEFINITION",
            new[] { "UI_ID" },
            new[] { "TITLE", "DEFINITION_JSON", "CREATED_BY", "CREATED_AT", "UPDATED_BY", "UPDATED_AT" });

        var now = DateTime.UtcNow;
        var p = new Dapper.DynamicParameters();
        p.Add("UI_ID", record.UiId);
        p.Add("TITLE", record.Title);
        p.Add("DEFINITION_JSON", record.DefinitionJson);
        p.Add("CREATED_BY", "SYSTEM");
        p.Add("CREATED_AT", now);
        p.Add("UPDATED_BY", "SYSTEM");
        p.Add("UPDATED_AT", now);
        return _processor.ExecuteAsync(sql, p, ct);
    }
}
