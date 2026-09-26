using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.HumanResources;
using NexaOne.HR.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Hr;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class HrTimeReportPersistenceTests :
    IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Grants =
    [
        "hr.time.read", "hr.time.record-manual", "hr.timesheet.submit", "hr.timesheet.review"
    ];
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly BusinessMembershipBridge _memberships;
    private readonly TimeTrackingBridge _bridge;

    public HrTimeReportPersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        var dataSource = new EesDataSource
        {
            Provider = new SqliteProvider(), ConnectionString = _connectionString
        };
        _memberships = new(dataSource);
        _bridge = new(dataSource, _memberships,
            Mock.Of<IBusinessProjectDirectory>(MockBehavior.Strict), new FixedReportClock(Now));
        Execute("""
            INSERT INTO SYS_ROLE (ROLE_ID,ROLE_NAME,PERMISSIONS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('HR-REPORT','HR report','','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID,USER_NAME,PASSWORD_HASH,EMAIL,ROLE_ID,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('report-user','Report user','','','HR-REPORT','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID,USER_NAME,PASSWORD_HASH,EMAIL,ROLE_ID,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('write-only','Write only','','','HR-REPORT','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            """);
    }

    public async Task InitializeAsync()
    {
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "report-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "write-only", new(0, true, ["hr.time.record-manual", "hr.timesheet.review"]))).IsSuccess.Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        File.Delete(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Report_filters_completed_entries_and_csv_is_stable_and_formula_safe()
    {
        var first = await Record(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            new(2026, 9, 1, 11, 30, 0, TimeSpan.Zero), "=SUM(A1:A2)");
        var sheet = await _bridge.SubmitTimesheetAsync("report-user", _tenant, _organization,
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        await _bridge.ReviewTimesheetAsync("write-only", _tenant, _organization,
            sheet.Id, sheet.Version, true, "approved");
        var second = await Record(new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            new(2026, 9, 3, 13, 0, 0, TimeSpan.Zero), "Follow-up");

        var query = new HrTimeReportQuery(new(2026, 9, 1), new(2026, 9, 30));
        var report = await _bridge.BuildTimeReportAsync("report-user", _tenant, _organization, query);

        report.Scope.Should().Be(new BusinessScope("NexaOne.MES", _tenant.ToString("D"),
            _organization.ToString("D")));
        report.GeneratedAt.Should().Be(Now);
        report.Rows.Select(row => row.EntryId).Should().Equal(first.Id, second.Id);
        report.Rows[0].ApprovalState.Should().Be(TimesheetState.Approved);
        report.Rows[1].ApprovalState.Should().BeNull();
        report.TotalDurationTicks.Should().Be(TimeSpan.FromHours(3.5).Ticks);

        var approved = await _bridge.BuildTimeReportAsync("report-user", _tenant, _organization,
            query with { Approval = HrTimeApprovalFilter.Approved });
        approved.Rows.Should().ContainSingle().Which.EntryId.Should().Be(first.Id);
        var unsubmitted = await _bridge.BuildTimeReportAsync("report-user", _tenant, _organization,
            query with { Approval = HrTimeApprovalFilter.Unsubmitted });
        unsubmitted.Rows.Should().ContainSingle().Which.EntryId.Should().Be(second.Id);

        var export = await _bridge.ExportTimeReportCsvAsync("report-user", _tenant,
            _organization, query);
        export.RowCount.Should().Be(2);
        export.Content.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var csv = Encoding.UTF8.GetString(export.Content);
        csv.Should().Contain("\"'=SUM(A1:A2)\"");
        csv.Should().Contain("\"2.5\"");
        csv.Split("\r\n", StringSplitOptions.None).Should().HaveCount(4);
    }

    [Fact]
    public async Task Scope_and_report_reads_require_the_current_read_grant_and_valid_period()
    {
        var scopes = await _bridge.ListAccessibleScopesAsync("report-user");
        scopes.Total.Should().Be(1);
        scopes.Items.Should().ContainSingle().Which.OrganizationId.Should().Be(_organization);
        (await _bridge.ListAccessibleScopesAsync("write-only")).Items.Should().BeEmpty();

        await Error(() => _bridge.BuildTimeReportAsync("write-only", _tenant, _organization,
            new(new(2026, 9, 1), new(2026, 9, 30))), "BUSINESS_ACCESS_DENIED");
        await Error(() => _bridge.BuildTimeReportAsync("report-user", _tenant, _organization,
            new(new(2026, 9, 30), new(2026, 9, 1))), "INVALID_BUSINESS_INPUT");
        await Error(() => _bridge.BuildTimeReportAsync("report-user", _tenant, _organization,
            new(new(2025, 1, 1), new(2026, 1, 2))), "INVALID_BUSINESS_INPUT");
        await Error(() => _bridge.BuildTimeReportAsync("report-user", _tenant, _organization,
            new(DateOnly.MaxValue, DateOnly.MaxValue)), "INVALID_BUSINESS_INPUT");
    }

    [Fact]
    public async Task Report_rejects_owner_column_and_payload_disagreement()
    {
        var entry = await Record(new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero),
            new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero), "Valid");
        var other = BusinessId("write-only");
        Execute("UPDATE HR_TIME_ENTRY SET EMPLOYEE_ID=@Employee WHERE TIME_ENTRY_ID=@Id",
            new { Employee = other.ToString("D"), Id = entry.Id.ToString("D") });

        await Error(() => _bridge.BuildTimeReportAsync("report-user", _tenant, _organization,
            new(new(2026, 9, 1), new(2026, 9, 30))), "STORAGE_CONTRACT_VIOLATION");
    }

    [Fact]
    public async Task Report_translates_malformed_storage_payload_to_a_stable_contract_error()
    {
        var entry = await Record(new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero),
            new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero), "Valid");
        Execute("UPDATE HR_TIME_ENTRY SET PAYLOAD='{' WHERE TIME_ENTRY_ID=@Id",
            new { Id = entry.Id.ToString("D") });

        await Error(() => _bridge.BuildTimeReportAsync("report-user", _tenant, _organization,
            new(new(2026, 9, 1), new(2026, 9, 30))), "STORAGE_CONTRACT_VIOLATION");
    }

    private Task<TimeEntry> Record(DateTimeOffset start, DateTimeOffset end, string description)
        => _bridge.RecordTimeAsync("report-user", _tenant, _organization,
            new(start, end, Description: description));

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

file sealed class FixedReportClock(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}
