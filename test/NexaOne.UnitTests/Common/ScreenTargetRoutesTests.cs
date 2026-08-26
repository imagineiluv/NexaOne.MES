using NexaOne.ServiceContracts.Sys;

namespace NexaOne.UnitTests.Common;

public sealed class ScreenTargetRoutesTests
{
    [Fact]
    public void Empty_target_uses_mes_default_route()
    {
        var target = ScreenTargetRoutes.Resolve("FACTORY_SAMPLE");

        target.Should().Be(new ScreenTarget("MES", "/meta/FACTORY_SAMPLE"));
    }

    [Theory]
    [InlineData("mobile", "/Mobile/WORK_EXECUTION", "MOBILE")]
    [InlineData("POP", "/POP/WORK_EXECUTION", "POP")]
    [InlineData("MES", "/meta/WORK_EXECUTION", "MES")]
    public void Channel_and_matching_explicit_route_are_normalized(
        string channel, string path, string expectedChannel)
    {
        var target = ScreenTargetRoutes.Resolve("WORK_EXECUTION", channel, path);

        target.Should().Be(new ScreenTarget(expectedChannel, path));
    }

    [Fact]
    public void Mismatched_route_is_rejected()
    {
        var act = () => ScreenTargetRoutes.Resolve(
            "WORK_EXECUTION", ScreenTargetRoutes.Mobile, "/POP/WORK_EXECUTION");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*EntryPath must be '/Mobile/WORK_EXECUTION'*");
    }

    [Fact]
    public void Unknown_channel_is_rejected()
    {
        var act = () => ScreenTargetRoutes.Resolve("WORK_EXECUTION", "TABLET");

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*TargetChannel must be MES, MOBILE, or POP*");
    }
}
