using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Crm;
using NexaFramework.Service.Projects;
using NexaOne.Server.Components.Pages;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;
using ProjectRecord = NexaFramework.Service.Projects.Project;

namespace NexaOne.ServerTests;

public sealed class CrmWorkspaceTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");

    public CrmWorkspaceTests()
    {
        var authorization = this.AddAuthorization();
        authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, "operator"));
        authorization.SetAuthorized("operator");
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new UiTextService());
        Read(_ => new BusinessPage<BusinessMembership>([], 0));
        Read(_ => new CrmCustomerEnrollmentPage([], 0));
        Read(_ => new CrmPage<Pipeline>([], 0));
        Read(_ => new CrmPage<Deal>([], 0));
        Read(_ => new WorkPage<ProjectRecord>([], 0));
        Read(_ => new WorkPage<Team>([], 0));
    }

    [Fact]
    public void No_scopes_is_a_successful_empty_state()
    {
        var cut = Render<HostCrmWorkspace>();

        cut.WaitForAssertion(() => cut.Find("#crm-scopes [data-empty]").TextContent.Should().Contain("접근 가능한"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
        Paths<BusinessPage<BusinessMembership>>().Should().Equal("api/v1/crm/scopes/me?offset=0&limit=50");
    }

    [Fact]
    public void Read_scope_renders_only_server_returned_deals_and_reads_selected_detail()
    {
        var scope = Scope("crm.read", "crm.deal.assigned");
        Read(_ => new BusinessPage<BusinessMembership>([scope], 1));
        var visible = new Deal(Guid.NewGuid(), Business(), Guid.NewGuid(), Guid.NewGuid(), "Visible deal", 3, null)
        { AssignedEmployeeIds = [scope.BusinessUserId], CreatedByUserId = "another" };
        Read(path => new CrmPage<Deal>(path.Contains("deals?", StringComparison.Ordinal) ? [visible] : [], 1));
        _api.Setup(api => api.ReadInventoryAsync<Deal>(It.Is<string>(path => path.EndsWith($"deals/{visible.Id:D}", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((visible, 200, null, null));

        var cut = Render<HostCrmWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.FindAll("#crm-deals [data-select]").Should().ContainSingle());

        cut.Markup.Should().Contain("Visible deal").And.NotContain("Hidden deal");
        cut.Find("#crm-deals [data-select]").Click();
        cut.WaitForAssertion(() => cut.Find("#crm-details").TextContent.Should().Contain("Visible deal"));
        Paths<CrmPage<Deal>>().Should().ContainSingle(path => path == $"api/v1/crm/{Tenant:D}/{Organization:D}/deals?offset=0&limit=50");
        Paths<Deal>().Should().ContainSingle(path => path.EndsWith($"deals/{visible.Id:D}", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_only_scope_does_not_issue_deal_or_customer_reads()
    {
        var scope = Scope("crm.project.read", "crm.project.assigned");
        Read(_ => new BusinessPage<BusinessMembership>([scope], 1));
        var project = new ProjectRecord(Guid.NewGuid(), Business(), Guid.NewGuid(), "operator",
            new ProjectInput("Visible project"), new ProjectLinks(null, [new ProjectMember(scope.BusinessUserId)], []));
        Read(_ => new WorkPage<ProjectRecord>([project], 1));

        var cut = Render<HostCrmWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#crm-projects").TextContent.Should().Contain("Visible project"));

        cut.FindAll("#crm-deals").Should().BeEmpty();
        cut.FindAll("#crm-customers").Should().BeEmpty();
        Paths<CrmPage<Deal>>().Should().BeEmpty();
        Paths<CrmCustomerEnrollmentPage>().Should().BeEmpty();
    }

    [Fact]
    public void Enrollment_grant_posts_customer_and_refreshes_the_customer_index()
    {
        var scope = Scope("crm.read", "crm.customer.enroll");
        Read(_ => new BusinessPage<BusinessMembership>([scope], 1));
        var enrolled = new CrmCustomerEnrollment(Guid.NewGuid(), Guid.NewGuid(), "CUST-1", "Customer One", "operator", DateTimeOffset.UtcNow);
        _api.Setup(api => api.WriteInventoryAsync<CrmCustomerEnrollment>(HttpMethod.Post,
                $"api/v1/crm/{Tenant:D}/{Organization:D}/customer-enrollments",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((enrolled, 200, null, null));

        var cut = Render<HostCrmWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#crm-customer-id").Change("CUST-1");
        cut.Find("#crm-enroll").Click();

        cut.WaitForAssertion(() => cut.Find("#crm-customers").TextContent.Should().Contain("CUST-1"));
        _api.Verify(api => api.WriteInventoryAsync<CrmCustomerEnrollment>(HttpMethod.Post,
            $"api/v1/crm/{Tenant:D}/{Organization:D}/customer-enrollments",
            It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
    }

    private void Read<T>(Func<string, T> response) where T : class
        => _api.Setup(api => api.ReadInventoryAsync<T>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult<(T?, int, string?, string?)>((response(path), 200, null, null)));

    private string[] Paths<T>() where T : class => _api.Invocations
        .Where(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync) && call.Method.GetGenericArguments()[0] == typeof(T))
        .Select(call => (string)call.Arguments[0]).ToArray();

    private static BusinessMembership Scope(params string[] grants)
        => new(Tenant, Organization, "operator", Guid.Parse("30000000-0000-0000-0000-000000000001"), true, 1, grants);
    private static BusinessScope Business() => new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D"));
}
