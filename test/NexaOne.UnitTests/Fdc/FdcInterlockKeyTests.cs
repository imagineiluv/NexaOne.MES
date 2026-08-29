using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcInterlockKeyTests
{
    private const string LeftEquipmentId = "EQ\u001fZONE";
    private const string LeftParameterId = "TEMP";
    private const string RightEquipmentId = "EQ";
    private const string RightParameterId = "ZONE\u001fTEMP";

    private static readonly IReadOnlyList<FdcInterlockTopology> CollisionTopology =
    [
        new(LeftEquipmentId, [LeftParameterId]),
        new(RightEquipmentId, [RightParameterId])
    ];

    [Fact]
    public async Task Runtime_snapshot_keeps_control_character_key_pairs_isolated()
    {
        var rules = RuleRepository();
        var service = new FdcInterlockService(rules.Object, EmptyHistory().Object);

        await service.InitializeRuntimeAsync(CollisionTopology);

        var leftMatches = service.EvaluateRuntime(LeftEquipmentId, LeftParameterId, 50m);
        var rightMatches = service.EvaluateRuntime(RightEquipmentId, RightParameterId, 50m);

        leftMatches.Select(result => result.RuleId).Should().Equal("RULE-LEFT");
        rightMatches.Select(result => result.RuleId).Should().Equal("RULE-RIGHT");
    }

    [Fact]
    public async Task Startup_rejects_cross_equipment_open_effect_hidden_by_a_control_character_collision()
    {
        var rules = RuleRepository();
        var mismatchedEffect = FdcInterlockHistory.Create(
            "EFFECT-COLLISION",
            "RULE-RIGHT",
            LeftEquipmentId,
            LeftParameterId,
            50m,
            "STOP.RIGHT",
            "must not match the other equipment rule",
            DateTime.UtcNow).Value;
        var service = new FdcInterlockService(rules.Object, EmptyHistory([mismatchedEffect]).Object);

        var act = () => service.InitializeRuntimeAsync(CollisionTopology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EFFECT-COLLISION*no longer matches active rule/action*");
    }

    private static Mock<IFdcInterlockRuleRepository> RuleRepository()
    {
        var repository = new Mock<IFdcInterlockRuleRepository>();
        repository.Setup(x => x.GetByEquipmentAsync(LeftEquipmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("RULE-LEFT", LeftEquipmentId, LeftParameterId, "STOP.LEFT")]);
        repository.Setup(x => x.GetByEquipmentAsync(RightEquipmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("RULE-RIGHT", RightEquipmentId, RightParameterId, "STOP.RIGHT")]);
        return repository;
    }

    private static Mock<IFdcInterlockHistoryRepository> EmptyHistory(
        IReadOnlyList<FdcInterlockHistory>? openEffects = null)
    {
        var repository = new Mock<IFdcInterlockHistoryRepository>();
        repository.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(openEffects ?? Array.Empty<FdcInterlockHistory>());
        return repository;
    }

    private static FdcInterlockRule Rule(
        string ruleId,
        string equipmentId,
        string parameterId,
        string action) =>
        FdcInterlockRule.Create(
            ruleId,
            ruleId,
            equipmentId,
            parameterId,
            "GT",
            10m,
            action,
            1).Value;
}
