using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class FavoriteMenuRepository : QueryRepository, IFavoriteMenuRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FavoriteMenuRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
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
        // HOLDLOCK: 동시 업서트 시 MERGE race로 인한 PK 중복 오류 방지 (ConditionSettingRepository와 동일 패턴)
        // 재등록/재정렬 시 CREATED_AT은 최초 등록 시점을 유지한다
        const string sql = @"MERGE SYS_FAVORITE_MENU WITH (HOLDLOCK) AS t
            USING (SELECT @UserId AS USER_ID, @MenuId AS MENU_ID) AS s
               ON t.USER_ID = s.USER_ID AND t.MENU_ID = s.MENU_ID
            WHEN MATCHED THEN
                UPDATE SET DISPLAY_SEQUENCE = @DisplaySequence
            WHEN NOT MATCHED THEN
                INSERT (USER_ID, MENU_ID, DISPLAY_SEQUENCE, CREATED_AT)
                VALUES (@UserId, @MenuId, @DisplaySequence, @CreatedAt);";
        await _processor.UpdateAsync(sql, FavoriteRow.FromDomain(favorite), ct);
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
