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
using NexaOne.ServiceContracts.Sls;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class SlsWriteRecoveryControllerTests
{
    [Fact]
    public async Task Sales_request_writes_report_uncertain_storage_outcome_without_leaking_details()
    {
        var failure = new StorageFailure("private-sales-request-database");
        var bridge = new Mock<ISalesRequestBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.CreateDraftAsync(It.IsAny<SalesRequestDraftCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        bridge.Setup(x => x.ReceiveAsync(It.IsAny<SalesRequestReceiptCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        bridge.Setup(x => x.WithdrawAsync(It.IsAny<SalesRequestWithdrawCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        using var services = Services();
        var controller = WithUser(new SalesRequestController(bridge.Object,
            Mock.Of<ILogger<SalesRequestController>>()), services);

        AssertRecovery(await controller.Create(new("SR-1", "request", "C-1", "P-1", DateTime.UtcNow, 1),
            CancellationToken.None));
        AssertRecovery(await controller.Receive("SR-1", new("SO-1", "PLANT-1", "order",
            DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), CancellationToken.None));
        AssertRecovery(await controller.Withdraw("SR-1", CancellationToken.None));
        bridge.VerifyAll();
        bridge.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Delivery_writes_report_storage_or_transport_failure_without_leaking_details(
        bool transportFailure, bool nested)
    {
        Exception cause = transportFailure
            ? new IOException("private-commit-response")
            : new StorageFailure("private-delivery-database");
        var failure = nested
            ? new AggregateException("private-transaction", new AggregateException(cause))
            : cause;
        var bridge = new Mock<ISalesOrderDeliveryBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.RequestDeliveryAsync(It.IsAny<SalesOrderDeliveryCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        bridge.Setup(x => x.ConfirmDeliveryAsync(It.IsAny<SalesOrderDeliveryConfirmationCommand>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        using var services = Services();
        var logger = new Mock<ILogger<SalesOrderDeliveryController>>();
        var controller = WithUser(new SalesOrderDeliveryController(bridge.Object, logger.Object), services);

        AssertRecovery(await controller.RequestDelivery("SO-1", new("DO-1", "DI-1"), CancellationToken.None));
        AssertRecovery(await controller.ConfirmDelivery("SO-1", CancellationToken.None));
        logger.Verify(log => log.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.Is<Exception?>(reported => ReferenceEquals(reported, failure)),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(2));
        bridge.VerifyAll();
        bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sales_order_writes_include_non_generic_delete_result()
    {
        var failure = new StorageFailure("private-sales-order-database");
        var bridge = new Mock<ISalesOrderCommandBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.SaveDraftAsync(It.IsAny<SalesOrderDraftCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        bridge.Setup(x => x.DeleteDraftAsync("SO-1", "seller", It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        bridge.Setup(x => x.ConfirmAsync("SO-1", "seller", It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        bridge.Setup(x => x.CloseAsync("SO-1", "seller", It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        using var services = Services();
        var controller = WithUser(new SalesOrderCommandController(bridge.Object,
            Mock.Of<ILogger<SalesOrderCommandController>>()), services);

        AssertRecovery(await controller.SaveDraft(new("SO-1", "order", "PLANT-1", "C-1", "P-1",
            DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 1), CancellationToken.None));
        AssertRecovery(await controller.DeleteDraft("SO-1", CancellationToken.None));
        AssertRecovery(await controller.Confirm("SO-1", CancellationToken.None));
        AssertRecovery(await controller.Close("SO-1", CancellationToken.None));
        bridge.VerifyAll();
        bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Business_conflict_and_missing_actor_keep_their_existing_mappings()
    {
        var bridge = new Mock<ISalesRequestBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.WithdrawAsync(It.IsAny<SalesRequestWithdrawCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<SalesRequestState>(Error.Conflict("SLS_CONFLICT", "already changed")));
        using var services = Services();
        var logger = new Mock<ILogger<SalesRequestController>>();
        var controller = WithUser(new SalesRequestController(bridge.Object, logger.Object), services);

        (await controller.Withdraw("SR-1", CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        (await controller.Withdraw("SR-1", CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        logger.VerifyNoOtherCalls();
        bridge.Verify(x => x.WithdrawAsync(It.IsAny<SalesRequestWithdrawCommand>(),
            It.IsAny<CancellationToken>()), Times.Once);
        bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Programming_failure_and_cancellation_are_not_reported_as_storage_outages()
    {
        var bridge = new Mock<ISalesRequestBridge>(MockBehavior.Strict);
        var programming = new AggregateException(new InvalidOperationException("private-programming"));
        var cancelled = new OperationCanceledException();
        bridge.SetupSequence(x => x.WithdrawAsync(It.IsAny<SalesRequestWithdrawCommand>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(programming)
            .ThrowsAsync(cancelled);
        using var services = Services();
        var logger = new Mock<ILogger<SalesRequestController>>();
        var controller = WithUser(new SalesRequestController(bridge.Object, logger.Object), services);

        (await Assert.ThrowsAsync<AggregateException>(() => controller.Withdraw("SR-1", CancellationToken.None)))
            .Should().BeSameAs(programming);
        (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Withdraw("SR-1",
            CancellationToken.None))).Should().BeSameAs(cancelled);
        logger.VerifyNoOtherCalls();
    }

    private static void AssertRecovery(IActionResult action)
    {
        var response = action.Should().BeOfType<ObjectResult>().Which;
        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = response.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        problem.Title.Should().Be("SLS storage is unavailable.");
        problem.Detail.Should().Contain("write outcome may be unknown")
            .And.Contain("SLS.SalesRequestById")
            .And.Contain("SLS.SalesOrderById")
            .And.Contain("sls:read")
            .And.Contain("Do not retry automatically");
        JsonSerializer.Serialize(problem).Should().NotContain("private-");
    }

    private static T WithUser<T>(T controller, IServiceProvider services) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "seller")], "test")),
            },
        };
        return controller;
    }

    private static ServiceProvider Services()
        => new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();

    private sealed class StorageFailure(string message) : DbException(message) { }
}
