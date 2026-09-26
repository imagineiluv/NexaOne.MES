using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>COM_ID_RULE 채번 규칙 — 기간 키와 PREFIX+zero-pad 렌더링의 순수 규칙 검증.</summary>
public sealed class IdRuleTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Period_key_tracks_the_reset_cycle()
    {
        new IdRule("R", "P", 4, null).PeriodKey(Now).Should().BeEmpty();
        new IdRule("R", "P", 4, "never").PeriodKey(Now).Should().BeEmpty();
        new IdRule("R", "P", 4, "Daily").PeriodKey(Now).Should().Be("20260927");
        new IdRule("R", "P", 4, "MONTHLY").PeriodKey(Now).Should().Be("202609");
        new IdRule("R", "P", 4, "Yearly").PeriodKey(Now).Should().Be("2026");
    }

    [Fact]
    public void Unknown_reset_cycle_fails_instead_of_silently_never_resetting()
    {
        var act = () => new IdRule("R", "P", 4, "Fortnightly").PeriodKey(Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Fortnightly*");
    }

    [Fact]
    public void Format_zero_pads_to_seq_length_and_overflows_without_truncation()
    {
        var rule = new IdRule("WO", "WO-", 5, "Monthly");

        rule.Format(1).Should().Be("WO-00001");
        rule.Format(42).Should().Be("WO-00042");
        rule.Format(123456).Should().Be("WO-123456",
            "시퀀스가 자릿수를 넘어가면 잘라내지 않고 그대로 커진다");
        new IdRule("X", null, null, null).Format(7).Should().Be("7");
    }
}
