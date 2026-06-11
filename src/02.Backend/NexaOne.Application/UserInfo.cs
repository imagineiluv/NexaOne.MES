namespace NexaOne.Application;

public sealed class UserInfo
{
    private static readonly ThreadLocal<UserInfo> _current = new(() => new UserInfo());

    public static UserInfo Current => _current.Value!;

    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string PlantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public List<string> AuthorityList { get; set; } = new();
    public string Language { get; set; } = "ko-KR";
    public bool IsAuthenticated => !string.IsNullOrEmpty(UserId);

    public static void Set(string userId, string userName, string plantId,
        string email, string roleId, IEnumerable<string>? authorities = null,
        string language = "ko-KR")
    {
        var info = Current;
        info.UserId = userId;
        info.UserName = userName;
        info.PlantId = plantId;
        info.Email = email;
        info.RoleId = roleId;
        info.AuthorityList = authorities?.ToList() ?? new List<string>();
        info.Language = language;
    }

    public static void Clear()
    {
        var info = Current;
        info.UserId = string.Empty;
        info.UserName = string.Empty;
        info.PlantId = string.Empty;
        info.Email = string.Empty;
        info.RoleId = string.Empty;
        info.AuthorityList = new List<string>();
        info.Language = "ko-KR";
    }
}
