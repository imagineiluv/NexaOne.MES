using FluentAssertions;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>배치 cron 스케줄(Quartz CronExpression 래퍼) 파싱/다음 발생 계산 — Quartz 방언(6~7필드,
/// 요일 1=일·7=토, day-of-month/day-of-week 중 한쪽 '?' 필수)·목록·범위·스텝·잘못된 식 거부·불가능
/// 날짜 null을 검증한다(no-DB 순수 단위). 레거시 배치식은 Quartz 출신이라 무변환으로 파싱된다.</summary>
public sealed class CronScheduleTests
{
    private static DateTime Next(string expression, DateTime after)
    {
        CronSchedule.TryParse(expression, out var cron).Should().BeTrue($"'{expression}' 파싱");
        var next = cron!.GetNextOccurrence(after);
        next.Should().NotBeNull($"'{expression}'의 다음 발생");
        return next!.Value;
    }

    [Fact]
    public void Legacy_daily_2am_expression_fires_next_2am()
    {
        // 레거시 대표식 "0 0 2 * * ?" — 매일 02:00:00(dom=*, dow=?). Quartz 네이티브 파싱.
        var after = new DateTime(2026, 7, 3, 1, 30, 0, DateTimeKind.Utc);
        Next("0 0 2 * * ?", after).Should().Be(new DateTime(2026, 7, 3, 2, 0, 0, DateTimeKind.Utc));

        // 이미 02:00을 지난(같은) 시각이면 다음 날 — GetNextValidTimeAfter는 '초과' 규약.
        var afterTwo = new DateTime(2026, 7, 3, 2, 0, 0, DateTimeKind.Utc);
        Next("0 0 2 * * ?", afterTwo).Should().Be(new DateTime(2026, 7, 4, 2, 0, 0, DateTimeKind.Utc),
            "발생 시각과 정확히 같으면 '이후' 규약으로 다음 발생");
    }

    [Fact]
    public void Step_and_list_and_range_fields_are_supported()
    {
        // day-of-week는 '?'로 고정(Quartz는 dom·dow 양쪽 값 지정을 금지 — 한쪽은 '?').
        var after = new DateTime(2026, 7, 3, 10, 7, 0, DateTimeKind.Utc);
        // 15분 스텝.
        Next("0 */15 * * * ?", after).Should().Be(new DateTime(2026, 7, 3, 10, 15, 0, DateTimeKind.Utc));
        // 시각 목록.
        Next("0 0 9,18 * * ?", after).Should().Be(new DateTime(2026, 7, 3, 18, 0, 0, DateTimeKind.Utc));
        // 시각 범위 — 10시대는 이미 지나는 중이므로 다음은 11:00.
        Next("0 0 9-11 * * ?", after).Should().Be(new DateTime(2026, 7, 3, 11, 0, 0, DateTimeKind.Utc));
        // 시작값+스텝(Quartz "5/15") — 5,20,35,50분.
        Next("0 5/15 * * * ?", after).Should().Be(new DateTime(2026, 7, 3, 10, 20, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Day_of_week_uses_quartz_numbering_1_is_sunday()
    {
        // 2026-07-03 = 금요일. Quartz 요일: 1=일 … 7=토(Cronos/Unix의 0=일과 다르다 — 통일의 핵심 차이).
        var after = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
        // 이름(MON)으로 지정한 월요일 08:00 → 2026-07-06.
        Next("0 0 8 ? * MON", after).Should().Be(new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc));
        // 숫자 2 = 월요일(1=일 기준) → 동일하게 2026-07-06.
        Next("0 0 8 ? * 2", after).Should().Be(new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc));
        // 숫자 1 = 일요일 → 2026-07-05.
        Next("0 0 8 ? * 1", after).Should().Be(new DateTime(2026, 7, 5, 8, 0, 0, DateTimeKind.Utc));
        // 숫자 7 = 토요일(Cronos에서 7=일로 오변환하던 것을 Quartz 네이티브로 교정) → 2026-07-04.
        Next("0 0 8 ? * 7", after).Should().Be(new DateTime(2026, 7, 4, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Month_boundary_jumps_to_next_month()
    {
        // 매월 1일 정오(dom=1, dow=?) — 월 경계 점프.
        var after = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
        Next("0 0 12 1 * ?", after).Should().Be(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Both_day_of_month_and_day_of_week_restricted_is_rejected()
    {
        // Quartz는 day-of-month·day-of-week 중 정확히 한쪽이 '?'여야 한다 — 양쪽 값 지정은 미지원(거부).
        // (레거시 배치식은 항상 한쪽이 '?'라 실사용에서 발생하지 않는다. Cronos는 이를 AND로 조용히 해석했다.)
        CronSchedule.TryParse("0 0 0 15 * 1", out _).Should().BeFalse(
            "dom·dow 양쪽 값 지정은 Quartz가 거부한다('?' 필수)");
    }

    [Theory]
    [InlineData("")]                     // 빈 식
    [InlineData("0 0 2 * *")]            // 5필드(초 누락) — Quartz는 6~7필드
    [InlineData("0 0 25 * * ?")]         // 시 범위 초과
    [InlineData("0 0 2 32 * ?")]         // 일 범위 초과
    [InlineData("0 0 2 ? 13 *")]         // 월 범위 초과
    [InlineData("0 0 2 ? * 8")]          // 요일 범위 초과(Quartz 1-7)
    [InlineData("a b c d e f")]          // 비숫자
    [InlineData("70 0 2 * * ?")]         // 초 범위 초과(0-59)
    public void Invalid_expressions_are_rejected(string expression)
    {
        CronSchedule.TryParse(expression, out _).Should().BeFalse($"'{expression}'는 거부돼야 한다");
    }

    [Fact]
    public void Impossible_date_returns_null_not_infinite_loop()
    {
        CronSchedule.TryParse("0 0 0 30 2 ?", out var cron).Should().BeTrue("2월 30일 — 문법상 유효");
        cron!.GetNextOccurrence(new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc))
            .Should().BeNull("존재하지 않는 날짜는 상한 탐색 후 null(무한 루프 금지)");
    }
}
