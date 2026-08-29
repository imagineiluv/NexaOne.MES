using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class TraceMaterialControllerTests
{
    [Fact]
    public async Task Binding_and_feed_endpoints_replace_body_actor_with_authenticated_actor()
    {
        var bridge = new RecordingBridge();
        var controller = Controller(bridge, "operator-7");
        var binding = BindingCommand() with { ActorId = "spoofed-binding" };
        var feed = FeedCommand() with { ActorId = "spoofed-feed" };

        var bindingResult = await controller.ExecuteBinding(binding, CancellationToken.None);
        var feedResult = await controller.ExecuteFeedSession(feed, CancellationToken.None);

        bindingResult.Should().BeOfType<OkObjectResult>();
        feedResult.Should().BeOfType<OkObjectResult>();
        bridge.BindingCommand.Should().Be(binding with { ActorId = "operator-7" });
        bridge.FeedCommand.Should().Be(feed with { ActorId = "operator-7" });
    }

    [Fact]
    public async Task Missing_actor_is_rejected_before_any_bridge_call()
    {
        var bridge = new RecordingBridge();
        var controller = Controller(bridge, null);

        var binding = await controller.ExecuteBinding(BindingCommand(), CancellationToken.None);
        var feed = await controller.ExecuteFeedSession(FeedCommand(), CancellationToken.None);

        binding.Should().BeOfType<UnauthorizedResult>();
        feed.Should().BeOfType<UnauthorizedResult>();
        bridge.BindingCommand.Should().BeNull();
        bridge.FeedCommand.Should().BeNull();
    }

    private static TraceMaterialController Controller(RecordingBridge bridge, string? actor)
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        if (actor is not null) identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, actor));
        return new TraceMaterialController(bridge)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static TraceBindingCommand BindingCommand() => new(
        TraceBindingOperations.Create,
        "B-01",
        0,
        "binding-01",
        "MES",
        "binding-source-01",
        DateTime.UnixEpoch.AddHours(1),
        DateTime.UnixEpoch.AddHours(1),
        PlantId: "P1",
        EquipmentId: "E1",
        ParameterId: "FLOW",
        FeedPointId: "FEED",
        CalculationMode: "Direct",
        ScaleFactor: 1m,
        OutputUnit: "kg");

    private static FeedSessionCommand FeedCommand() => new(
        FeedSessionOperations.Mount,
        "FS-01",
        0,
        "feed-01",
        "MES",
        "feed-source-01",
        DateTime.UnixEpoch.AddHours(2),
        PlantId: "P1",
        EquipmentId: "E1",
        FeedPointId: "FEED",
        MaterialLotId: "LOT-01");

    private sealed class RecordingBridge : ITraceMaterialBridge
    {
        public TraceBindingCommand? BindingCommand { get; private set; }
        public FeedSessionCommand? FeedCommand { get; private set; }

        public Task<Result<TraceBindingDto>> ExecuteBindingAsync(
            TraceBindingCommand command,
            CancellationToken ct = default)
        {
            BindingCommand = command;
            return Task.FromResult(Result.Success(new TraceBindingDto(
                command.BindingId, "P1", "E1", "FLOW", "FEED", "Direct", 1m,
                null, "kg", command.EffectiveAt, null, true, 1,
                TraceBindingOperations.Create, command.ActorId!, command.OccurredAt,
                command.SourceSystem, command.SourceEventId, null, null, false)));
        }

        public Task<Result<FeedSessionDto>> ExecuteFeedSessionAsync(
            FeedSessionCommand command,
            CancellationToken ct = default)
        {
            FeedCommand = command;
            return Task.FromResult(Result.Success(new FeedSessionDto(
                command.FeedSessionId, "P1", "E1", "FEED", "LOT-01", "MAT-01",
                null, null, null, null, null, command.OccurredAt, command.ActorId!,
                null, null, "Mounted", 1, FeedSessionOperations.Mount, command.ActorId!,
                command.OccurredAt, command.SourceSystem, command.SourceEventId,
                null, null, false)));
        }
    }
}
