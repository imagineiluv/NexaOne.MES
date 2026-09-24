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

        cut.WaitForAssertion(() => cut.Find("#crm-write-status").TextContent.Should().Contain("CUST-1"));
        _api.Verify(api => api.WriteInventoryAsync<CrmCustomerEnrollment>(HttpMethod.Post,
            $"api/v1/crm/{Tenant:D}/{Organization:D}/customer-enrollments",
            It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
        Paths<CrmCustomerEnrollmentPage>().Count(path => path.Contains("customer-enrollments?", StringComparison.Ordinal))
            .Should().BeGreaterThan(1);
    }

    [Fact]
    public void Pipeline_manager_creates_a_pipeline_and_refreshes_the_indexes()
    {
        var scope = Scope("crm.read", "crm.pipeline.manage");
        Read(_ => new BusinessPage<BusinessMembership>([scope], 1));
        var pipeline = new Pipeline(Guid.NewGuid(), Business(), Guid.NewGuid(), "Sales", "Primary");
        var created = new PipelineDetails(pipeline,
            [new PipelineStage(Guid.NewGuid(), Business(), pipeline.Id, "Lead", null, 1)]);
        _api.Setup(api => api.WriteInventoryAsync<PipelineDetails>(HttpMethod.Post,
                $"api/v1/crm/{Tenant:D}/{Organization:D}/pipelines", It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((created, 200, null, null));

        var cut = Render<HostCrmWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#crm-pipeline-name").Change("Sales");
        cut.Find("#crm-pipeline-description").Change("Primary");
        cut.Find("#crm-pipeline-stages").Change("Lead, Won");
        cut.Find("#crm-save-pipeline").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pipeline을 저장했습니다"));
        _api.Verify(api => api.WriteInventoryAsync<PipelineDetails>(HttpMethod.Post,
            $"api/v1/crm/{Tenant:D}/{Organization:D}/pipelines", It.IsAny<object>(), "operator",
            It.IsAny<CancellationToken>()), Times.Once);
        Paths<CrmPage<Pipeline>>().Count(path => path.Contains("pipelines?", StringComparison.Ordinal)).Should().BeGreaterThan(1);
    }

    [Fact]
    public void Pipeline_manager_updates_and_deletes_a_selected_pipeline()
    {
        var scope = Scope("crm.read", "crm.pipeline.manage");
        Read(_ => new BusinessPage<BusinessMembership>([scope], 1));
        var pipeline = new Pipeline(Guid.NewGuid(), Business(), Guid.NewGuid(), "Sales", null);
        var details = new PipelineDetails(pipeline,
            [new PipelineStage(Guid.NewGuid(), Business(), pipeline.Id, "Lead", null, 1)]);
        Read(_ => new CrmPage<Pipeline>([pipeline], 1));
        _api.Setup(api => api.ReadInventoryAsync<PipelineDetails>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((details, 200, null, null));
        _api.Setup(api => api.WriteInventoryAsync<PipelineDetails>(HttpMethod.Put,
                It.Is<string>(path => path.EndsWith($"pipelines/{pipeline.Id:D}", StringComparison.Ordinal)), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((details with { Pipeline = pipeline with { Name = "Updated", Version = Guid.NewGuid() } }, 200, null, null));
        _api.Setup(api => api.WriteInventoryAsync<Dictionary<string, object>>(HttpMethod.Delete,
                It.Is<string>(path => path.Contains($"pipelines/{pipeline.Id:D}?version=", StringComparison.Ordinal)), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((new Dictionary<string, object> { ["id"] = pipeline.Id }, 200, null, null));

        var cut = Render<HostCrmWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#crm-pipelines [data-select]").Should().NotBeNull());
        cut.Find("#crm-pipelines [data-select]").Click();
        cut.Find("#crm-pipeline-name").Change("Updated");
        cut.Find("#crm-save-pipeline").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pipeline을 저장했습니다"));

        cut.Find("#crm-pipelines [data-select]").Click();
        cut.Find("#crm-delete-pipeline").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pipeline을 삭제했습니다"));
        _api.Verify(api => api.WriteInventoryAsync<PipelineDetails>(HttpMethod.Put, It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(api => api.WriteInventoryAsync<Dictionary<string, object>>(HttpMethod.Delete, It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Deal_manager_updates_moves_and_deletes_a_selected_visible_deal()
    {
        var scope = Scope("crm.read", "crm.pipeline.manage", "crm.deal.manage", "crm.deal.delete", "crm.deal.all");
        Read(_ => new BusinessPage<BusinessMembership>([scope], 1));
        var pipeline = new Pipeline(Guid.NewGuid(), Business(), Guid.NewGuid(), "Sales", null);
        var lead = new PipelineStage(Guid.NewGuid(), Business(), pipeline.Id, "Lead", null, 1);
        var won = new PipelineStage(Guid.NewGuid(), Business(), pipeline.Id, "Won", null, 2);
        var details = new PipelineDetails(pipeline, [lead, won]);
        var deal = new Deal(Guid.NewGuid(), Business(), Guid.NewGuid(), lead.Id, "Visible", 2, null);
        Read(_ => new CrmPage<Pipeline>([pipeline], 1));
        Read(_ => new CrmPage<Deal>([deal], 1));
        _api.Setup(api => api.ReadInventoryAsync<PipelineDetails>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((details, 200, null, null));
        _api.Setup(api => api.ReadInventoryAsync<Deal>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((deal, 200, null, null));
        _api.Setup(api => api.WriteInventoryAsync<Deal>(HttpMethod.Put,
                It.Is<string>(path => path.EndsWith($"deals/{deal.Id:D}", StringComparison.Ordinal)), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((deal with { Title = "Updated", Version = Guid.NewGuid() }, 200, null, null));
        _api.Setup(api => api.WriteInventoryAsync<Deal>(HttpMethod.Post,
                $"api/v1/crm/{Tenant:D}/{Organization:D}/deals", It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((deal, 200, null, null));
        _api.Setup(api => api.WriteInventoryAsync<Deal>(HttpMethod.Post,
                It.Is<string>(path => path.EndsWith($"deals/{deal.Id:D}/move", StringComparison.Ordinal)), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((deal with { StageId = won.Id, Version = Guid.NewGuid() }, 200, null, null));
        _api.Setup(api => api.WriteInventoryAsync<Dictionary<string, object>>(HttpMethod.Delete,
                It.Is<string>(path => path.Contains($"deals/{deal.Id:D}?version=", StringComparison.Ordinal)), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((new Dictionary<string, object> { ["id"] = deal.Id }, 200, null, null));

        var cut = Render<HostCrmWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#crm-pipelines [data-select]").Should().NotBeNull());
        cut.Find("#crm-pipelines [data-select]").Click();
        cut.WaitForAssertion(() => cut.Find("#crm-deals [data-select]").Should().NotBeNull());
        cut.Find("#crm-deal-title").Change("Created");
        cut.Find("#crm-save-deal").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("거래를 저장했습니다"));

        cut.Find("#crm-deals [data-select]").Click();
        cut.Find("#crm-deal-title").Change("Updated");
        cut.Find("#crm-save-deal").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("거래를 저장했습니다"));

        cut.Find("#crm-deals [data-select]").Click();
        cut.Find("#crm-deal-stage").Change(won.Id.ToString("D"));
        cut.Find("#crm-move-deal").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("거래 단계를 이동했습니다"));

        cut.Find("#crm-deals [data-select]").Click();
        cut.Find("#crm-delete-deal").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("거래를 삭제했습니다"));
        _api.Verify(api => api.WriteInventoryAsync<Deal>(HttpMethod.Put, It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(api => api.WriteInventoryAsync<Deal>(HttpMethod.Post, $"api/v1/crm/{Tenant:D}/{Organization:D}/deals", It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(api => api.WriteInventoryAsync<Deal>(HttpMethod.Post, It.Is<string>(path => path.EndsWith("/move", StringComparison.Ordinal)), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(api => api.WriteInventoryAsync<Dictionary<string, object>>(HttpMethod.Delete, It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
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
