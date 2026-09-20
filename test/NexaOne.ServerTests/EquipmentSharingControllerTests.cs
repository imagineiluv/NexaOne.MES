using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NexaFramework.Service;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class EquipmentSharingControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_failure_and_failed_rollback_return_503_and_log_the_original_aggregate(bool nested)
    {
        var failure = new AggregateException("private-transaction-detail",
            new StorageFailure("private-database-detail"),
            new InvalidOperationException("private-rollback-detail"));
        var reported = nested ? new AggregateException("private-outer-detail", failure) : failure;

        await AssertStorageUnavailable(reported);
    }

    [Fact]
    public async Task Business_failure_with_database_cleanup_failure_returns_503_instead_of_business_conflict()
    {
        var failure = new AggregateException("private-transaction-detail",
            new BusinessException("private-business-code"),
            new StorageFailure("private-cleanup-detail"));

        await AssertStorageUnavailable(failure);
    }

    [Fact]
    public async Task Direct_database_failure_keeps_the_sanitized_unknown_outcome_response()
    {
        await AssertStorageUnavailable(new StorageFailure("private-commit-detail"));
    }

    [Fact]
    public async Task Unexpected_aggregate_without_database_failure_propagates_unchanged()
    {
        var failure = new AggregateException("private-programming-detail",
            new InvalidOperationException("private-operation-detail"),
            new AggregateException(new ArgumentException("private-argument-detail")));
        using var services = Services();
        var logger = new Mock<ILogger<EquipmentSharingController>>();
        var controller = Controller(failure, logger.Object, services);

        var observed = await Assert.ThrowsAsync<AggregateException>(() => Return(controller));

        observed.Should().BeSameAs(failure);
        logger.VerifyNoOtherCalls();
    }

    private static async Task AssertStorageUnavailable(Exception failure)
    {
        using var services = Services();
        var logger = new Mock<ILogger<EquipmentSharingController>>();
        var controller = Controller(failure, logger.Object, services);

        var result = (await Return(controller)).Should().BeOfType<ObjectResult>().Which;

        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        problem.Title.Should().Be("Shared equipment storage is unavailable.");
        problem.Detail.Should().Contain("write outcome may be unknown")
            .And.Contain("by ID before retrying")
            .And.Contain("same operation ID and original payload");
        JsonSerializer.Serialize(problem).Should().NotContain("private-");
        logger.Verify(log => log.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.Is<Exception?>(logged => ReferenceEquals(logged, failure)),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private static ServiceProvider Services()
        => new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();

    private static EquipmentSharingController Controller(
        Exception failure, ILogger<EquipmentSharingController> logger, IServiceProvider services)
    {
        var bridge = new Mock<IEquipmentSharingBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.ReturnAsync("equipment-user", It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Guid>(), It.IsAny<Guid>(), CancellationToken.None)).ThrowsAsync(failure);
        return new(bridge.Object, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "equipment-user")], "test")),
                },
            },
        };
    }

    private static Task<IActionResult> Return(EquipmentSharingController controller)
        => controller.Return(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new EquipmentSharingController.VersionedCommand(Guid.NewGuid()), CancellationToken.None);

    private sealed class StorageFailure(string message) : DbException(message) { }
}
