using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Domain;
using NexaDB.Data.Abstractions.Interfaces;

namespace NexaOne.SYS.Infrastructure;

public sealed class ConditionSettingRepository : QueryRepository, IConditionSettingRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public ConditionSettingRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task<IReadOnlyList<ConditionSetting>> GetByMenuAsync(
        string userId, string menuId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_CONDITION_SETTING
            WHERE USER_ID = @userId AND MENU_ID = @menuId
            ORDER BY SAVED_AT DESC";
        var rows = await QueryAsync<ConditionRow>(sql, new { userId, menuId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<ConditionSetting?> GetAsync(
        string userId, string menuId, string name, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_CONDITION_SETTING
            WHERE USER_ID = @userId AND MENU_ID = @menuId AND NAME = @name";
        var row = await QueryFirstOrDefaultAsync<ConditionRow>(sql, new { userId, menuId, name }, ct);
        return row?.ToDomain();
    }

    public async Task UpsertAsync(ConditionSetting setting, CancellationToken ct = default)
    {
        // 방언 추상화 업서트: MSSQL=병합문 WITH(HOLDLOCK), SQLite=INSERT .. ON CONFLICT.
        // KEY_COL = PK(USER_ID, MENU_ID, NAME), DATA_COL = UPDATE 대상(SAVED_AT, VALUES_JSON).
        var sql = _dialect.BuildUpsertSql(
            "SYS_CONDITION_SETTING",
            new[] { "USER_ID", "MENU_ID", "NAME" },
            new[] { "SAVED_AT", "VALUES_JSON" });

        // BuildUpsertSql은 @<COLUMN_NAME>(SNAKE_CASE) 플레이스홀더를 사용한다.
        // 기존 Row는 PascalCase이므로 컬럼명을 프로퍼티명으로 갖는 익명 객체로 매핑한다.
        // (ServiceObjectProcessor가 param의 public 프로퍼티명을 그대로 파라미터 키로 사용 — DynamicParameters는
        //  내부 저장이라 동일 경로에서 키가 유실되므로 익명 객체로 컬럼명 키를 보장한다.)
        var r = ConditionRow.FromDomain(setting);
        var p = new
        {
            USER_ID = r.UserId,
            MENU_ID = r.MenuId,
            NAME = r.Name,
            SAVED_AT = r.SavedAt,
            VALUES_JSON = r.ValuesJson
        };
        await _processor.ExecuteAsync(sql, p, ct);
    }

    public async Task DeleteAsync(
        string userId, string menuId, string name, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM SYS_CONDITION_SETTING
            WHERE USER_ID = @userId AND MENU_ID = @menuId AND NAME = @name";
        await _processor.DeleteAsync(sql, new { userId, menuId, name }, ct);
    }

    private sealed class ConditionRow
    {
        public string UserId { get; set; } = "";
        public string MenuId { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public string ValuesJson { get; set; } = "";

        public ConditionSetting ToDomain() =>
            ConditionSetting.Create(UserId, MenuId, Name, ValuesJson, SavedAt);

        public static ConditionRow FromDomain(ConditionSetting s) => new()
        {
            UserId = s.UserId,
            MenuId = s.MenuId,
            Name = s.Name,
            SavedAt = s.SavedAt,
            ValuesJson = s.ValuesJson
        };
    }
}
