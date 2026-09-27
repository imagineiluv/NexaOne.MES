using System.Globalization;

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
        "DAILY" => utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        "MONTHLY" => utcNow.ToString("yyyyMM", CultureInfo.InvariantCulture),
        "YEARLY" => utcNow.ToString("yyyy", CultureInfo.InvariantCulture),
        var other => throw new InvalidOperationException(
            $"ID rule '{RuleId}' has an unsupported reset cycle '{other}'."),
    };

    /// <summary>기간 리셋 규칙은 접두어의 {period} 자리에 기간 키를 넣어 재시작 후에도 ID가 겹치지 않게 한다.</summary>
    public string Format(int sequence, string period = "")
    {
        if (sequence <= 0)
            throw new InvalidOperationException($"ID rule '{RuleId}' cannot issue a non-positive sequence.");
        var cycle = Normalize(ResetCycle);
        var prefix = Prefix ?? string.Empty;
        if (cycle is null or "NEVER" or "NONE")
        {
            if (prefix.Contains("{period}", StringComparison.Ordinal))
                throw new InvalidOperationException($"ID rule '{RuleId}' has a period placeholder without a reset cycle.");
        }
        else if (!prefix.Contains("{period}", StringComparison.Ordinal) || string.IsNullOrEmpty(period))
            throw new InvalidOperationException($"ID rule '{RuleId}' needs a {{period}} prefix placeholder for periodic reset.");

        var length = SeqLength ?? 0;
        if (length < 0 || length > 10)
            throw new InvalidOperationException($"ID rule '{RuleId}' has an invalid sequence length.");
        var number = sequence.ToString(CultureInfo.InvariantCulture);
        if (length > 0 && number.Length > length)
            throw new InvalidOperationException($"ID rule '{RuleId}' sequence exceeds its configured length.");
        if (length > number.Length)
            number = number.PadLeft(length, '0');
        return prefix.Replace("{period}", period, StringComparison.Ordinal) + number;
    }

    private static string? Normalize(string? resetCycle)
        => string.IsNullOrWhiteSpace(resetCycle) ? null : resetCycle.Trim().ToUpperInvariant();
}
