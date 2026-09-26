using System.Security.Claims;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaOne.Server.Components.Pages;
using NexaOne.ServiceContracts.Hr;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class HrTimeReportPageTests : BunitContext
{
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Employee = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly List<string> _paths = [];

    public HrTimeReportPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = this.AddAuthorization();
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "report-user"));
        auth.SetAuthorized("report-user");
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new UiTextService());
        var scope = new BusinessMembership(Tenant, Organization, "report-user", Employee,
            true, 1, ["hr.time.read"]);
        _api.Setup(value => value.ReadInventoryAsync<BusinessPage<BusinessMembership>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) =>
            {
                _paths.Add(path);
                return Task.FromResult<(BusinessPage<BusinessMembership>?, int, string?, string?)>(
                    (new([scope], 1), 200, null, null));
            });
    }

    [Fact]
    public void Report_page_selects_scope_applies_filters_and_renders_rows()
    {
        var row = new HrTimeReportRow(Guid.NewGuid(), Employee,
            new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            new(2026, 9, 1, 11, 30, 0, TimeSpan.Zero), TimeSpan.FromHours(2.5).Ticks,
            null, null, null, null, "Assembly support");
        _api.Setup(value => value.ReadInventoryAsync<HrTimeReport>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) =>
            {
                _paths.Add(path);
                var query = new HrTimeReportQuery(new(2026, 9, 1), new(2026, 9, 30),
                    Employee, Approval: HrTimeApprovalFilter.Unsubmitted);
                return Task.FromResult<(HrTimeReport?, int, string?, string?)>((new(
                    new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), query,
                    new(2026, 9, 30, 1, 0, 0, TimeSpan.Zero), row.DurationTicks, [row]),
                    200, null, null));
            });
        var cut = Render<HostHrTimeReport>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#hr-report-start").Change("2026-09-01");
        cut.Find("#hr-report-end").Change("2026-09-30");
        cut.Find("#hr-report-approval").Change("Unsubmitted");
        cut.Find("#hr-report-employee").Change(Employee.ToString("D"));
        cut.Find("#hr-report-run").Click();

        cut.WaitForAssertion(() => cut.Find("[data-report-row]").TextContent
            .Should().Contain("Assembly support").And.Contain("2.5"));
        _paths.Last().Should().Be($"api/v1/hr/{Tenant:D}/{Organization:D}/time/report?" +
            $"start=2026-09-01&end=2026-09-30&approval=Unsubmitted&employeeId={Employee:D}");
        cut.Find("#hr-report-export").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Csv_export_uses_the_successfully_loaded_query_after_draft_filters_change()
    {
        var query = new HrTimeReportQuery(new(2026, 9, 1), new(2026, 9, 30),
            Employee, Approval: HrTimeApprovalFilter.Unsubmitted);
        var row = new HrTimeReportRow(Guid.NewGuid(), Employee,
            new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1).Ticks,
            null, null, null, null, "Assembly support");
        _api.Setup(value => value.ReadInventoryAsync<HrTimeReport>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new(
                new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), query,
                new(2026, 9, 30, 1, 0, 0, TimeSpan.Zero), row.DurationTicks, [row]),
                200, null, null));
        var exportPath = $"api/v1/hr/{Tenant:D}/{Organization:D}/time/report/export.csv?" +
            $"start=2026-09-01&end=2026-09-30&approval=Unsubmitted&employeeId={Employee:D}";
        _api.Setup(value => value.DownloadInventoryFileAsync(exportPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Encoding.UTF8.GetBytes("entry_id\r\n"), "hr-time-report.csv", "text/csv", 200, null, null));
        var cut = Render<HostHrTimeReport>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#hr-report-start").Change("2026-09-01");
        cut.Find("#hr-report-end").Change("2026-09-30");
        cut.Find("#hr-report-approval").Change("Unsubmitted");
        cut.Find("#hr-report-employee").Change(Employee.ToString("D"));
        cut.Find("#hr-report-run").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-report-row]").Should().ContainSingle());

        cut.Find("#hr-report-start").Change("2026-09-15");
        cut.Find("#hr-report-employee").Change("");
        cut.Find("#hr-report-export").Click();

        cut.WaitForAssertion(() => JSInterop.Invocations.Should()
            .ContainSingle(invocation => invocation.Identifier == "nxDownloadStream"));
        _api.Verify(value => value.DownloadInventoryFileAsync(exportPath,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Invalid_identifier_stays_client_side_and_explains_recovery()
    {
        var cut = Render<HostHrTimeReport>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#hr-report-employee").Change("not-a-guid");
        cut.Find("#hr-report-run").Click();

        cut.Find("[role=alert]").TextContent.Should().Contain("올바른 GUID");
        _api.Invocations.Count(invocation => invocation.Method.Name == nameof(IApiClient.ReadInventoryAsync)
            && invocation.Method.GetGenericArguments()[0] == typeof(HrTimeReport)).Should().Be(0);
    }

    [Fact]
    public void Empty_scope_result_is_a_success_state()
    {
        _api.Reset();
        _api.Setup(value => value.ReadInventoryAsync<BusinessPage<BusinessMembership>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new([], 0), 200, null, null));
        var cut = Render<HostHrTimeReport>();
        cut.WaitForAssertion(() => cut.Find(".hrr-empty").TextContent.Should().Contain("조직이 없습니다"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }
}
