using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class FdcDomainTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    // ── FdcInterlockRule ──────────────────────────────────────────────────────

    [Fact]
    public void Create_rule_valid_succeeds()
    {
        var result = FdcInterlockRule.Create("RULE001", "온도 상한", "EQ001", "PARAM001", "GT", 100m, "STOP", 1);
        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_rule_invalid_operator_fails()
    {
        var result = FdcInterlockRule.Create("RULE001", "규칙", "EQ001", "PARAM001", "NE", 100m, "STOP", 1);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_rule_invalid_action_fails()
    {
        var result = FdcInterlockRule.Create("RULE001", "규칙", "EQ001", "PARAM001", "GT", 100m, "PAUSE", 1);
        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("GT",  101, true)]
    [InlineData("GT",  100, false)]
    [InlineData("LT",   99, true)]
    [InlineData("LT",  100, false)]
    [InlineData("GTE", 100, true)]
    [InlineData("LTE", 100, true)]
    [InlineData("EQ",  100, true)]
    [InlineData("EQ",   99, false)]
    public void Evaluate_returns_correct_result(string op, decimal value, bool expected)
    {
        var rule = FdcInterlockRule.Create("RULE001", "규칙", "EQ001", "PARAM001", op, 100m, "ALARM", 1).Value;
        rule.Evaluate(value).Should().Be(expected);
    }

    [Fact]
    public void Deactivate_sets_IsActive_false()
    {
        var rule = FdcInterlockRule.Create("RULE001", "규칙", "EQ001", "PARAM001", "GT", 100m, "STOP", 1).Value;
        rule.Deactivate();
        rule.IsActive.Should().BeFalse();
    }

    // ── FdcParameter ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_parameter_valid_succeeds()
    {
        var result = FdcParameter.Create("PARAM001", "온도", "EQ001", "℃", 50m, 200m);
        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_parameter_lower_greater_than_upper_fails()
    {
        var result = FdcParameter.Create("PARAM001", "온도", "EQ001", "℃", 200m, 50m);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_parameter_lower_equals_upper_fails()
    {
        var result = FdcParameter.Create("PARAM001", "온도", "EQ001", "℃", 100m, 100m);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SetControlLimits_updates_lcl_ucl()
    {
        var param = FdcParameter.Create("PARAM001", "온도", "EQ001", "℃", 50m, 200m).Value;
        param.SetControlLimits(80m, 170m).IsSuccess.Should().BeTrue();
        param.LowerControlLimit.Should().Be(80m);
        param.UpperControlLimit.Should().Be(170m);
    }

    [Fact]
    public void UpdateLimits_rejects_inverted_range()
    {
        var param = FdcParameter.Create("PARAM001", "온도", "EQ001", "℃", 50m, 200m).Value;

        param.UpdateLimits(10m, 5m).IsFailure.Should().BeTrue("mutator도 Create와 같은 불변식을 강제해야 한다");
        param.LowerLimit.Should().Be(50m, "거부된 갱신은 기존 값을 바꾸지 않는다");
        param.UpperLimit.Should().Be(200m);

        param.UpdateLimits(60m, 180m).IsSuccess.Should().BeTrue();
        param.LowerLimit.Should().Be(60m);
        param.UpperLimit.Should().Be(180m);
    }

    [Fact]
    public void AssignToGroup_sets_and_clears_group()
    {
        var param = FdcParameter.Create("PARAM001", "온도", "EQ001", "℃", 50m, 200m).Value;
        param.GroupId.Should().BeNull();

        param.AssignToGroup("GRP-1");
        param.GroupId.Should().Be("GRP-1");

        param.AssignToGroup(null);
        param.GroupId.Should().BeNull("null 배정은 그룹 해제");
    }

    [Fact]
    public void Deactivate_parameter_sets_IsActive_false()
    {
        var param = FdcParameter.Create("PARAM001", "온도", "EQ001", "℃", 50m, 200m).Value;
        param.Deactivate();
        param.IsActive.Should().BeFalse();
    }

    // ── FdcCollectData ────────────────────────────────────────────────────────

    [Fact]
    public void Create_collect_data_valid_succeeds()
    {
        var result = FdcCollectData.Create("DATA001", "EQ001", "PARAM001", 120m, Now, "Good", 50m, 200m);
        result.IsSuccess.Should().BeTrue();
        result.Value.IsOutOfSpec.Should().BeFalse();
    }

    [Fact]
    public void Create_collect_data_invalid_quality_fails()
    {
        var result = FdcCollectData.Create("DATA001", "EQ001", "PARAM001", 120m, Now, "Normal", 50m, 200m);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void IsOutOfSpec_true_when_value_exceeds_upper_limit()
    {
        var data = FdcCollectData.Create("DATA001", "EQ001", "PARAM001", 250m, Now, "Bad", 50m, 200m).Value;
        data.IsOutOfSpec.Should().BeTrue();
    }

    [Fact]
    public void IsOutOfSpec_true_when_value_below_lower_limit()
    {
        var data = FdcCollectData.Create("DATA001", "EQ001", "PARAM001", 10m, Now, "Bad", 50m, 200m).Value;
        data.IsOutOfSpec.Should().BeTrue();
    }
}
