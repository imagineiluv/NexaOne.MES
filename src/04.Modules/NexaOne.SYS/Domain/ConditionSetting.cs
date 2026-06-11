namespace NexaOne.SYS.Domain;

/// <summary>
/// 사용자별/메뉴별 저장 조회 조건 (설계서 20.8).
/// 현행 ConditionSetting_{userId}_{menuId}.json 의 항목 하나에 해당한다.
/// </summary>
public sealed class ConditionSetting
{
    /// <summary>마지막 조회 조건(자동 저장)에 사용하는 예약 조건명. 한도/수동 삭제 대상에서 제외된다.</summary>
    public const string LatestName = "$latest";

    private ConditionSetting() { }

    public string UserId { get; private set; } = string.Empty;
    public string MenuId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public DateTime SavedAt { get; private set; }
    public string ValuesJson { get; private set; } = string.Empty;

    // DB 콜레이션(대소문자 무시)과 판정이 어긋나지 않도록 OrdinalIgnoreCase로 비교한다
    public bool IsLatest => string.Equals(Name, LatestName, StringComparison.OrdinalIgnoreCase);

    public static ConditionSetting Create(
        string userId,
        string menuId,
        string name,
        string valuesJson,
        DateTime savedAt) =>
        new()
        {
            UserId = userId,
            MenuId = menuId,
            Name = name,
            ValuesJson = valuesJson,
            SavedAt = savedAt
        };
}
