namespace NexaOne.SYS.Domain;

public enum MenuItemType { Folder, Screen }

public sealed class MenuItem
{
    private MenuItem() { }

    public string MenuId { get; private init; } = string.Empty;
    public string MenuName { get; private init; } = string.Empty;
    public string? ParentMenuId { get; private init; }
    public int DisplaySequence { get; private init; }
    public MenuItemType MenuType { get; private init; }
    public string ProgramId { get; private init; } = string.Empty;
    public string? ImageId { get; private init; }
    public string? Options { get; private init; }
    public string UiId { get; private init; } = string.Empty;

    public static MenuItem Create(
        string menuId,
        string menuName,
        string? parentMenuId,
        int displaySequence,
        MenuItemType menuType,
        string programId,
        string uiId,
        string? imageId = null,
        string? options = null)
    {
        return new MenuItem
        {
            MenuId         = menuId,
            MenuName       = menuName,
            ParentMenuId   = NormalizeParent(parentMenuId),
            DisplaySequence = displaySequence,
            MenuType       = menuType,
            ProgramId      = programId,
            UiId           = uiId,
            ImageId        = imageId,
            Options        = options
        };
    }

    private static string? NormalizeParent(string? v)
        => string.IsNullOrWhiteSpace(v) || v == "*" ? null : v;
}
