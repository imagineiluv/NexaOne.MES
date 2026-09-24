using Microsoft.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Crm;
using NexaFramework.Service.Projects;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;
using NexaOne.UnitTests.TestInfrastructure;
using CrmModule = NexaOne.CRM.Module;

namespace NexaOne.UnitTests.Crm;

public sealed class CrmRowAccessBridgeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nexa-crm-{Guid.NewGuid():N}.db");
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _adminEmployee = Guid.NewGuid();
    private readonly Guid _creatorEmployee = Guid.NewGuid();
    private readonly Guid _assigneeEmployee = Guid.NewGuid();
    private readonly Guid _outsiderEmployee = Guid.NewGuid();

    [Fact]
    public async Task Deal_reads_apply_created_and_assigned_visibility_before_paging_and_get()
    {
        var memberships = Memberships();
        var module = Module(memberships.Object);
        Initialize(module);
        var bridge = module.GetCrmBridge();

        var pipeline = await bridge.CreatePipelineAsync("admin", _tenantId, _organizationId,
            new PipelineInput("Sales", Stages:
            [
                new StageInput(null, "Lead"),
                new StageInput(null, "Won"),
            ]));
        var creatorDeal = await bridge.CreateDealAsync("creator", _tenantId, _organizationId,
            new DealInput("Creator deal", 2, pipeline.Stages[0].Id));
        var assignedDeal = await bridge.CreateDealAsync("admin", _tenantId, _organizationId,
            new DealInput("Assigned deal", 3, pipeline.Stages[0].Id)
            {
                AssignedEmployeeIds = [_assigneeEmployee],
            });

        var created = await bridge.ListDealsAsync("creator", _tenantId, _organizationId, new DealQuery());
        created.Total.Should().Be(1);
        created.Items.Should().ContainSingle().Which.Id.Should().Be(creatorDeal.Id);

        var assigned = await bridge.ListDealsAsync("assignee", _tenantId, _organizationId, new DealQuery());
        assigned.Total.Should().Be(1);
        assigned.Items.Should().ContainSingle().Which.Id.Should().Be(assignedDeal.Id);

        var outsider = await bridge.ListDealsAsync("outsider", _tenantId, _organizationId, new DealQuery());
        outsider.Total.Should().Be(0);
        outsider.Items.Should().BeEmpty();

        var hidden = () => bridge.GetDealAsync("creator", _tenantId, _organizationId, assignedDeal.Id);
        await hidden.Should().ThrowAsync<BusinessException>().WithMessage("DEAL_NOT_FOUND");
    }

    [Fact]
    public async Task Narrow_deal_manager_cannot_replace_assignments()
    {
        var memberships = Memberships();
        var module = Module(memberships.Object);
        Initialize(module);
        var bridge = module.GetCrmBridge();
        var pipeline = await bridge.CreatePipelineAsync("admin", _tenantId, _organizationId,
            new PipelineInput("Sales", Stages: [new StageInput(null, "Lead")]));
        var deal = await bridge.CreateDealAsync("creator", _tenantId, _organizationId,
            new DealInput("Owned", 2, pipeline.Stages[0].Id));

        var update = () => bridge.UpdateDealAsync("creator", _tenantId, _organizationId,
            deal.Id, deal.Version, new DealInput("Owned", 3, pipeline.Stages[0].Id)
            {
                AssignedEmployeeIds = [_creatorEmployee],
            });

        await update.Should().ThrowAsync<BusinessException>().WithMessage("CRM_ACCESS_DENIED");
    }

    [Fact]
    public async Task Pipeline_stage_replacement_can_reorder_existing_stages_atomically()
    {
        var memberships = Memberships();
        var module = Module(memberships.Object);
        Initialize(module);
        var bridge = module.GetCrmBridge();
        var pipeline = await bridge.CreatePipelineAsync("admin", _tenantId, _organizationId,
            new PipelineInput("Sales", Stages:
            [
                new StageInput(null, "Lead"),
                new StageInput(null, "Won"),
            ]));

        var updated = await bridge.UpdatePipelineAsync("admin", _tenantId, _organizationId,
            pipeline.Pipeline.Id, pipeline.Pipeline.Version,
            new PipelineInput("Sales", Stages:
            [
                new StageInput(pipeline.Stages[1].Id, "Won"),
                new StageInput(pipeline.Stages[0].Id, "Lead"),
            ]), DealRemoval.RejectIfReferenced);

        updated.Stages.Select(stage => stage.Id).Should().Equal(
            pipeline.Stages[1].Id, pipeline.Stages[0].Id);
        updated.Stages.Select(stage => stage.Index).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Project_reads_apply_created_and_assigned_visibility_before_paging_and_get()
    {
        var memberships = Memberships();
        var module = Module(memberships.Object);
        Initialize(module);
        var bridge = module.GetCrmBridge();

        var creatorProject = await bridge.CreateProjectAsync("creator", _tenantId, _organizationId,
            new ProjectInput("Creator project"),
            new ProjectLinks(null, [new ProjectMember(_creatorEmployee)], []));
        var assignedProject = await bridge.CreateProjectAsync("admin", _tenantId, _organizationId,
            new ProjectInput("Assigned project"),
            new ProjectLinks(null, [new ProjectMember(_assigneeEmployee)], []));

        var created = await bridge.ListProjectsAsync("creator", _tenantId, _organizationId,
            new ProjectQuery(Limit: 1));
        created.Total.Should().Be(1);
        created.Items.Should().ContainSingle().Which.Id.Should().Be(creatorProject.Id);

        var assigned = await bridge.ListProjectsAsync("assignee", _tenantId, _organizationId,
            new ProjectQuery(Limit: 1));
        assigned.Total.Should().Be(1);
        assigned.Items.Should().ContainSingle().Which.Id.Should().Be(assignedProject.Id);

        var outsider = await bridge.ListProjectsAsync("outsider", _tenantId, _organizationId,
            new ProjectQuery(Limit: 1));
        outsider.Total.Should().Be(0);
        outsider.Items.Should().BeEmpty();

        var hidden = () => bridge.GetProjectAsync("creator", _tenantId, _organizationId, assignedProject.Id);
        await hidden.Should().ThrowAsync<BusinessException>().WithMessage("PROJECT_NOT_FOUND");
    }

    [Fact]
    public async Task Project_team_links_are_atomic_and_narrow_roles_cannot_replace_them()
    {
        var memberships = Memberships();
        var module = Module(memberships.Object);
        Initialize(module);
        var bridge = module.GetCrmBridge();

        var team = await bridge.CreateTeamAsync("admin", _tenantId, _organizationId,
            new TeamInput("Delivery", "DLV"), [new ProjectMember(_assigneeEmployee, true)]);
        var project = await bridge.CreateProjectAsync("creator", _tenantId, _organizationId,
            new ProjectInput("Restricted"),
            new ProjectLinks(null, [new ProjectMember(_creatorEmployee)], []));

        var denied = () => bridge.SetProjectLinksAsync("creator", _tenantId, _organizationId,
            project.Id, project.Version,
            new ProjectLinks(null, [new ProjectMember(_creatorEmployee)], [team.Id]));
        await denied.Should().ThrowAsync<BusinessException>().WithMessage("WORK_ACCESS_DENIED");

        var linked = await bridge.SetProjectLinksAsync("admin", _tenantId, _organizationId,
            project.Id, project.Version,
            new ProjectLinks(null, [new ProjectMember(_assigneeEmployee)], [team.Id]));
        linked.Links.TeamIds.Should().Equal(team.Id);
        linked.Links.Members.Should().ContainSingle().Which.EmployeeId.Should().Be(_assigneeEmployee);

        var deleteReferencedTeam = () => bridge.DeleteTeamAsync("admin", _tenantId, _organizationId,
            team.Id, team.Version);
        await deleteReferencedTeam.Should().ThrowAsync<BusinessException>().WithMessage("TEAM_IS_REFERENCED");

        var customerLink = () => bridge.SetProjectLinksAsync("admin", _tenantId, _organizationId,
            linked.Id, linked.Version, new ProjectLinks(Guid.NewGuid(), linked.Links.Members, linked.Links.TeamIds));
        await customerLink.Should().ThrowAsync<BusinessException>().WithMessage("CLIENT_NOT_FOUND");
    }

    private CrmModule Module(IBusinessMembershipBridge memberships) => new(new EesDataSource
    {
        Provider = new SqliteTestDatabaseProvider(),
        ConnectionString = $"Data Source={_path};Foreign Keys=True",
    }, memberships);

    private void Initialize(CrmModule module)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Foreign Keys=True");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        module.GetSqliteSchemaContribution().Apply(connection, transaction);
        transaction.Commit();
    }

    private Mock<IBusinessMembershipBridge> Memberships()
    {
        var employees = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["admin"] = _adminEmployee,
            ["creator"] = _creatorEmployee,
            ["assignee"] = _assigneeEmployee,
            ["outsider"] = _outsiderEmployee,
        };
        var permissions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["admin"] = ["crm.read", "crm.pipeline.manage", "crm.deal.manage", "crm.deal.delete", "crm.deal.all",
                "crm.project.read", "crm.project.manage", "crm.project.delete", "crm.project.link-customer", "crm.project.all",
                "crm.team.read", "crm.team.manage", "crm.team.delete"],
            ["creator"] = ["crm.read", "crm.deal.manage", "crm.deal.created",
                "crm.project.read", "crm.project.manage", "crm.project.created"],
            ["assignee"] = ["crm.read", "crm.deal.assigned", "crm.project.read", "crm.project.assigned"],
            ["outsider"] = ["crm.read", "crm.deal.assigned", "crm.project.read", "crm.project.assigned"],
        };
        var mock = new Mock<IBusinessMembershipBridge>();
        mock.Setup(value => value.GetAccessAsync(It.IsAny<string>(), _tenantId, _organizationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string user, Guid _, Guid _, CancellationToken _) =>
                employees.TryGetValue(user, out var employee)
                    ? new BusinessMembership(_tenantId, _organizationId, user, employee, true, 1, permissions[user])
                    : null);
        mock.Setup(value => value.GetActiveMemberInTransactionAsync(It.IsAny<System.Data.Common.DbTransaction>(),
                _tenantId, _organizationId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((System.Data.Common.DbTransaction _, Guid _, Guid _, Guid employee, CancellationToken _) =>
            {
                var pair = employees.SingleOrDefault(value => value.Value == employee);
                return pair.Key is null ? null
                    : new BusinessMembership(_tenantId, _organizationId, pair.Key, employee, true, 1, permissions[pair.Key]);
            });
        return mock;
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}
