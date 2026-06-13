using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Screens;

namespace NexaOne.SYS.Infrastructure;

/// <summary>SYS_SCREEN_DEFINITION 저장소(Phase 4 후속). 읽기는 QueryRepository(게이트웨이 ADR-001),
/// 쓰기는 ServiceObjectProcessor(트랜잭션·감사필드)로 위임. 업서트는 HOLDLOCK MERGE(동시성 안전).</summary>
public sealed class ScreenDefinitionStore : QueryRepository, IScreenDefinitionStore
{
    private const string SelectCols = "UI_ID AS UiId, TITLE AS Title, DEFINITION_JSON AS DefinitionJson";
    private readonly ServiceObjectProcessor _processor;

    public ScreenDefinitionStore(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public Task<IReadOnlyList<ScreenDefinitionRecord>> GetAllAsync(CancellationToken ct = default)
        => QueryAsync<ScreenDefinitionRecord>(
            $"SELECT {SelectCols} FROM SYS_SCREEN_DEFINITION ORDER BY UI_ID", null, ct);

    public Task<ScreenDefinitionRecord?> GetAsync(string uiId, CancellationToken ct = default)
        => QueryFirstOrDefaultAsync<ScreenDefinitionRecord>(
            $"SELECT {SelectCols} FROM SYS_SCREEN_DEFINITION WHERE UI_ID = @uiId", new { uiId }, ct);

    public Task UpsertAsync(ScreenDefinitionRecord record, CancellationToken ct = default)
    {
        const string sql = @"
            MERGE INTO SYS_SCREEN_DEFINITION WITH (HOLDLOCK) AS t
            USING (SELECT @UiId AS UI_ID) AS s ON (t.UI_ID = s.UI_ID)
            WHEN MATCHED THEN
                UPDATE SET TITLE = @Title, DEFINITION_JSON = @DefinitionJson,
                           UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHEN NOT MATCHED THEN
                INSERT (UI_ID, TITLE, DEFINITION_JSON, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@UiId, @Title, @DefinitionJson, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt);";
        return _processor.InsertAsync(sql, new { record.UiId, record.Title, record.DefinitionJson }, ct);
    }
}
