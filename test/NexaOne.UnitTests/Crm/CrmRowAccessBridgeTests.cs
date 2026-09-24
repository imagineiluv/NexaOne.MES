using Microsoft.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Crm;
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
            ["admin"] = ["crm.read", "crm.pipeline.manage", "crm.deal.manage", "crm.deal.delete", "crm.deal.all"],
            ["creator"] = ["crm.read", "crm.deal.manage", "crm.deal.created"],
            ["assignee"] = ["crm.read", "crm.deal.assigned"],
            ["outsider"] = ["crm.read", "crm.deal.assigned"],
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
