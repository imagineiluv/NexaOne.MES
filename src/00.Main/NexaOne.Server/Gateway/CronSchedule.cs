using System.Globalization;

namespace NexaOne.Server.Gateway;

/// <summary>배치 cron 스케줄(6필드: 초 분 시 일 월 요일) — 외부 의존 없는 최소 구현.
/// 지원: 숫자·*·?(=* 동치, 레거시 Quartz 표기 수용)·목록(,)·범위(-)·스텝(/). 요일은 0~7(0·7=일요일).
/// 일(dom)·요일(dow)이 둘 다 제한되면 표준 cron 의미(OR)로 판정한다. 요일/월 영문명(MON, JAN)은 미지원(문서화).
/// 레거시 예: "0 0 2 * * ?" = 매일 02:00:00. 계산은 UTC 기준(다음 발생 시각 탐색, 상한 5년).</summary>
public sealed class CronSchedule
{
    private readonly HashSet<int> _seconds;
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private CronSchedule(
        HashSet<int> seconds, HashSet<int> minutes, HashSet<int> hours,
        HashSet<int> daysOfMonth, HashSet<int> months, HashSet<int> daysOfWeek,
        bool domRestricted, bool dowRestricted)
    {
        _seconds = seconds;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
    }

    public static bool TryParse(string? expression, out CronSchedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 6) return false;

        if (!TryParseField(fields[0], 0, 59, out var seconds)) return false;
        if (!TryParseField(fields[1], 0, 59, out var minutes)) return false;
        if (!TryParseField(fields[2], 0, 23, out var hours)) return false;
        if (!TryParseField(fields[3], 1, 31, out var dom)) return false;
        if (!TryParseField(fields[4], 1, 12, out var months)) return false;
        if (!TryParseField(fields[5], 0, 7, out var dow)) return false;
        // 요일 7=일요일 별칭 → 0으로 정규화.
        if (dow!.Remove(7)) dow.Add(0);

        schedule = new CronSchedule(
            seconds!, minutes!, hours!, dom!, months!, dow,
            domRestricted: !IsWildcard(fields[3]), dowRestricted: !IsWildcard(fields[5]));
        return true;
    }

    /// <summary>afterUtc '이후'(초과) 첫 발생 시각 — 매칭 불가(예: 2월 30일)면 null(상한 5년 탐색).</summary>
    public DateTime? GetNextOccurrence(DateTime afterUtc)
    {
        var t = new DateTime(afterUtc.Year, afterUtc.Month, afterUtc.Day, afterUtc.Hour, afterUtc.Minute, afterUtc.Second,
            DateTimeKind.Utc).AddSeconds(1);
        var limit = t.AddYears(5);
        var guard = 0;

        while (t < limit && ++guard < 500_000)
        {
            if (!_months.Contains(t.Month))
            {
                // 다음 달 1일 00:00:00으로 점프.
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                continue;
            }
            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }
            if (!_hours.Contains(t.Hour))
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
                continue;
            }
            if (!_minutes.Contains(t.Minute))
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
                continue;
            }
            if (!_seconds.Contains(t.Second))
            {
                t = t.AddSeconds(1);
                continue;
            }
            return t;
        }
        return null;
    }

    // 표준 cron 의미 — dom/dow 둘 다 제한이면 OR, 하나만 제한이면 그 필드가 판정, 둘 다 *면 항상 참.
    private bool DayMatches(DateTime t)
    {
        var domMatch = _daysOfMonth.Contains(t.Day);
        var dowMatch = _daysOfWeek.Contains((int)t.DayOfWeek);   // DayOfWeek.Sunday=0 — 필드 규약과 일치
        return (_domRestricted, _dowRestricted) switch
        {
            (true, true) => domMatch || dowMatch,
            (true, false) => domMatch,
            (false, true) => dowMatch,
            _ => true,
        };
    }

    private static bool IsWildcard(string field) => field is "*" or "?";

    private static bool TryParseField(string field, int min, int max, out HashSet<int>? values)
    {
        values = null;
        var set = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var body = part;
            var step = 1;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                body = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                    return false;
            }

            int rangeStart, rangeEnd;
            if (IsWildcard(body))
            {
                rangeStart = min;
                rangeEnd = max;
            }
            else if (body.Contains('-'))
            {
                var bounds = body.Split('-');
                if (bounds.Length != 2
                    || !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out rangeStart)
                    || !int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out rangeEnd))
                    return false;
            }
            else
            {
                if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out rangeStart)) return false;
                // 단일 값 + 스텝("5/15")은 Quartz 의미(시작값부터 max까지 스텝).
                rangeEnd = slash >= 0 ? max : rangeStart;
            }

            if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd) return false;
            for (var v = rangeStart; v <= rangeEnd; v += step) set.Add(v);
        }

        if (set.Count == 0) return false;
        values = set;
        return true;
    }
}
