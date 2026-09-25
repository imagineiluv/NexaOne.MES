using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ERP.Infrastructure;
using NexaOne.HR.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Hr;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class BillingTimeSourcePersistenceTests :
    IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Grants =
    [
        "billing.read", "billing.write", "hr.time.read", "hr.time.write", "hr.time.stop",
        "hr.time.record-manual", "hr.time.correct",
        "hr.timesheet.submit", "hr.timesheet.review"
    ];
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly Guid _project = Guid.NewGuid();
    private readonly EesDataSource _dataSource;
    private readonly BusinessMembershipBridge _memberships;
    private readonly TimeTrackingBridge _hr;
    private readonly BillingBridge _billing;

    public BillingTimeSourcePersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _dataSource = new() { Provider = new SqliteProvider(), ConnectionString = _connectionString };
        _memberships = new(_dataSource);
        var projects = new Mock<IBusinessProjectDirectory>(MockBehavior.Strict);
        projects.Setup(value => value.ProjectExistsInTransactionAsync(
                It.IsAny<System.Data.Common.DbTransaction>(), _tenant, _organization, _project,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var clock = new FixedHrClock(Now);
        _hr = new(_dataSource, _memberships, projects.Object, clock);
        _billing = new(_dataSource, _memberships, new BusinessMasterDirectory(_dataSource), clock,
            timeBilling: _hr);
        Execute("""
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID,CUSTOMER_NAME,IS_ACTIVE)
                VALUES ('TIME-CUSTOMER','Time customer',1);
            INSERT INTO SYS_ROLE (ROLE_ID,ROLE_NAME,PERMISSIONS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('TIME-ROLE','Time role','','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID,USER_NAME,PASSWORD_HASH,EMAIL,ROLE_ID,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('time-user','Time user','','','TIME-ROLE','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID,USER_NAME,PASSWORD_HASH,EMAIL,ROLE_ID,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('time-reviewer','Time reviewer','','','TIME-ROLE','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            """);
    }

    public async Task InitializeAsync()
    {
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "time-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "time-reviewer", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        Execute("""
            INSERT INTO CRM_PROJECT
                (TENANT_ID,ORGANIZATION_ID,PROJECT_ID,VERSION,CREATED_BY,NAME,DESCRIPTION,CODE,
                 PROJECT_STATUS,START_AT_TICKS,END_AT_TICKS,IS_BILLABLE,IS_PUBLIC,BUDGET,BUDGET_TYPE,CUSTOMER_ID)
            VALUES (@Tenant,@Organization,@Project,@Version,@CreatedBy,'Time project',NULL,'TIME',
                    'Open',NULL,NULL,1,1,NULL,'Hours',NULL)
            """, new { Tenant = _tenant.ToString("D"), Organization = _organization.ToString("D"),
                Project = _project.ToString("D"), Version = Guid.NewGuid().ToString("D"),
                CreatedBy = BusinessId("time-user").ToString("D") });
    }

    public Task DisposeAsync()
    {
        File.Delete(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Approved_time_entries_generate_employee_project_and_task_invoices_once()
    {
        var task = await _hr.CreateTaskAsync("time-user", _tenant, _organization,
            new("Implementation", _project));
        var employeeEntry = await Record(Now.AddHours(-7), Now.AddHours(-6), "Employee hours");
        employeeEntry = await _hr.CorrectTimeAsync("time-user", _tenant, _organization,
            employeeEntry.Id, employeeEntry.Version, Now.AddHours(-7), Now.AddHours(-5.5),
            "corrected employee hours");
        var projectEntry = await Record(Now.AddHours(-5), Now.AddHours(-3), "Project hours", _project);
        var taskEntry = await Record(Now.AddHours(-3), Now, "Task hours", _project, task.Id);
        var sheet = await _hr.SubmitTimesheetAsync("time-user", _tenant, _organization,
            Now.AddHours(-8), Now);
        var contact = await _billing.EnrollContactAsync("time-user", _tenant, _organization,
            "TIME-CUSTOMER");
        await Error(() => _billing.RegisterTimeSourceAsync("time-user", _tenant, _organization,
            employeeEntry.Id, new(contact.Id, "KRW", "Employee hours", 100m)),
            "TIME_ENTRY_NOT_APPROVED");

        await _hr.ReviewTimesheetAsync("time-reviewer", _tenant, _organization,
            sheet.Id, sheet.Version, true, "approved");
        await Register(employeeEntry, contact.Id, 100m);
        await Register(projectEntry, contact.Id, 200m);
        await Register(taskEntry, contact.Id, 300m);

        var taskInvoice = await Generate(contact.Id, BillingInvoiceType.ByTaskHours);
        taskInvoice.Document.Totals.Total.Should().Be(900m);
        taskInvoice.Generation.Sources.Should().ContainSingle().Which.Should()
            .Match<AutomaticBillingSourceLink>(source => source.SourceId == taskEntry.Id
                && source.TaskId == task.Id && source.ProjectId == _project);

        var projectInvoice = await Generate(contact.Id, BillingInvoiceType.ByProjectHours);
        projectInvoice.Document.Totals.Total.Should().Be(400m);
        projectInvoice.Generation.Sources.Should().ContainSingle().Which.SourceId
            .Should().Be(projectEntry.Id);

        var employeeInvoice = await Generate(contact.Id, BillingInvoiceType.ByEmployeeHours);
        employeeInvoice.Document.Totals.Total.Should().Be(150m);
        employeeInvoice.Generation.Sources.Should().ContainSingle().Which.SourceId
            .Should().Be(employeeEntry.Id);

        (await _billing.GetTimeSourceAsync("time-user", _tenant, _organization, taskEntry.Id))
            .State.Should().Be(BillingTimeSourceState.Invoiced);
        Scalar<long>("SELECT COUNT(*) FROM ERP_AUTOMATIC_BILLING_SOURCE WHERE SOURCE_KIND=0")
            .Should().Be(3);
        await Error(() => Generate(contact.Id, BillingInvoiceType.ByEmployeeHours),
            "AUTOMATIC_BILLING_SOURCE_NOT_FOUND");
    }

    private Task<NexaFramework.Service.HumanResources.TimeEntry> Record(DateTimeOffset start,
        DateTimeOffset end, string description, Guid? project = null, Guid? task = null)
        => _hr.RecordTimeAsync("time-user", _tenant, _organization,
            new(start, end, project, task, description));

    private Task<BillingTimeSource> Register(
        NexaFramework.Service.HumanResources.TimeEntry entry, Guid contact, decimal rate)
        => _billing.RegisterTimeSourceAsync("time-user", _tenant, _organization, entry.Id,
            new(contact, "KRW", entry.Description, rate, ApplyTax: false, ApplyDiscount: false));

    private Task<AutomaticBillingResult> Generate(Guid contact, BillingInvoiceType type)
        => _billing.GenerateAutomaticInvoiceAsync("time-user", _tenant, _organization,
            Guid.NewGuid(), new(contact, type, new(2026, 9, 1), new(2026, 9, 30),
                new(2026, 9, 26), new(2026, 10, 26), "KRW"));

    private Guid BusinessId(string userId) => Guid.Parse(Scalar<string>(
        "SELECT BUSINESS_USER_ID FROM SYS_BUSINESS_IDENTITY WHERE USER_ID=@userId", new { userId }));

    private void Execute(string sql, object? values = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute(sql, values);
    }

    private T Scalar<T>(string sql, object? values = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection.ExecuteScalar<T>(sql, values)!;
    }

    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);
}

file sealed class FixedHrClock(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}
