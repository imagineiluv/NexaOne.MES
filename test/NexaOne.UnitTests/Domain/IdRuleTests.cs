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
    public void Format_expands_period_and_zero_pads_sequence()
    {
        var rule = new IdRule("WO", "WO-{period}-", 5, "Monthly");

        rule.Format(1, "202609").Should().Be("WO-202609-00001");
        rule.Format(42, "202609").Should().Be("WO-202609-00042");
        new IdRule("X", null, null, null).Format(7).Should().Be("7");
    }

    [Fact]
    public void Periodic_rule_without_period_placeholder_fails_before_claiming_a_number()
    {
        var rule = new IdRule("R", "REQ-", 3, "Monthly");
        var act = () => rule.Format(1, "202609");

        act.Should().Throw<InvalidOperationException>().WithMessage("*{period}*");
    }

    [Fact]
    public void Sequence_length_is_a_hard_limit_not_a_truncation_hint()
    {
        var rule = new IdRule("R", "WO-", 3, "Never");

        rule.Format(999).Should().Be("WO-999");
        var act = () => rule.Format(1000);
        act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds*");
        var zero = () => rule.Format(0);
        zero.Should().Throw<InvalidOperationException>();
    }
}
