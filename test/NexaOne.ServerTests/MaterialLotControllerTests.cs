using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NexaOne.Common;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MaterialLotControllerTests
{
    [Fact]
    public async Task Execute_uses_authenticated_claim_instead_of_body_actor()
    {
        var bridge = new Mock<IMaterialLotBridge>(MockBehavior.Strict);
        var command = Command() with { ActorId = "spoofed-user" };
        var expected = command with { ActorId = "operator-7" };
        bridge.Setup(x => x.ExecuteAsync(
                expected, CancellationToken.None))
            .ReturnsAsync(Result.Success(Dto()));

        var result = await Controller(bridge, "operator-7").Execute(command, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Fact]
    public async Task Execute_rejects_missing_actor_before_bridge_call()
    {
        var bridge = new Mock<IMaterialLotBridge>(MockBehavior.Strict);

        var result = await Controller(bridge, null).Execute(Command(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        bridge.Invocations.Should().BeEmpty();
    }

    private static MaterialLotController Controller(Mock<IMaterialLotBridge> bridge, string? actor)
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        if (actor is not null) identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, actor));
        return new MaterialLotController(bridge.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static MaterialLotCommand Command() => new(
        "TX-1", "KEY-1", MaterialLotOperations.Receive, "LOT-1", 0,
        DateTime.UnixEpoch, "MES", "EV-1", "MAT-1", Quantity: 10m, Unit: "kg",
        Location: "STORE");

    private static MaterialLotEventDto Dto() => new(
        "TX-1", "KEY-1", MaterialLotOperations.Receive, "LOT-1", "MAT-1", 10m,
        0m, 10m, 10m, null, "STORE", "InStock", 0, 1, DateTime.UnixEpoch,
        "operator-7", "MES", "EV-1", null, false);
}
