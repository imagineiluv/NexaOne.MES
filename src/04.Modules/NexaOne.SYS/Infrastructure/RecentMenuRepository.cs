using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Domain;
using NexaDB.Data.Abstractions.Interfaces;

namespace NexaOne.SYS.Infrastructure;

public sealed class RecentMenuRepository : QueryRepository, IRecentMenuRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public RecentMenuRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task<IReadOnlyList<RecentMenu>> GetByUserAsync(
        string userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_RECENT_MENU
            WHERE USER_ID = @userId
            ORDER BY LAST_USED_AT DESC";
        var rows = await QueryAsync<RecentRow>(sql, new { userId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task UpsertAsync(RecentMenu recent, CancellationToken ct = default)
    {
        // 방언 추상화: MSSQL은 업서트(HOLDLOCK), SQLite는 ON CONFLICT DO UPDATE를 생성한다.
        // KEY = PK(USER_ID, MENU_ID) — SQLite ON CONFLICT 충돌 컬럼은 반드시 PK/UNIQUE여야 한다.
        var sql = _dialect.BuildUpsertSql(
            "SYS_RECENT_MENU",
            new[] { "USER_ID", "MENU_ID" },
            new[] { "LAST_USED_AT" });

        // BuildUpsertSql은 @<COLUMN_NAME>(SNAKE_CASE) 플레이스홀더를 쓰므로 컬럼명 키로 파라미터를 구성한다.
        var r = RecentRow.FromDomain(recent);
        var p = new Dapper.DynamicParameters();
        p.Add("USER_ID", r.UserId);
        p.Add("MENU_ID", r.MenuId);
        p.Add("LAST_USED_AT", r.LastUsedAt);

        // SYS_RECENT_MENU에는 감사 컬럼이 없고(V013 DDL), DynamicParameters는 ServiceObjectProcessor의
        // 리플렉션 기반 감사 주입(InsertAsync/UpdateAsync)과 호환되지 않으므로 파라미터를 그대로 전달하는
        // ExecuteAsync(감사 미주입 raw 실행 경로)로 업서트를 수행한다.
        await _processor.ExecuteAsync(sql, p, ct);
    }

    public async Task DeleteAsync(string userId, string menuId, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM SYS_RECENT_MENU
            WHERE USER_ID = @userId AND MENU_ID = @menuId";
        await _processor.DeleteAsync(sql, new { userId, menuId }, ct);
    }

    private sealed class RecentRow
    {
        public string UserId { get; set; } = "";
        public string MenuId { get; set; } = "";
        public DateTime LastUsedAt { get; set; }

        public RecentMenu ToDomain() =>
            RecentMenu.Restore(UserId, MenuId, LastUsedAt);

        public static RecentRow FromDomain(RecentMenu r) => new()
        {
            UserId = r.UserId,
            MenuId = r.MenuId,
            LastUsedAt = r.LastUsedAt
        };
    }
}
