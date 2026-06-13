using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class MenuRepository : QueryRepository, IMenuRepository
{
    private readonly ServiceObjectProcessor _processor;

    public MenuRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<MenuItem>> GetAuthorizedMenusAsync(
        string userId,
        CancellationToken ct = default)
    {
        // Single-role model: SYS_USER.ROLE_ID → SYS_MENU_ROLE.ROLE_ID
        const string sql = @"
            SELECT DISTINCT
                M.MENU_ID, M.MENU_NAME, M.PARENT_MENU_ID, M.DISPLAY_SEQUENCE,
                M.MENU_TYPE, M.PROGRAM_ID, M.IMAGE_ID, M.OPTIONS, M.UI_ID
            FROM SYS_MENU M
            JOIN SYS_MENU_ROLE MR
              ON MR.MENU_ID = M.MENU_ID AND MR.VALID_STATE = 'Valid'
            JOIN SYS_USER U
              ON U.ROLE_ID = MR.ROLE_ID AND U.USER_ID = @userId
            WHERE M.VALID_STATE = 'Valid'
            ORDER BY M.PARENT_MENU_ID, M.DISPLAY_SEQUENCE, M.MENU_TYPE, M.MENU_ID";

        var rows = await QueryAsync<MenuRow>(sql, new { userId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<MenuItem>> GetAllMenusAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE,
                   MENU_TYPE, PROGRAM_ID, IMAGE_ID, OPTIONS, UI_ID
            FROM SYS_MENU
            WHERE VALID_STATE = 'Valid'
            ORDER BY PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, MENU_ID";
        var rows = await QueryAsync<MenuRow>(sql, null, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(MenuItem item, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO SYS_MENU
                (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE,
                 PROGRAM_ID, IMAGE_ID, OPTIONS, UI_ID, VALID_STATE)
            VALUES
                (@MenuId, @MenuName, @ParentMenuId, @DisplaySequence, @MenuType,
                 @ProgramId, @ImageId, @Options, @UiId, 'Valid')";
        await _processor.InsertAsync(sql, MenuRow.FromDomain(item), ct);
    }

    public async Task UpdateAsync(MenuItem item, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE SYS_MENU SET
                MENU_NAME = @MenuName, PARENT_MENU_ID = @ParentMenuId,
                DISPLAY_SEQUENCE = @DisplaySequence, MENU_TYPE = @MenuType,
                PROGRAM_ID = @ProgramId, IMAGE_ID = @ImageId, OPTIONS = @Options
            WHERE MENU_ID = @MenuId";
        await _processor.UpdateAsync(sql, MenuRow.FromDomain(item), ct);
    }

    private sealed class MenuRow
    {
        public string MenuId { get; set; } = "";
        public string MenuName { get; set; } = "";
        public string? ParentMenuId { get; set; }
        public int DisplaySequence { get; set; }
        public string MenuType { get; set; } = "Screen";
        public string ProgramId { get; set; } = "";
        public string? ImageId { get; set; }
        public string? Options { get; set; }
        public string UiId { get; set; } = "";

        public MenuItem ToDomain()
        {
            Enum.TryParse<MenuItemType>(MenuType, ignoreCase: true, out var type);
            return MenuItem.Create(MenuId, MenuName, ParentMenuId,
                DisplaySequence, type, ProgramId, UiId, ImageId, Options);
        }

        public static MenuRow FromDomain(MenuItem m) => new()
        {
            MenuId          = m.MenuId,
            MenuName        = m.MenuName,
            ParentMenuId    = m.ParentMenuId,
            DisplaySequence = m.DisplaySequence,
            MenuType        = m.MenuType.ToString(),
            ProgramId       = m.ProgramId,
            ImageId         = m.ImageId,
            Options         = m.Options,
            UiId            = m.UiId
        };
    }
}
