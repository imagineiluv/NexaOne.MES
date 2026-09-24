using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class BusinessReportControllerTests
{
    [Fact]
    public async Task Catalog_build_and_export_forward_scope_key_period_and_unit()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var period = new BusinessReportPeriod(new(2026, 9, 1), new(2026, 9, 30));
        var definition = new BusinessReportDefinition("erp.financial-activity",
            "financial-report.read", [BusinessReportUnit.Total, BusinessReportUnit.Day]);
        var report = new BusinessReport(new("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")),
            definition.Key, period, BusinessReportUnit.Day,
            new(2026, 9, 30, 1, 2, 3, TimeSpan.Zero),
            [new(new(2026, 9, 10), new(2026, 9, 10), "KRW", "invoice.invoiced", 1, 100m)]);
        const string csv = "bucket_start,value\r\n2026-09-10,100\r\n";
        using var cancellation = new CancellationTokenSource();
        var bridge = new Mock<IBusinessReportBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.ListAsync("report-user", tenant, organization, cancellation.Token))
            .ReturnsAsync([definition]);
        bridge.Setup(x => x.BuildAsync("report-user", tenant, organization, definition.Key,
            period, BusinessReportUnit.Day, cancellation.Token)).ReturnsAsync(report);
        bridge.Setup(x => x.ExportCsvAsync("report-user", tenant, organization, definition.Key,
            period, BusinessReportUnit.Day, cancellation.Token)).ReturnsAsync(csv);
        var controller = Controller(bridge.Object);

        (await controller.List(tenant, organization, cancellation.Token))
            .Should().BeOfType<OkObjectResult>();
        (await controller.Build(tenant, organization, definition.Key, period.Start, period.End,
            BusinessReportUnit.Day, cancellation.Token)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(report);
        var file = (await controller.Export(tenant, organization, definition.Key,
            period.Start, period.End, BusinessReportUnit.Day, cancellation.Token))
            .Should().BeOfType<FileContentResult>().Which;
        Encoding.UTF8.GetString(file.FileContents).Should().Be(csv);
        file.FileDownloadName.Should().Be("erp.financial-activity-20260901-20260930-day.csv");
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_principal_and_report_failures_follow_read_endpoint_conventions()
    {
        var anonymous = Controller(Mock.Of<IBusinessReportBridge>(), null);
        (await anonymous.Build(Guid.NewGuid(), Guid.NewGuid(), "erp.financial-activity",
            new(2026, 9, 1), new(2026, 9, 30), BusinessReportUnit.Total, CancellationToken.None))
            .Should().BeOfType<UnauthorizedResult>();

        foreach (var (code, status) in new[]
        {
            ("INVALID_BUSINESS_REPORT_KEY", 400), ("BUSINESS_REPORT_UNIT_NOT_SUPPORTED", 400),
            ("BUSINESS_REPORT_TOO_LARGE", 413), ("BUSINESS_REPORT_NOT_FOUND", 404),
            ("STORAGE_CONTRACT_VIOLATION", 409)
        })
        {
            var result = (await Failing(new BusinessException(code)).Build(Guid.NewGuid(), Guid.NewGuid(),
                "erp.financial-activity", new(2026, 9, 1), new(2026, 9, 30),
                BusinessReportUnit.Total, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which;
            result.StatusCode.Should().Be(status);
            JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString().Should().Be(code);
        }
        (await Failing(new BusinessException("BUSINESS_ACCESS_DENIED")).Build(Guid.NewGuid(), Guid.NewGuid(),
            "erp.financial-activity", new(2026, 9, 1), new(2026, 9, 30),
            BusinessReportUnit.Total, CancellationToken.None)).Should().BeOfType<ForbidResult>();
        var corrupt = (await Failing(new InvalidDataException("corrupt source")).Build(
            Guid.NewGuid(), Guid.NewGuid(), "erp.financial-activity", new(2026, 9, 1),
            new(2026, 9, 30), BusinessReportUnit.Total, CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which;
        corrupt.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    private static BusinessReportController Failing(Exception failure)
    {
        var bridge = new Mock<IBusinessReportBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.BuildAsync("report-user", It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<string>(), It.IsAny<BusinessReportPeriod>(), It.IsAny<BusinessReportUnit>(),
            CancellationToken.None)).ThrowsAsync(failure);
        return Controller(bridge.Object);
    }

    private static BusinessReportController Controller(IBusinessReportBridge bridge,
        string? userId = "report-user")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(userId is null ? [] :
                [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };
        return new(bridge, Mock.Of<ILogger<BusinessReportController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}
