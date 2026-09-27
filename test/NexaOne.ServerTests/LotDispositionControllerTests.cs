using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NexaOne.Common;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class LotDispositionControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Storage_or_transport_failure_returns_sanitized_503_with_safe_replay_guidance(
        bool transportFailure, bool nested)
    {
        Exception cause = transportFailure
            ? new IOException("private-commit-response")
            : new StorageFailure("private-database-detail");
        var failure = nested
            ? new AggregateException("private-transaction-detail",
                new AggregateException(cause), new InvalidOperationException("private-rollback-detail"))
            : cause;
        using var services = Services();
        var logger = new Mock<ILogger<LotDispositionController>>();
        var command = Command();
        var bridge = FailingBridge(command, failure);
        var controller = Controller(bridge.Object, logger.Object, services);

        var result = (await controller.Record(command, CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which;

        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        problem.Title.Should().Be("LOT disposition storage is unavailable.");
        problem.Detail.Should().Contain("write outcome may be unknown")
            .And.Contain("same idempotency key and original payload")
            .And.Contain("Do not use a new key");
        JsonSerializer.Serialize(problem).Should().NotContain("private-");
        logger.Verify(log => log.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.Is<Exception?>(logged => ReferenceEquals(logged, failure)),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        bridge.VerifyAll();
        bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unexpected_failure_propagates_without_storage_recovery_response()
    {
        var failure = new AggregateException("private-programming-detail",
            new InvalidOperationException("private-operation-detail"));
        using var services = Services();
        var logger = new Mock<ILogger<LotDispositionController>>();
        var command = Command();
        var bridge = FailingBridge(command, failure);
        var controller = Controller(bridge.Object, logger.Object, services);

        var observed = await Assert.ThrowsAsync<AggregateException>(
            () => controller.Record(command, CancellationToken.None));

        observed.Should().BeSameAs(failure);
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Success_conflict_and_missing_actor_keep_existing_result_mapping()
    {
        using var services = Services();
        var command = Command();
        var disposition = new LotDispositionDto("disposition-1", command.PlantId, command.LotId,
            command.WorkOrderId, command.ProcessId, command.DefectExecutionId, command.DefectCode,
            command.DispositionType, command.Quantity, command.ReasonCode, command.Reason,
            "operator01", DateTime.UtcNow, command.SourceExecutionId, command.IdempotencyKey,
            command.ClientChannel, command.DeviceId);
        var bridge = new Mock<ILotDispositionBridge>(MockBehavior.Strict);
        bridge.SetupSequence(value => value.RecordAsync(command, "operator01", CancellationToken.None))
            .ReturnsAsync(Result.Success(disposition))
            .ReturnsAsync(Result.Failure<LotDispositionDto>(Error.Conflict(
                "POM.LotDisposition.IdempotencyConflict", "Different input")));
        var controller = Controller(bridge.Object, Mock.Of<ILogger<LotDispositionController>>(), services);

        (await controller.Record(command, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(disposition);
        (await controller.Record(command, CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>().Which.StatusCode.Should().Be(409);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        (await controller.Record(command, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        bridge.Verify(value => value.RecordAsync(command, "operator01", CancellationToken.None), Times.Exactly(2));
        bridge.VerifyNoOtherCalls();
    }

    private static RecordLotDispositionDto Command() => new(
        "PLANT01", "lot-1", null, "CUT", "execution-1", "SCRATCH", "Scrap", 1m,
        "QUALITY", "confirmed defect", "DISP:LOST:lot-1", "MOBILE", "PDA-01", "execution-1");

    private static Mock<ILotDispositionBridge> FailingBridge(RecordLotDispositionDto command, Exception failure)
    {
        var bridge = new Mock<ILotDispositionBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.RecordAsync(command, "operator01", CancellationToken.None))
            .ThrowsAsync(failure);
        return bridge;
    }

    private static LotDispositionController Controller(
        ILotDispositionBridge bridge, ILogger<LotDispositionController> logger, IServiceProvider services)
        => new(bridge, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "operator01")], "test")),
                },
            },
        };

    private static ServiceProvider Services()
        => new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();

    private sealed class StorageFailure(string message) : DbException(message) { }
}
