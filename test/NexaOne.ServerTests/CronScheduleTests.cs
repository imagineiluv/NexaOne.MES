using FluentAssertions;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>배치 cron 스케줄(6필드) 파서/다음 발생 계산 — 레거시 Quartz 표기('?')·목록·범위·스텝·
/// dom/dow OR 의미·잘못된 식 거부를 검증한다(no-DB 순수 단위).</summary>
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
        // 레거시 대표식 "0 0 2 * * ?" — 매일 02:00:00. '?'는 '*' 동치로 수용.
        var after = new DateTime(2026, 7, 3, 1, 30, 0, DateTimeKind.Utc);
        Next("0 0 2 * * ?", after).Should().Be(new DateTime(2026, 7, 3, 2, 0, 0, DateTimeKind.Utc));

        // 이미 02:00을 지난 시각이면 다음 날.
        var afterTwo = new DateTime(2026, 7, 3, 2, 0, 0, DateTimeKind.Utc);
        Next("0 0 2 * * ?", afterTwo).Should().Be(new DateTime(2026, 7, 4, 2, 0, 0, DateTimeKind.Utc),
            "발생 시각과 정확히 같으면 '이후' 규약으로 다음 발생");
    }

    [Fact]
    public void Step_and_list_and_range_fields_are_supported()
    {
        var after = new DateTime(2026, 7, 3, 10, 7, 0, DateTimeKind.Utc);
        // 15분 스텝.
        Next("0 */15 * * * *", after).Should().Be(new DateTime(2026, 7, 3, 10, 15, 0, DateTimeKind.Utc));
        // 시각 목록.
        Next("0 0 9,18 * * *", after).Should().Be(new DateTime(2026, 7, 3, 18, 0, 0, DateTimeKind.Utc));
        // 시각 범위 — 10시대는 이미 지나는 중이므로 다음은 11:00.
        Next("0 0 9-11 * * *", after).Should().Be(new DateTime(2026, 7, 3, 11, 0, 0, DateTimeKind.Utc));
        // 시작값+스텝(Quartz "5/15") — 5,20,35,50분.
        Next("0 5/15 * * * *", after).Should().Be(new DateTime(2026, 7, 3, 10, 20, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Day_of_week_and_month_boundaries()
    {
        // 2026-07-03 = 금요일. 다음 월요일(1) 08:00.
        var after = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
        Next("0 0 8 ? * 1", after).Should().Be(new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc));
        // 7=일요일 별칭 — 다음 일요일 08:00.
        Next("0 0 8 ? * 7", after).Should().Be(new DateTime(2026, 7, 5, 8, 0, 0, DateTimeKind.Utc));
        // 매월 1일 정오 — 월 경계 점프.
        Next("0 0 12 1 * ?", after).Should().Be(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Dom_and_dow_both_restricted_means_or_semantics()
    {
        // 표준 cron: 일(15일)과 요일(월요일) 둘 다 제한 → OR. 2026-07-03(금) 이후 첫 월요일=7/6 < 15일.
        var after = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
        Next("0 0 0 15 * 1", after).Should().Be(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("")]                     // 빈 식
    [InlineData("0 0 2 * *")]            // 5필드(초 누락)
    [InlineData("0 0 25 * * *")]         // 시 범위 초과
    [InlineData("0 0 2 32 * *")]         // 일 범위 초과
    [InlineData("0 0 2 * 13 *")]         // 월 범위 초과
    [InlineData("0 0 2 * * 8")]          // 요일 범위 초과(0-7)
    [InlineData("a b c d e f")]          // 비숫자
    [InlineData("0 0/0 * * * *")]        // 0 스텝
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
