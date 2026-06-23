namespace NexaOne.Server.Components;

/// <summary>
/// SmartUX 셸 사이드바의 계층 메뉴 노드(N단계 트리). 평면 SYS.MenuTree 행을 부모-자식으로 조립한 결과.
/// Folder는 토글(자식 펼침), Screen은 /meta/{UiId}로 이동하는 잎. MesShellLayout이 빌드하고 MesNavTree가 재귀 렌더한다.
/// </summary>
public sealed record MesMenuNode(
    string Id,
    string Name,
    string? ParentId,
    int Seq,
    string Type,
    string UiId,
    IReadOnlyList<MesMenuNode> Children)
{
    /// <summary>폴더 여부 — MENU_TYPE 기준(빈 폴더도 폴더로 취급해 토글·"하위 없음" 안내가 렌더되게 한다).</summary>
    public bool IsFolder => string.Equals(Type, "Folder", StringComparison.OrdinalIgnoreCase);
}
