namespace NexaOne.SYS.Domain;

/// <summary>
/// 사용자 즐겨찾기 메뉴 (설계서 20.12).
/// 메뉴 권한이 제거돼도 행은 삭제하지 않는다 — 조회 시 권한 메뉴와 교차해 숨긴다.
/// </summary>
public sealed class FavoriteMenu
{
    private FavoriteMenu() { }

    public string UserId { get; private set; } = string.Empty;
    public string MenuId { get; private set; } = string.Empty;
    /// <summary>사용자 지정 표시 순서 (현행 드래그 정렬의 웹 적응).</summary>
    public int DisplaySequence { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static FavoriteMenu Create(string userId, string menuId, int displaySequence) =>
        new()
        {
            UserId = userId,
            MenuId = menuId,
            DisplaySequence = displaySequence,
            CreatedAt = DateTime.UtcNow
        };

    public static FavoriteMenu Restore(string userId, string menuId, int displaySequence, DateTime createdAt) =>
        new()
        {
            UserId = userId,
            MenuId = menuId,
            DisplaySequence = displaySequence,
            CreatedAt = createdAt
        };

    public void ChangeSequence(int displaySequence) => DisplaySequence = displaySequence;
}
