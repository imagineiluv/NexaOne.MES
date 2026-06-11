using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class RecentMenuRepository : QueryRepository, IRecentMenuRepository
{
    private readonly ServiceObjectProcessor _processor;

    public RecentMenuRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<RecentMenu>> GetByUserAsync(
        string userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_RECENT_MENU WITH(NOLOCK)
            WHERE USER_ID = @userId
            ORDER BY LAST_USED_AT DESC";
        var rows = await QueryAsync<RecentRow>(sql, new { userId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task UpsertAsync(RecentMenu recent, CancellationToken ct = default)
    {
        // HOLDLOCK: 동시 업서트 시 MERGE race로 인한 PK 중복 오류 방지 (ConditionSettingRepository와 동일 패턴)
        const string sql = @"MERGE SYS_RECENT_MENU WITH (HOLDLOCK) AS t
            USING (SELECT @UserId AS USER_ID, @MenuId AS MENU_ID) AS s
               ON t.USER_ID = s.USER_ID AND t.MENU_ID = s.MENU_ID
            WHEN MATCHED THEN
                UPDATE SET LAST_USED_AT = @LastUsedAt
            WHEN NOT MATCHED THEN
                INSERT (USER_ID, MENU_ID, LAST_USED_AT)
                VALUES (@UserId, @MenuId, @LastUsedAt);";
        await _processor.UpdateAsync(sql, RecentRow.FromDomain(recent), ct);
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
