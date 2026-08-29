using Moq;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class FdcInterlockServiceTests
{
    private FdcInterlockService BuildService(Mock<IFdcInterlockRuleRepository> repo) => new(repo.Object);

    private static FdcInterlockRule ActiveRule(string op = "GT", decimal threshold = 100m, string action = "ALARM") =>
        FdcInterlockRule.Create("RULE001", "Test Rule", "EQ001", "PARAM01", op, threshold, action, 1).Value;

    // ── GetRulesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRules_returns_repository_list()
    {
        var rules = new List<FdcInterlockRule> { ActiveRule() };
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.GetByEquipmentAsync("EQ001", default)).ReturnsAsync(rules);

        var result = await BuildService(repo).GetRulesAsync("EQ001");

        result.Should().HaveCount(1);
    }

    // ── CreateRuleAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRule_valid_data_succeeds()
    {
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<FdcInterlockRule>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreateRuleAsync(
            "RULE001", "Test Rule", "EQ001", "PARAM01", "GT", 100m, "ALARM", 1);

        result.IsSuccess.Should().BeTrue();
        result.Value.Operator.Should().Be("GT");
        result.Value.ThresholdValue.Should().Be(100m);
        result.Value.Action.Should().Be("ALARM");
        result.Value.IsActive.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<FdcInterlockRule>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateRule_invalid_operator_fails()
    {
        var repo = new Mock<IFdcInterlockRuleRepository>();
        var result = await BuildService(repo).CreateRuleAsync(
            "RULE001", "Test Rule", "EQ001", "PARAM01", "INVALID", 100m, "ALARM", 1);

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<FdcInterlockRule>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateRule_project_specific_action_key_is_persisted_opaquely()
    {
        var repo = new Mock<IFdcInterlockRuleRepository>();
        var result = await BuildService(repo).CreateRuleAsync(
            "RULE001", "Test Rule", "EQ001", "PARAM01", "GT", 100m, "EXPLODE", 1);

        result.IsSuccess.Should().BeTrue();
        result.Value.Action.Should().Be("EXPLODE");
        repo.Verify(r => r.AddAsync(
            It.Is<FdcInterlockRule>(rule => rule.Action == "EXPLODE"), default), Times.Once);
    }

    [Fact]
    public async Task CreateRule_missing_id_fails()
    {
        var repo = new Mock<IFdcInterlockRuleRepository>();
        var result = await BuildService(repo).CreateRuleAsync(
            "", "Test Rule", "EQ001", "PARAM01", "GT", 100m, "ALARM", 1);

        result.IsFailure.Should().BeTrue();
    }

    // ── EvaluateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_value_exceeds_GT_threshold_triggers()
    {
        var rules = new List<FdcInterlockRule> { ActiveRule("GT", 100m, "STOP") };
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.GetActiveRulesAsync("EQ001", "PARAM01", default)).ReturnsAsync(rules);

        var result = await BuildService(repo).EvaluateAsync("EQ001", "PARAM01", 101m);

        result.IsTriggered.Should().BeTrue();
        result.Action.Should().Be("STOP");
    }

    [Fact]
    public async Task Evaluate_value_at_GT_threshold_does_not_trigger()
    {
        var rules = new List<FdcInterlockRule> { ActiveRule("GT", 100m) };
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.GetActiveRulesAsync("EQ001", "PARAM01", default)).ReturnsAsync(rules);

        var result = await BuildService(repo).EvaluateAsync("EQ001", "PARAM01", 100m);

        result.IsTriggered.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_value_below_LT_threshold_triggers()
    {
        var rules = new List<FdcInterlockRule> { ActiveRule("LT", 50m, "NOTIFY") };
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.GetActiveRulesAsync("EQ001", "PARAM01", default)).ReturnsAsync(rules);

        var result = await BuildService(repo).EvaluateAsync("EQ001", "PARAM01", 49m);

        result.IsTriggered.Should().BeTrue();
        result.Action.Should().Be("NOTIFY");
    }

    [Fact]
    public async Task Evaluate_GTE_threshold_boundary_triggers()
    {
        var rules = new List<FdcInterlockRule> { ActiveRule("GTE", 100m) };
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.GetActiveRulesAsync("EQ001", "PARAM01", default)).ReturnsAsync(rules);

        var result = await BuildService(repo).EvaluateAsync("EQ001", "PARAM01", 100m);

        result.IsTriggered.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_no_active_rules_returns_pass()
    {
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.GetActiveRulesAsync("EQ001", "PARAM01", default))
            .ReturnsAsync(new List<FdcInterlockRule>());

        var result = await BuildService(repo).EvaluateAsync("EQ001", "PARAM01", 999m);

        result.IsTriggered.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_respects_priority_order_first_match_wins()
    {
        var highPriority = FdcInterlockRule.Create("R1", "Rule 1", "EQ001", "PARAM01", "GT", 50m, "STOP", 1).Value;
        var lowPriority  = FdcInterlockRule.Create("R2", "Rule 2", "EQ001", "PARAM01", "GT", 80m, "ALARM", 2).Value;
        var rules = new List<FdcInterlockRule> { lowPriority, highPriority };
        var repo = new Mock<IFdcInterlockRuleRepository>();
        repo.Setup(r => r.GetActiveRulesAsync("EQ001", "PARAM01", default)).ReturnsAsync(rules);

        var result = await BuildService(repo).EvaluateAsync("EQ001", "PARAM01", 100m);

        result.IsTriggered.Should().BeTrue();
        result.Action.Should().Be("STOP");
    }
}
