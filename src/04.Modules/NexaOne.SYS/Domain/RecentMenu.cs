namespace NexaOne.SYS.Domain;

/// <summary>
/// 사용자 최근 사용 메뉴 (설계서 20.12).
/// 현행 %AppData% JSON 파일의 웹 적응 — 서버 DB에 영속해 브라우저/PC와 무관하게 유지된다.
/// 동일 메뉴 재사용 시 LastUsedAt만 갱신돼 목록 맨 앞으로 온다.
/// </summary>
public sealed class RecentMenu
{
    private RecentMenu() { }

    public string UserId { get; private set; } = string.Empty;
    public string MenuId { get; private set; } = string.Empty;
    public DateTime LastUsedAt { get; private set; }

    public static RecentMenu Create(string userId, string menuId) =>
        new()
        {
            UserId = userId,
            MenuId = menuId,
            LastUsedAt = DateTime.UtcNow
        };

    public static RecentMenu Restore(string userId, string menuId, DateTime lastUsedAt) =>
        new()
        {
            UserId = userId,
            MenuId = menuId,
            LastUsedAt = lastUsedAt
        };
}
