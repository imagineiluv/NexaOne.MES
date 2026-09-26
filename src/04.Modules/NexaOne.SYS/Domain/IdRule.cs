namespace NexaOne.SYS.Domain;

/// <summary>
/// ID 채번 규칙 1건입니다. 리셋 주기는 마지막 발급 기간 키와 비교해 판정한다.
/// </summary>
public sealed record IdRule(
    string RuleId,
    string? Prefix,
    int? SeqLength,
    string? ResetCycle)
{
    /// <summary>현재 시각의 리셋 기간 키다. 규칙이 없거나 해석할 수 없으면 예외로 계약 위반을 드러낸다.</summary>
    public string PeriodKey(DateTime utcNow) => Normalize(ResetCycle) switch
    {
        null or "NEVER" or "NONE" => string.Empty,
        "DAILY" => utcNow.ToString("yyyyMMdd"),
        "MONTHLY" => utcNow.ToString("yyyyMM"),
        "YEARLY" => utcNow.ToString("yyyy"),
        var other => throw new InvalidOperationException(
            $"ID rule '{RuleId}' has an unsupported reset cycle '{other}'."),
    };

    /// <summary>발급 시퀀스를 규칙 형식으로 렌더링한다(PREFIX + zero-padded sequence).</summary>
    public string Format(int sequence)
    {
        var length = SeqLength ?? 0;
        var number = sequence.ToString();
        if (length > number.Length)
            number = number.PadLeft(length, '0');
        return (Prefix ?? string.Empty) + number;
    }

    private static string? Normalize(string? resetCycle)
        => string.IsNullOrWhiteSpace(resetCycle) ? null : resetCycle.Trim().ToUpperInvariant();
}
