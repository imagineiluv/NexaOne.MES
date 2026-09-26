using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NexaFramework.Service;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Hr;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class HumanResourcesControllerTests
{
    [Fact]
    public async Task Report_and_export_forward_the_exact_scope_and_filters()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var employee = Guid.NewGuid(); var project = Guid.NewGuid(); var task = Guid.NewGuid();
        var query = new HrTimeReportQuery(new(2026, 9, 1), new(2026, 9, 30), employee,
            project, task, HrTimeApprovalFilter.Approved);
        var report = new HrTimeReport(new("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")),
            query, DateTimeOffset.UtcNow, 0, []);
        var export = new HrTimeReportCsvExport("report.csv", "text/csv; charset=utf-8", [1, 2, 3], 0);
        var bridge = new Mock<IHumanResourcesBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.BuildTimeReportAsync("hr-user", tenant, organization, query,
            CancellationToken.None)).ReturnsAsync(report);
        bridge.Setup(value => value.ExportTimeReportCsvAsync("hr-user", tenant, organization, query,
            CancellationToken.None)).ReturnsAsync(export);
        var controller = Controller(bridge.Object);

        (await controller.Report(tenant, organization, query.Start, query.End, employee, project,
            task, query.Approval)).Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(report);
        var file = (await controller.ExportReport(tenant, organization, query.Start, query.End,
            employee, project, task, query.Approval)).Should().BeOfType<FileContentResult>().Which;
        file.FileContents.Should().Equal(export.Content);
        file.ContentType.Should().Be(export.ContentType);
        file.FileDownloadName.Should().Be(export.FileName);
        bridge.VerifyAll();
    }

    [Theory]
    [InlineData("INVALID_BUSINESS_INPUT", 400)]
    [InlineData("HR_TIME_REPORT_TOO_LARGE", 413)]
    [InlineData("TIME_ENTRY_NOT_FOUND", 404)]
    [InlineData("STORAGE_CONTRACT_VIOLATION", 409)]
    public async Task Report_maps_business_failures(string code, int status)
    {
        var bridge = new Mock<IHumanResourcesBridge>(MockBehavior.Strict);
        bridge.Setup(value => value.BuildTimeReportAsync("hr-user", It.IsAny<Guid>(),
            It.IsAny<Guid>(), It.IsAny<HrTimeReportQuery>(), CancellationToken.None))
            .ThrowsAsync(new BusinessException(code));
        var result = (await Controller(bridge.Object).Report(Guid.NewGuid(), Guid.NewGuid(),
            new(2026, 9, 1), new(2026, 9, 30), null, null, null))
            .Should().BeOfType<ObjectResult>().Which;
        result.StatusCode.Should().Be(status);
        JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task Missing_identity_is_unauthorized_and_denied_scope_is_forbidden()
    {
        var bridge = new Mock<IHumanResourcesBridge>(MockBehavior.Strict);
        (await Controller(bridge.Object, null).Report(Guid.NewGuid(), Guid.NewGuid(),
            new(2026, 9, 1), new(2026, 9, 30), null, null, null))
            .Should().BeOfType<UnauthorizedResult>();
        bridge.Setup(value => value.BuildTimeReportAsync("hr-user", It.IsAny<Guid>(),
            It.IsAny<Guid>(), It.IsAny<HrTimeReportQuery>(), CancellationToken.None))
            .ThrowsAsync(new BusinessException("BUSINESS_ACCESS_DENIED"));
        (await Controller(bridge.Object).Report(Guid.NewGuid(), Guid.NewGuid(),
            new(2026, 9, 1), new(2026, 9, 30), null, null, null))
            .Should().BeOfType<ForbidResult>();
    }

    private static HumanResourcesController Controller(IHumanResourcesBridge bridge,
        string? userId = "hr-user")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(userId is null ? [] :
                [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };
        return new(bridge, Mock.Of<ILogger<HumanResourcesController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}
