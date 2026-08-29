using FluentAssertions;
using NexaOne.Server.Components.Pages;
using NexaOne.Web.Services.Meta;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class HostMrpPlanningContractTests
{
    [Fact]
    public void Bulk_handler_accepts_only_the_mrp_conversion_command_case_insensitively()
    {
        HostMrpPlanning.HandlesBridgeBulkCommand(MrpConversionMetaCommands.Convert.ToUpperInvariant())
            .Should().BeTrue();

        HostMrpPlanning.HandlesBridgeBulkCommand("bridge:pom.work-order.start")
            .Should().BeFalse("another bridge command must stay with its own driver or host");
        HostMrpPlanning.HandlesBridgeBulkCommand("bridge:pom.mrp.convert.extra")
            .Should().BeFalse("the host contract is an exact command ID, not a prefix");
    }
}
