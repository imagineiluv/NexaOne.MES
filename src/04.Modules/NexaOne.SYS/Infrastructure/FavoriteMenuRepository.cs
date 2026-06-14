using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Domain;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.SYS.Infrastructure;

public sealed class FavoriteMenuRepository : QueryRepository, IFavoriteMenuRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public FavoriteMenuRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task<IReadOnlyList<FavoriteMenu>> GetByUserAsync(
        string userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_FAVORITE_MENU
            WHERE USER_ID = @userId
            ORDER BY DISPLAY_SEQUENCE";
        var rows = await QueryAsync<FavoriteRow>(sql, new { userId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task UpsertAsync(FavoriteMenu favorite, CancellationToken ct = default)
    {
        // 방언 추상화: MSSQL은 병합문(WITH HOLDLOCK), SQLite는 ON CONFLICT DO UPDATE를 생성한다.
        // KEY = PK(USER_ID, MENU_ID) — SQLite ON CONFLICT 충돌 컬럼은 반드시 PK/UNIQUE여야 한다.
        // BuildUpsertSql은 INSERT에 KEY+DATA 전체를, UPDATE SET에는 DATA만 넣는다. 원본 병합문은
        // CREATED_AT을 INSERT만 하고 UPDATE에서는 제외해 최초 등록 시점을 보존했으나, BuildUpsertSql에는
        // "INSERT 전용" 컬럼 표현이 없어 CREATED_AT을 DATA에 포함한다(그렇지 않으면 NOT NULL INSERT 실패).
        // 실제 호출 경로상 충돌(MATCHED) 업서트는 재정렬뿐이고, 이때 전달되는 CREATED_AT은 GetByUser로
        // 읽어 Restore된 기존 값과 동일하며(재등록은 RemoveFavorite로 선삭제 후 신규 INSERT), 따라서
        // CREATED_AT 보존 의미는 모든 실제 경로에서 동일하게 유지된다.
        // CREATED_AT은 insert-only — 충돌(재정렬) 시 최초 등록 시점을 보존한다.
        var sql = _dialect.BuildUpsertSql(
            "SYS_FAVORITE_MENU",
            new[] { "USER_ID", "MENU_ID" },
            new[] { "DISPLAY_SEQUENCE" },
            insertOnlyColumns: new[] { "CREATED_AT" });

        // BuildUpsertSql은 @<COLUMN_NAME>(SNAKE_CASE) 플레이스홀더를 쓰므로 컬럼명 키로 파라미터를 구성한다.
        var r = FavoriteRow.FromDomain(favorite);
        var p = new Dapper.DynamicParameters();
        p.Add("USER_ID", r.UserId);
        p.Add("MENU_ID", r.MenuId);
        p.Add("DISPLAY_SEQUENCE", r.DisplaySequence);
        p.Add("CREATED_AT", r.CreatedAt);

        // SYS_FAVORITE_MENU에는 감사 컬럼이 없고(V013 DDL), DynamicParameters는 ServiceObjectProcessor의
        // 리플렉션 기반 감사 주입(InsertAsync/UpdateAsync)과 호환되지 않으므로 파라미터를 그대로 전달하는
        // ExecuteAsync(감사 미주입 raw 실행 경로)로 업서트를 수행한다. (RecentMenuRepository와 동일 패턴)
        await _processor.ExecuteAsync(sql, p, ct);
    }

    public async Task DeleteAsync(string userId, string menuId, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM SYS_FAVORITE_MENU
            WHERE USER_ID = @userId AND MENU_ID = @menuId";
        await _processor.DeleteAsync(sql, new { userId, menuId }, ct);
    }

    private sealed class FavoriteRow
    {
        public string UserId { get; set; } = "";
        public string MenuId { get; set; } = "";
        public int DisplaySequence { get; set; }
        public DateTime CreatedAt { get; set; }

        public FavoriteMenu ToDomain() =>
            FavoriteMenu.Restore(UserId, MenuId, DisplaySequence, CreatedAt);

        public static FavoriteRow FromDomain(FavoriteMenu f) => new()
        {
            UserId = f.UserId,
            MenuId = f.MenuId,
            DisplaySequence = f.DisplaySequence,
            CreatedAt = f.CreatedAt
        };
    }
}
