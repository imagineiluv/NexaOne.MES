using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Menus;

public sealed class MenuNode
{
    public required MenuItem Item { get; init; }
    public List<MenuNode> Children { get; } = [];
    public int Depth { get; set; }
    public bool IsOrphan { get; set; }
}
