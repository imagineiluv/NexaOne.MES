using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class FinancialReportControllerTests
{
    [Fact]
    public async Task Build_and_csv_forward_the_inclusive_period_and_scope()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var period = new FinancialReportPeriod(new(2026, 9, 1), new(2026, 9, 30));
        var report = new FinancialReport(new("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")),
            period, new(2026, 9, 30, 1, 2, 3, TimeSpan.Zero),
            [new("KRW", 1, 10m, 4m, 6m, 0, 0m, 0, 0m, 0m, 0m)]);
        const string csv = "currency,invoice_count\r\nKRW,1\r\n";
        var bridge = new Mock<IFinancialReportBridge>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        bridge.Setup(x => x.BuildAsync("report-user", tenant, organization, period, cancellation.Token))
            .ReturnsAsync(report);
        bridge.Setup(x => x.ExportCsvAsync("report-user", tenant, organization, period, cancellation.Token))
            .ReturnsAsync(csv);
        var cashPeriod = new CashFlowReportPeriod(period.Start, period.End);
        var cash = new CashFlowReport(report.Scope, cashPeriod, report.GeneratedAt,
            [new("KRW", 1, 4m)]);
        const string cashCsv = "currency,payment_count,received\r\nKRW,1,4\r\n";
        bridge.Setup(x => x.BuildCashFlowAsync("report-user", tenant, organization, cashPeriod, cancellation.Token))
            .ReturnsAsync(cash);
        bridge.Setup(x => x.ExportCashFlowCsvAsync("report-user", tenant, organization, cashPeriod, cancellation.Token))
            .ReturnsAsync(cashCsv);
        var controller = Controller(bridge.Object);

        (await controller.Build(tenant, organization, period.Start, period.End, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(report);
        var file = (await controller.ExportCsv(tenant, organization, period.Start, period.End,
            cancellation.Token)).Should().BeOfType<FileContentResult>().Which;
        Encoding.UTF8.GetString(file.FileContents).Should().Be(csv);
        file.ContentType.Should().Be("text/csv; charset=utf-8");
        file.FileDownloadName.Should().Be("financial-report-20260901-20260930.csv");
        (await controller.BuildCashFlow(tenant, organization, period.Start, period.End, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(cash);
        var cashFile = (await controller.ExportCashFlowCsv(tenant, organization, period.Start, period.End,
            cancellation.Token)).Should().BeOfType<FileContentResult>().Which;
        Encoding.UTF8.GetString(cashFile.FileContents).Should().Be(cashCsv);
        cashFile.FileDownloadName.Should().Be("cash-flow-report-20260901-20260930.csv");
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_principal_and_report_failures_follow_read_endpoint_conventions()
    {
        var anonymous = Controller(Mock.Of<IFinancialReportBridge>(), null);
        (await anonymous.Build(Guid.NewGuid(), Guid.NewGuid(), new(2026, 9, 1), new(2026, 9, 30),
            CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();

        foreach (var (code, status) in new[]
        {
            ("INVALID_BUSINESS_INPUT", 400), ("FINANCIAL_REPORT_TOO_LARGE", 413),
            ("STORAGE_CONTRACT_VIOLATION", 409), ("REPORT_SNAPSHOT_NOT_FOUND", 404)
        })
        {
            var result = (await Failing(new BusinessException(code)).Build(Guid.NewGuid(), Guid.NewGuid(),
                new(2026, 9, 1), new(2026, 9, 30), CancellationToken.None))
                .Should().BeOfType<ObjectResult>().Which;
            result.StatusCode.Should().Be(status);
            JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString().Should().Be(code);
        }
        (await Failing(new BusinessException("BUSINESS_ACCESS_DENIED")).Build(Guid.NewGuid(), Guid.NewGuid(),
            new(2026, 9, 1), new(2026, 9, 30), CancellationToken.None)).Should().BeOfType<ForbidResult>();
        var corrupt = (await Failing(new InvalidDataException("corrupt snapshot")).Build(
            Guid.NewGuid(), Guid.NewGuid(), new(2026, 9, 1), new(2026, 9, 30),
            CancellationToken.None)).Should().BeOfType<ObjectResult>().Which;
        corrupt.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Snapshot_endpoints_forward_identity_filters_comparison_download_and_audit()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var snapshotId = Guid.NewGuid();
        var start = new DateOnly(2026, 9, 1); var end = new DateOnly(2026, 9, 30);
        var at = new DateTimeOffset(2026, 9, 30, 1, 2, 3, TimeSpan.Zero);
        const string hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var summary = new FinancialReportSnapshotSummary(snapshotId,
            FinancialReportSnapshotKind.CashFlow, start, end, at, Guid.NewGuid().ToString("D"), hash);
        var report = new CashFlowReport(new("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")),
            new(start, end), at, [new("KRW", 1, 40m)]);
        var snapshot = new FinancialReportSnapshot(summary, null, report);
        var comparison = new FinancialReportSnapshotComparison(snapshotId, true, hash, hash, at);
        var download = new FinancialReportSnapshotDownload($"cash-flow-report-{snapshotId:D}.csv", "csv");
        var audit = new FinancialReportSnapshotAuditEntry(Guid.NewGuid(), snapshotId,
            FinancialReportSnapshotAuditAction.Created, summary.CreatedBy, at, hash, null);
        var bridge = new Mock<IFinancialReportBridge>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        bridge.Setup(x => x.CreateSnapshotAsync("report-user", tenant, organization, snapshotId,
            FinancialReportSnapshotKind.CashFlow, start, end, cancellation.Token)).ReturnsAsync(snapshot);
        bridge.Setup(x => x.ListSnapshotsAsync("report-user", tenant, organization,
            FinancialReportSnapshotKind.CashFlow, 2, 10, cancellation.Token))
            .ReturnsAsync(new BusinessPage<FinancialReportSnapshotSummary>([summary], 1));
        bridge.Setup(x => x.GetSnapshotAsync("report-user", tenant, organization, snapshotId,
            cancellation.Token)).ReturnsAsync(snapshot);
        bridge.Setup(x => x.RegenerateSnapshotAsync("report-user", tenant, organization, snapshotId,
            cancellation.Token)).ReturnsAsync(comparison);
        bridge.Setup(x => x.DownloadSnapshotAsync("report-user", tenant, organization, snapshotId,
            cancellation.Token)).ReturnsAsync(download);
        bridge.Setup(x => x.ListSnapshotAuditAsync("report-user", tenant, organization, snapshotId,
            1, 20, cancellation.Token))
            .ReturnsAsync(new BusinessPage<FinancialReportSnapshotAuditEntry>([audit], 1));
        var controller = Controller(bridge.Object);

        var request = new CreateFinancialReportSnapshotRequest(snapshotId,
            FinancialReportSnapshotKind.CashFlow, start, end);
        (await controller.CreateSnapshot(tenant, organization, request, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(snapshot);
        (await controller.ListSnapshots(tenant, organization, cancellation.Token,
            FinancialReportSnapshotKind.CashFlow, 2, 10))
            .Should().BeOfType<OkObjectResult>();
        (await controller.GetSnapshot(tenant, organization, snapshotId, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(snapshot);
        (await controller.RegenerateSnapshot(tenant, organization, snapshotId, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(comparison);
        var file = (await controller.DownloadSnapshot(tenant, organization, snapshotId,
            cancellation.Token)).Should().BeOfType<FileContentResult>().Which;
        Encoding.UTF8.GetString(file.FileContents).Should().Be("csv");
        file.FileDownloadName.Should().Be(download.FileName);
        (await controller.ListSnapshotAudit(tenant, organization, snapshotId,
            cancellation.Token, 1, 20)).Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    private static FinancialReportController Failing(Exception failure)
    {
        var bridge = new Mock<IFinancialReportBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.BuildAsync("report-user", It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<FinancialReportPeriod>(), CancellationToken.None)).ThrowsAsync(failure);
        return Controller(bridge.Object);
    }

    private static FinancialReportController Controller(IFinancialReportBridge bridge,
        string? userId = "report-user")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(userId is null ? [] :
                [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };
        return new(bridge, Mock.Of<ILogger<FinancialReportController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}
