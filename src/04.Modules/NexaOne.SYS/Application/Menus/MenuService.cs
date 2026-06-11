using Microsoft.Extensions.Logging;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Menus;

public sealed class MenuService
{
    private readonly IMenuRepository _repository;
    private readonly ILogger<MenuService> _logger;

    public MenuService(IMenuRepository repository, ILogger<MenuService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MenuNode>> GetAuthorizedMenuTreeAsync(
        string userId,
        CancellationToken ct = default)
    {
        var items = await _repository.GetAuthorizedMenusAsync(userId, ct);
        return BuildTree(items);
    }

    public async Task<IReadOnlyList<MenuNode>> GetAllMenuTreeAsync(CancellationToken ct = default)
    {
        var items = await _repository.GetAllMenusAsync(ct);
        return BuildTree(items);
    }

    public static IReadOnlyList<MenuNode> BuildTree(IReadOnlyList<MenuItem> items)
    {
        var nodes = items.ToDictionary(i => i.MenuId, i => new MenuNode { Item = i });
        var roots = new List<MenuNode>();

        foreach (var node in nodes.Values)
        {
            if (string.IsNullOrEmpty(node.Item.ParentMenuId))
            {
                roots.Add(node);
            }
            else if (nodes.TryGetValue(node.Item.ParentMenuId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                // parent filtered out by permissions — treat as orphan root
                node.IsOrphan = true;
                roots.Add(node);
            }
        }

        SortAndSetDepth(roots, 0);
        return roots;
    }

    private static void SortAndSetDepth(List<MenuNode> nodes, int depth)
    {
        nodes.Sort((a, b) =>
        {
            var seq = a.Item.DisplaySequence.CompareTo(b.Item.DisplaySequence);
            if (seq != 0) return seq;
            // Folder before Screen
            var type = a.Item.MenuType.CompareTo(b.Item.MenuType);
            if (type != 0) return type;
            return string.Compare(a.Item.MenuId, b.Item.MenuId, StringComparison.Ordinal);
        });

        foreach (var node in nodes)
        {
            node.Depth = depth;
            if (node.Depth > 3)
                continue; // cap at 3 levels (matches original MainForm 3-tier menu)
            SortAndSetDepth(node.Children, depth + 1);
        }
    }
}
