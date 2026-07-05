using Cronos;

namespace NexaOne.Server.Gateway;

/// <summary>배치 cron 스케줄(6필드: 초 분 시 일 월 요일) — 검증된 Cronos 라이브러리 얇은 래퍼.
/// 공개 계약(TryParse/GetNextOccurrence)과 레거시 표기 수용은 유지한다: '?'(Quartz)=* 동치,
/// 요일 7=일요일 별칭(→0 정규화). 목록(,)·범위(-)·스텝(/)·dom/dow OR 의미·잘못된 식 거부·존재하지
/// 않는 날짜(2월 30일 등)의 null 반환은 Cronos가 표준 cron 의미로 처리한다. 계산은 UTC 기준.
/// (이전엔 무의존 수제 파서였으나 타임존·DST·엣지케이스 정확성을 위해 Cronos로 위임 — 계약 불변.)</summary>
public sealed class CronSchedule
{
    private readonly CronExpression _expr;

    private CronSchedule(CronExpression expr) => _expr = expr;

    public static bool TryParse(string? expression, out CronSchedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 6) return false;   // 초 분 시 일 월 요일(6필드) 고정

        // 레거시 수용: '?'(Quartz)=* 동치, 요일 7=일요일 별칭 → 0 정규화(Cronos는 0~6·이름 기반).
        for (var i = 0; i < fields.Length; i++) fields[i] = fields[i].Replace("?", "*");
        fields[5] = string.Join(',', fields[5].Split(',').Select(t => t == "7" ? "0" : t));

        try
        {
            schedule = new CronSchedule(CronExpression.Parse(string.Join(' ', fields), CronFormat.IncludeSeconds));
            return true;
        }
        catch (CronFormatException) { return false; }
    }

    /// <summary>afterUtc '이후'(초과) 첫 발생 시각 — 매칭 불가(예: 2월 30일)면 null.</summary>
    public DateTime? GetNextOccurrence(DateTime afterUtc)
    {
        var from = afterUtc.Kind == DateTimeKind.Utc ? afterUtc : DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        return _expr.GetNextOccurrence(from, inclusive: false);
    }
}
