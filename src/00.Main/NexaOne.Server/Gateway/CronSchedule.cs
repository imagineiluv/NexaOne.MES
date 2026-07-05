namespace NexaOne.Server.Gateway;

/// <summary>배치 cron 스케줄 — 시스템 스케줄러(Quartz)와 동일한 엔진(<see cref="Quartz.CronExpression"/>)의 얇은
/// 래퍼. 공개 계약(TryParse/GetNextOccurrence)은 유지한다. Quartz 방언을 그대로 따른다: 6~7필드
/// (초 분 시 일 월 요일 [연]), 요일은 1~7(1=일)·SUN~SAT 이름, day-of-month/day-of-week 중 정확히 한쪽은 '?'
/// (양쪽 지정은 미지원 — 거부). 목록(,)·범위(-)·스텝(/)·잘못된 식 거부·존재하지 않는 날짜(2월 30일 등)의
/// null 반환은 Quartz가 표준 의미로 처리한다. 계산은 UTC 기준.
/// (레거시 배치식은 Quartz 출신이라 이 엔진으로 무변환·네이티브 파싱된다 — 이전 Cronos 래퍼의 '?'→'*'·
///  요일 7→0 전처리가 유발하던 의미 왜곡을 제거하고, 프로세스 전역 크론 라이브러리를 Quartz로 통일한다.)</summary>
public sealed class CronSchedule
{
    private readonly Quartz.CronExpression _expr;

    private CronSchedule(Quartz.CronExpression expr) => _expr = expr;

    public static bool TryParse(string? expression, out CronSchedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        try
        {
            // UTC 기준 계산(공개 계약 GetNextOccurrence가 UTC를 반환). Quartz 기본은 로컬 TZ라 명시 지정.
            var expr = new Quartz.CronExpression(expression.Trim()) { TimeZone = TimeZoneInfo.Utc };
            schedule = new CronSchedule(expr);
            return true;
        }
        catch (FormatException) { return false; }   // Quartz는 문법·범위·양쪽 요일 지정 오류를 FormatException으로 던진다
    }

    /// <summary>afterUtc '이후'(초과) 첫 발생 시각 — 매칭 불가(예: 2월 30일)면 null.</summary>
    public DateTime? GetNextOccurrence(DateTime afterUtc)
    {
        var fromUtc = afterUtc.Kind == DateTimeKind.Utc ? afterUtc : DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        // GetNextValidTimeAfter는 '초과'(exclusive) — 발생 시각과 정확히 같으면 다음 발생을 준다.
        var next = _expr.GetNextValidTimeAfter(new DateTimeOffset(fromUtc, TimeSpan.Zero));
        return next?.UtcDateTime;
    }
}
