using System.Data;
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
using NexaFramework.Service.Inventory;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class StockControllerTests
{
    [Fact]
    public async Task Master_export_endpoints_preserve_contract_and_file_metadata()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var jobId = Guid.NewGuid();
        var query = new StockMasterExportQuery(StockMasterExportKind.Products, ["productId"]);
        var command = new StockMasterExportJobRequest(Guid.NewGuid(), StockMasterExportKind.Products, ["productId"]);
        var export = new StockMasterCsvExport("products.csv", "text/csv; charset=utf-8", [1, 2, 3], 1);
        var job = new StockMasterExportJob(jobId, Guid.NewGuid(), command.OperationId, command.Kind, command.Fields,
            false, StockMasterExportState.Pending, null, null, null, DateTimeOffset.UtcNow, null);
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.ExportMastersCsvAsync("stock-user", tenant, organization, query, default)).ReturnsAsync(export);
        bridge.Setup(value => value.QueueMasterExportAsync("stock-user", tenant, organization, command, default)).ReturnsAsync(job);
        bridge.Setup(value => value.GetMasterExportJobAsync("stock-user", tenant, organization, jobId, default)).ReturnsAsync(job);
        bridge.Setup(value => value.RetryMasterExportAsync("stock-user", tenant, organization, jobId, job.Version, default)).ReturnsAsync(job);
        bridge.Setup(value => value.DownloadMasterExportAsync("stock-user", tenant, organization, jobId, default)).ReturnsAsync(export);
        var controller = Controller(bridge.Object);

        var direct = (await controller.ExportMasters(tenant, organization, query, default)).Should().BeOfType<FileContentResult>().Which;
        direct.FileDownloadName.Should().Be(export.FileName); direct.FileContents.Should().BeSameAs(export.Content);
        (await controller.QueueMasterExport(tenant, organization, command, default)).Should().BeOfType<AcceptedAtActionResult>()
            .Which.Value.Should().BeSameAs(job);
        (await controller.GetMasterExportJob(tenant, organization, jobId, default)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(job);
        (await controller.RetryMasterExport(tenant, organization, jobId, new(job.Version), default)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(job);
        (await controller.DownloadMasterExport(tenant, organization, jobId, default)).Should().BeOfType<FileContentResult>()
            .Which.FileContents.Should().BeSameAs(export.Content);
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Master_import_forwards_the_exact_payload_and_maps_validation_to_422()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var command = new StockMasterImportRequest(Guid.NewGuid(), [new("PRODUCT", "EA")], [new("MAIN", "Main")]);
        var error = new StockMasterImportError("products", 1, "productId", "PRODUCT_NOT_FOUND");
        var rejected = new StockMasterImportResult(command.OperationId, false, false, 0, 0, 0, 0, [error]);
        var applied = rejected with { Applied = true, Errors = [] };
        using var cancellation = new CancellationTokenSource();
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        bridge.SetupSequence(value => value.ImportMastersAsync("stock-user", tenant, organization,
                It.Is<StockMasterImportRequest>(candidate => ReferenceEquals(candidate, command)), cancellation.Token))
            .ReturnsAsync(rejected)
            .ReturnsAsync(applied);
        var controller = Controller(bridge.Object);

        var invalid = (await controller.ImportMasters(tenant, organization, command, cancellation.Token))
            .Should().BeOfType<UnprocessableEntityObjectResult>().Which;
        invalid.Value.Should().BeSameAs(rejected);
        (await controller.ImportMasters(tenant, organization, command, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(applied);
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Lists_forward_the_domain_query_current_principal_scope_and_cancellation_without_repacking_pages()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var query = new InventoryQuery("목록 %_[X]", true, 7, 3);
        var products = new BusinessPage<Product>([], 7);
        var warehouses = new BusinessPage<Warehouse>([], 7);
        using var cancellation = new CancellationTokenSource();
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        bridge.Setup(b => b.ListProductsAsync("stock-user", tenant, organization, query, cancellation.Token)).ReturnsAsync(products);
        bridge.Setup(b => b.ListWarehousesAsync("stock-user", tenant, organization, query, cancellation.Token)).ReturnsAsync(warehouses);
        var controller = Controller(bridge.Object);

        (await controller.ListProducts(tenant, organization, query, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(products);
        (await controller.ListWarehouses(tenant, organization, query, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(warehouses);
        var anonymous = Controller(bridge.Object, userId: null);
        (await anonymous.ListProducts(tenant, organization, query, cancellation.Token)).Should().BeOfType<UnauthorizedResult>();
        (await anonymous.ListWarehouses(tenant, organization, query, cancellation.Token)).Should().BeOfType<UnauthorizedResult>();
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Posting_preserves_domain_payload_decimal_scope_principal_and_cancellation_token()
    {
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var posting = new StockPosting(Guid.NewGuid(), Guid.NewGuid(), StockMovementKind.Receipt,
            999999999999.123456m, null, Guid.NewGuid(), "HTTP receipt");
        var movement = new StockMovement(Guid.NewGuid(), new("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")),
            Guid.NewGuid(), posting, "persisted-business-identity", [new(posting.ToWarehouseId!.Value, posting.Quantity)]);
        using var cancellation = new CancellationTokenSource();
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.PostAsync("stock-user", tenant, organization,
            It.Is<StockPosting>(candidate => ReferenceEquals(candidate, posting)), cancellation.Token)).ReturnsAsync(movement);
        var controller = Controller(bridge.Object);

        var result = (await controller.Post(tenant, organization, posting, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which;

        result.Value.Should().BeSameAs(movement);
        bridge.VerifyAll();
        bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Balance_report_and_csv_export_preserve_filters_and_file_metadata()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var warehouse = Guid.NewGuid(); var variant = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var report = new StockBalanceReport(new("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")),
            DateTimeOffset.UtcNow, []);
        var export = new StockBalanceCsvExport("balances.csv", "text/csv; charset=utf-8", [0xef, 0xbb, 0xbf, 0x41]);
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.BuildBalanceReportAsync("stock-user", tenant, organization, warehouse, variant, cancellation.Token))
            .ReturnsAsync(report);
        bridge.Setup(value => value.ExportBalanceReportCsvAsync("stock-user", tenant, organization, warehouse, variant, cancellation.Token))
            .ReturnsAsync(export);
        var controller = Controller(bridge.Object);

        (await controller.BalanceReport(tenant, organization, warehouse, variant, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(report);
        var file = (await controller.ExportBalanceReport(tenant, organization, warehouse, variant, cancellation.Token))
            .Should().BeOfType<FileContentResult>().Which;
        file.FileContents.Should().BeSameAs(export.Content);
        file.ContentType.Should().Be(export.ContentType);
        file.FileDownloadName.Should().Be(export.FileName);
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_current_user_does_not_invoke_the_bridge()
    {
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        var controller = Controller(bridge.Object, userId: null);

        (await Read(controller)).Should().BeOfType<UnauthorizedResult>();

        bridge.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("STOCK_MOVEMENT_NOT_FOUND", 404)]
    [InlineData("INVALID_STOCK_POSTING", 400)]
    [InlineData("EXPLICIT_CREATE_OR_VERSIONED_UPDATE_REQUIRED", 400)]
    [InlineData("STOCK_REPORT_TOO_LARGE", 413)]
    [InlineData("STOCK_MASTER_EXPORT_TOO_LARGE", 413)]
    [InlineData("STOCK_OPERATION_CONFLICT", 409)]
    public async Task Business_errors_keep_equipment_gateway_status_and_code_conventions(string code, int status)
    {
        var result = (await Read(Failing(new BusinessException(code)))).Should().BeOfType<ObjectResult>().Which;

        result.StatusCode.Should().Be(status);
        JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task Revoked_access_returns_forbidden_and_CAS_returns_conflict()
    {
        (await Read(Failing(new BusinessException("BUSINESS_ACCESS_DENIED")))).Should().BeOfType<ForbidResult>();
        var result = (await Read(Failing(new DBConcurrencyException("private-cas-detail"))))
            .Should().BeOfType<ConflictObjectResult>().Which;
        JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString().Should().Be("BUSINESS_VERSION_CONFLICT");
        JsonSerializer.Serialize(result.Value).Should().NotContain("private-");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("nested")]
    [InlineData("business-and-cleanup")]
    public async Task Database_and_aggregate_cleanup_failures_are_sanitized_and_logged(string kind)
    {
        Exception failure = kind switch
        {
            "direct" => new StorageFailure("private-commit-detail"),
            "nested" => new AggregateException("private-outer-detail", new AggregateException(
                new StorageFailure("private-database-detail"), new InvalidOperationException("private-rollback-detail"))),
            _ => new AggregateException(new BusinessException("private-business-code"), new StorageFailure("private-cleanup-detail"))
        };
        using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var logger = new Mock<ILogger<StockController>>();

        var result = (await Read(Failing(failure, logger.Object, services))).Should().BeOfType<ObjectResult>().Which;

        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Title.Should().Be("Stock storage is unavailable.");
        problem.Detail.Should().Contain("write outcome may be unknown").And.Contain("same operation ID and original payload");
        JsonSerializer.Serialize(problem).Should().NotContain("private-");
        logger.Verify(log => log.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.Is<Exception?>(logged => ReferenceEquals(logged, failure)),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Cancellation_propagates_with_the_original_token_and_without_storage_logging()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException(cancellation.Token);
        var logger = new Mock<ILogger<StockController>>();
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.GetMovementAsync("stock-user", It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Guid>(), cancellation.Token)).ThrowsAsync(failure);

        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Controller(bridge.Object, logger.Object).GetMovement(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));

        observed.Should().BeSameAs(failure);
        observed.CancellationToken.Should().Be(cancellation.Token);
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unexpected_aggregate_without_database_failure_propagates_unchanged()
    {
        var failure = new AggregateException(new InvalidOperationException("private-programming-detail"));
        var logger = new Mock<ILogger<StockController>>();

        (await Assert.ThrowsAsync<AggregateException>(() => Read(Failing(failure, logger.Object)))).Should().BeSameAs(failure);

        logger.VerifyNoOtherCalls();
    }

    private static StockController Failing(Exception failure, ILogger<StockController>? logger = null, IServiceProvider? services = null)
    {
        var bridge = new Mock<IStockBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.GetMovementAsync("stock-user", It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Guid>(), CancellationToken.None)).ThrowsAsync(failure);
        return Controller(bridge.Object, logger, services);
    }

    private static StockController Controller(IStockBridge bridge, ILogger<StockController>? logger = null,
        IServiceProvider? services = null, string? userId = "stock-user")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(userId is null ? [] : [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };
        if (services is not null) context.RequestServices = services;
        return new(bridge, logger ?? Mock.Of<ILogger<StockController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static Task<IActionResult> Read(StockController controller)
        => controller.GetMovement(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

    private sealed class StorageFailure(string message) : DbException(message) { }
}
