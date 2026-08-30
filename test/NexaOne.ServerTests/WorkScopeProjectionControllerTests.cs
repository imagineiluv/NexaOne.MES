using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Server.Gateway;
using NexaOne.Server.Security;
using NexaOne.ServiceContracts.Pom;
using FluentAssertions;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class WorkScopeProjectionControllerTests
{
    [Fact]
    public async Task Authenticated_new_equipment_projection_is_accepted_without_mutating_the_work_scope()
    {
        const string secret = "installation-secret-with-adequate-entropy";
        var configuration = Configuration(secret);
        var bridge = new StubProjectionBridge(new WorkScopeProjectionReceiptDto(
            "cleaner-a", "event-1", "WS-1", Replay: false, IsCurrent: true,
            CurrentRevision: 7, AcceptedAt: new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc)));
        var controller = new WorkScopeProjectionController(
            bridge,
            new ConfigurationEquipmentClientAuthenticator(configuration))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Request.Headers[EquipmentClientAuthentication.ClientSecretHeader] = secret;

        var result = await controller.Ingest(Command(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        bridge.ReceivedSourceClientId.Should().Be("cleaner-a");
    }

    [Fact]
    public async Task Missing_secret_and_equipment_outside_the_allow_list_never_reach_the_bridge()
    {
        const string secret = "installation-secret-with-adequate-entropy";
        var bridge = StubProjectionBridge.Success(replay: false);
        var controller = CreateController(Configuration(secret), bridge);

        (await controller.Ingest(Command(), CancellationToken.None))
            .Should().BeOfType<UnauthorizedResult>();
        controller.Request.Headers[EquipmentClientAuthentication.ClientSecretHeader] = secret;
        (await controller.Ingest(Command() with { EquipmentId = "EQ-2" }, CancellationToken.None))
            .Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        bridge.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Https_policy_is_enforced_before_projection_credentials()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RunAdmission:RequireHttps"] = "true",
            })
            .Build();
        var bridge = StubProjectionBridge.Success(replay: false);
        var controller = CreateController(configuration, bridge);

        var result = await controller.Ingest(Command(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
        bridge.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Exact_replay_returns_ok_and_hash_conflict_returns_conflict()
    {
        const string secret = "installation-secret-with-adequate-entropy";
        var replayController = CreateController(
            Configuration(secret), StubProjectionBridge.Success(replay: true));
        replayController.Request.Headers[EquipmentClientAuthentication.ClientSecretHeader] = secret;
        var conflictController = CreateController(
            Configuration(secret),
            new StubProjectionBridge(Result.Failure<WorkScopeProjectionReceiptDto>(
                Error.Conflict("Projection.EventHashConflict", "different hash"))));
        conflictController.Request.Headers[EquipmentClientAuthentication.ClientSecretHeader] = secret;

        (await replayController.Ingest(Command(), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await conflictController.Ingest(Command(), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
    }

    private static WorkScopeProjectionController CreateController(
        IConfiguration configuration,
        StubProjectionBridge bridge) => new(
            bridge,
            new ConfigurationEquipmentClientAuthenticator(configuration))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static IConfiguration Configuration(string secret) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RunAdmission:RequireHttps"] = "false",
            ["RunAdmission:Clients:cleaner-a:SecretSha256"] =
                "8c2489b33db91a76c2f08a4ec69c06163c598efda1453f0acb37df2e6d5026ba",
            ["RunAdmission:Clients:cleaner-a:ClientId"] = "cleaner-a",
            ["RunAdmission:Clients:cleaner-a:EquipmentIds:0"] = "EQ-1",
        })
        .Build();

    private static WorkScopeProjectionCommand Command() => new(
        ClientId: "cleaner-a",
        EventId: "event-1",
        WorkScopeId: "WS-1",
        EquipmentId: "EQ-1",
        OperationKey: "clean-pair-1",
        PairRunId: "pair-1",
        SequenceRunId: "sequence-1",
        Status: WorkScopeProjectionStatus.Running,
        TerminalCleanupCompleted: false,
        RecipeId: "RECIPE-1",
        RecipeSnapshotHash: new string('a', 64),
        ProgramHash: new string('b', 64),
        Carriers:
        [
            new WorkScopeProjectionCarrierDto("front", "CARRIER-F", "RUN-F"),
            new WorkScopeProjectionCarrierDto("rear", "CARRIER-R", "RUN-R"),
        ],
        OccurredAt: new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
        Revision: 7,
        ResultCode: "PAIR_RUNNING");

    private sealed class StubProjectionBridge(Result<WorkScopeProjectionReceiptDto> result)
        : IWorkScopeProjectionBridge
    {
        public string? ReceivedSourceClientId { get; private set; }
        public int CallCount { get; private set; }

        public StubProjectionBridge(WorkScopeProjectionReceiptDto receipt)
            : this(Result.Success(receipt))
        {
        }

        public static StubProjectionBridge Success(bool replay) => new(
            new WorkScopeProjectionReceiptDto(
                "cleaner-a", "event-1", "WS-1", replay, IsCurrent: true,
                CurrentRevision: 7,
                AcceptedAt: new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc)));

        public Task<Result<WorkScopeProjectionReceiptDto>> IngestAsync(
            string sourceClientId,
            WorkScopeProjectionCommand command,
            CancellationToken ct = default)
        {
            CallCount++;
            ReceivedSourceClientId = sourceClientId;
            return Task.FromResult(result);
        }
    }
}
