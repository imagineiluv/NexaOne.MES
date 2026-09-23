using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Erp;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>Real SQLite acceptance of recurring ERP rules and their rule/month idempotency boundary.</summary>
public sealed class RecurringPersistenceTests : IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private static readonly string[] Grants =
    [
        "recurring.read", "recurring.write", "recurring.execute", "billing.read", "billing.write",
        "expense.directory.read", "expense.directory.write", "expense.read", "expense.write"
    ];
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly BusinessMembershipBridge _memberships;
    private readonly BillingBridge _bridge;
    private Guid _employee;
    private BillingContact _contact = null!;
    private ExpenseCategory _category = null!;
    private ExpenseVendor _vendor = null!;

    public RecurringPersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _memberships = new(DataSource()); _bridge = NewBridge();
        Execute("""
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE)
                VALUES ('RECUR-CUSTOMER','Recurring customer',1);
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('RECUR-MEMBER','Recurring member','', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('recurring-user','Recurring owner','','','RECUR-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('recurring-reader','Recurring reader','','','RECUR-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }

    public async Task InitializeAsync()
    {
        var owner = await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "recurring-user", new(0, true, Grants));
        var reader = await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "recurring-reader", new(0, true, ["recurring.read"]));
        owner.IsSuccess.Should().BeTrue(); reader.IsSuccess.Should().BeTrue();
        _employee = owner.Value.BusinessUserId;
        _contact = await _bridge.EnrollContactAsync("recurring-user", _tenant, _organization, "RECUR-CUSTOMER");
        _category = await _bridge.CreateCategoryAsync("recurring-user", _tenant, _organization, new("Subscriptions"));
        _vendor = await _bridge.CreateVendorAsync("recurring-user", _tenant, _organization, new("Monthly vendor"));
    }

    public Task DisposeAsync() { File.Delete(_path); return Task.CompletedTask; }
    private EesDataSource DataSource() => new() { Provider = new SqliteProvider(), ConnectionString = _connectionString };
    private BillingBridge NewBridge() => new(DataSource(), new BusinessMembershipBridge(DataSource()), new BusinessMasterDirectory(DataSource()));
    private void Execute(string sql, object? values = null) { using var c = new SqliteConnection(_connectionString); c.Open(); c.Execute(sql, values); }
    private long Count(string table) { using var c = new SqliteConnection(_connectionString); c.Open(); return c.ExecuteScalar<long>("SELECT COUNT(*) FROM " + table); }
    private T Scalar<T>(string sql, object? values = null) { using var c = new SqliteConnection(_connectionString); c.Open(); return c.ExecuteScalar<T>(sql, values)!; }
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);
    private Task<RecurringRule> Rule(string name, RecurringTemplate template, Guid? operation = null)
        => _bridge.CreateRuleAsync("recurring-user", _tenant, _organization, operation ?? Guid.NewGuid(),
            new(name, new(new(2026, 9, 1), new(2026, 12, 1), 31), template));

    [Fact]
    public async Task Billing_income_and_expense_occurrences_persist_and_replay_by_rule_and_month()
    {
        var billingRule = await Rule("Monthly invoice", new RecurringBillingTemplate(BillingKind.Invoice,
            _contact.Id, 14, "KRW", [new("Service", 10.25m, 2m)]));
        var incomeRule = await Rule("Monthly income", new RecurringIncomeTemplate(25.5m,
            _contact.Id, _employee, "KRW", Reference: "INCOME"));
        var expenseRule = await Rule("Monthly expense", new RecurringExpenseTemplate(11m,
            ExpenseType.TaxDeductible, _category.Id, _vendor.Id, _employee, null, null, "KRW",
            Purpose: "Cloud", Tax: new(ExpenseTaxType.Percentage, 10m, "VAT")));

        var month = new DateOnly(2026, 9, 1);
        var billing = await _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization, billingRule.Id, month);
        var income = await _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization, incomeRule.Id, month);
        var expense = await _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization, expenseRule.Id, month);
        billing.Billing!.Input.DocumentDate.Should().Be(new DateOnly(2026, 9, 30));
        billing.Billing.Totals.Total.Should().Be(20.5m);
        income.Income!.Input.ValueDate.Should().Be(new DateOnly(2026, 9, 30));
        expense.Expense!.Amounts.Should().Be(new ExpenseAmounts(11m, 1m, 10m));

        (await NewBridge().ExecuteOccurrenceAsync("recurring-user", _tenant, _organization,
            billingRule.Id, month)).Should().BeEquivalentTo(billing);
        Count("ERP_RECURRING_OCCURRENCE").Should().Be(3);
        Count("ERP_BILLING_DOCUMENT").Should().Be(1);
        Count("ERP_INCOME").Should().Be(1);
        Count("ERP_EXPENSE").Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_same_month_execution_creates_exactly_one_business_record()
    {
        var rule = await Rule("Concurrent invoice", new RecurringBillingTemplate(BillingKind.Invoice,
            _contact.Id, 0, "KRW", [new("Service", 1m, 1m)]));
        var month = new DateOnly(2026, 10, 1);
        var executions = await Task.WhenAll(
            NewBridge().ExecuteOccurrenceAsync("recurring-user", _tenant, _organization, rule.Id, month),
            NewBridge().ExecuteOccurrenceAsync("recurring-user", _tenant, _organization, rule.Id, month));

        executions[1].Should().BeEquivalentTo(executions[0]);
        Count("ERP_RECURRING_OCCURRENCE").Should().Be(1);
        Count("ERP_BILLING_DOCUMENT").Should().Be(1);
    }

    [Fact]
    public async Task Deactivation_blocks_new_month_but_preserves_history_replay_and_permissions()
    {
        var operation = Guid.NewGuid();
        var input = new RecurringRuleInput("Lifecycle", new(new(2026, 9, 1), null, 1),
            new RecurringIncomeTemplate(5m, _contact.Id, null, "KRW"));
        var rule = await _bridge.CreateRuleAsync("recurring-user", _tenant, _organization, operation, input);
        (await NewBridge().CreateRuleAsync("recurring-user", _tenant, _organization, operation, input))
            .Should().BeEquivalentTo(rule);
        var september = await _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization,
            rule.Id, new(2026, 9, 1));
        var inactive = await _bridge.DeactivateRuleAsync("recurring-user", _tenant, _organization,
            rule.Id, rule.Version);
        inactive.Active.Should().BeFalse();
        (await NewBridge().ExecuteOccurrenceAsync("recurring-user", _tenant, _organization,
            rule.Id, new(2026, 9, 1))).Should().BeEquivalentTo(september);
        await Error(() => _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization,
            rule.Id, new(2026, 10, 1)), "RECURRING_RULE_INACTIVE");

        var read = await NewBridge().GetRuleAsync("recurring-reader", _tenant, _organization, rule.Id);
        read.Should().BeEquivalentTo(inactive);
        await Error(() => NewBridge().ExecuteOccurrenceAsync("recurring-reader", _tenant, _organization,
            rule.Id, new(2026, 9, 1)), "BUSINESS_ACCESS_DENIED");
        var scopes = await ((IRecurringBridge)_bridge).ListAccessibleScopesAsync("recurring-reader");
        scopes.Total.Should().Be(1);
        (await _bridge.ListRulesAsync("recurring-reader", _tenant, _organization,
            new(RecurringTarget.Income, false))).Items.Single().Should().BeEquivalentTo(inactive);
    }

    [Fact]
    public async Task Occurrence_history_is_scoped_filterable_paged_and_rejects_corrupt_storage()
    {
        var incomeRule = await Rule("History income", new RecurringIncomeTemplate(
            25m, _contact.Id, _employee, "KRW"));
        var expenseRule = await Rule("History expense", new RecurringExpenseTemplate(
            11m, ExpenseType.TaxDeductible, _category.Id, _vendor.Id, _employee,
            null, null, "KRW"));
        await _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization,
            incomeRule.Id, new(2026, 9, 1));
        await _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization,
            expenseRule.Id, new(2026, 10, 1));
        await _bridge.ExecuteOccurrenceAsync("recurring-user", _tenant, _organization,
            incomeRule.Id, new(2026, 11, 1));

        var all = await NewBridge().ListOccurrencesAsync(
            "recurring-reader", _tenant, _organization);
        all.Total.Should().Be(3);
        all.Items.Select(item => item.Occurrence.Month).Should().Equal(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 1));
        all.Items.Select(item => item.RuleName).Should().Equal(
            "History income", "History expense", "History income");

        var filtered = await NewBridge().ListOccurrencesAsync("recurring-reader", _tenant, _organization,
            new(incomeRule.Id, RecurringTarget.Income, new(2026, 10, 1), new(2026, 11, 1)));
        filtered.Total.Should().Be(1);
        filtered.Items.Single().Occurrence.Month.Should().Be(new DateOnly(2026, 11, 1));

        var page = await NewBridge().ListOccurrencesAsync("recurring-reader", _tenant, _organization,
            new(Offset: 1, Limit: 1));
        page.Total.Should().Be(3);
        page.Items.Single().Occurrence.Month.Should().Be(new DateOnly(2026, 10, 1));

        await Error(() => NewBridge().ListOccurrencesAsync("recurring-reader", _tenant, _organization,
            new(StartMonth: new(2026, 11, 1), EndMonth: new(2026, 10, 1))), "INVALID_BUSINESS_INPUT");
        await Error(() => NewBridge().ListOccurrencesAsync("recurring-reader", _tenant, _organization,
            new(StartMonth: new(2026, 10, 2))), "INVALID_BUSINESS_INPUT");
        await Error(() => NewBridge().ListOccurrencesAsync("recurring-reader", _tenant, _organization,
            new(RuleId: Guid.Empty)), "INVALID_BUSINESS_INPUT");
        await Error(() => NewBridge().ListOccurrencesAsync("recurring-reader", _tenant, _organization,
            new(Limit: 101)), "INVALID_BUSINESS_INPUT");

        Execute("UPDATE ERP_RECURRING_OCCURRENCE SET TARGET=0 WHERE RULE_ID=@rule",
            new { rule = incomeRule.Id.ToString("D") });
        await Assert.ThrowsAsync<InvalidDataException>(() => NewBridge().ListOccurrencesAsync(
            "recurring-reader", _tenant, _organization, new(RuleId: incomeRule.Id)));
    }

    [Fact]
    public async Task Service_principal_scope_is_audited_due_date_limited_and_live_revocation_stops_execution()
    {
        IRecurringAutomationBridge automation = _bridge;
        var principal = await automation.SavePrincipalAsync(
            "admin", "erp-monthly", new(0, "Monthly ERP", true));
        principal.IsSuccess.Should().BeTrue();
        var grant = await automation.SaveScopeAsync(
            "admin", "erp-monthly", _tenant, _organization, new(0, true, "Asia/Seoul", 2));
        grant.IsSuccess.Should().BeTrue();
        grant.Value.TimeZoneId.Should().Be("Asia/Seoul");
        grant.Value.CatchUpMonths.Should().Be(2);
        (await automation.GetScopeAsync("admin", "erp-monthly", _tenant, _organization)).Value
            .Should().BeEquivalentTo(grant.Value);
        (await automation.ListActiveScopesAsync("erp-monthly")).Items.Single()
            .Should().BeEquivalentTo(grant.Value);
        (await automation.SaveScopeAsync(
            "admin", "erp-monthly", _tenant, _organization, new(0, true)))
            .Error.Type.Should().Be(NexaOne.Common.ErrorType.Conflict);

        var rule = await Rule("Automated invoice", new RecurringBillingTemplate(BillingKind.Invoice,
            _contact.Id, 0, "KRW", [new("Service", 9m, 1m)]));
        (await automation.ListDueRulesAsync(
            "erp-monthly", _tenant, _organization, 1, new DateOnly(2026, 9, 29))).Total.Should().Be(0);
        (await automation.ListDueRulesAsync(
            "erp-monthly", _tenant, _organization, 1, new DateOnly(2026, 9, 30))).Items.Single().Id
            .Should().Be(rule.Id, "day 31 clips to the last day of September");

        var execution = await automation.ExecuteOccurrenceAsync(
            "erp-monthly", _tenant, _organization, 1, rule.Id, new DateOnly(2026, 9, 1));
        var actor = principal.Value.AuditActorId.ToString("D");
        execution.Occurrence.CreatedBy.Should().Be(actor);
        execution.Billing!.CreatedBy.Should().Be(actor);
        Scalar<string>("SELECT USER_ID FROM ERP_BILLING_AUDIT WHERE RESOURCE_TYPE='recurring-occurrence'")
            .Should().Be(actor);
        Scalar<long>("SELECT COUNT(*) FROM SYS_USER WHERE USER_ID='svc.erp.erp-monthly' AND IS_ACTIVE=0")
            .Should().Be(1, "the compatibility identity must never be login-enabled");
        Scalar<long>("SELECT COUNT(*) FROM SYS_BUSINESS_MEMBERSHIP WHERE USER_ID='svc.erp.erp-monthly'")
            .Should().Be(0, "service authority comes only from its recurring scope grant");
        Scalar<string>("SELECT r.PERMISSIONS FROM SYS_USER u JOIN SYS_ROLE r ON r.ROLE_ID=u.ROLE_ID WHERE u.USER_ID='svc.erp.erp-monthly'")
            .Should().BeEmpty("the compatibility identity must have no role authority even if misactivated");

        var revoked = await automation.SaveScopeAsync(
            "admin", "erp-monthly", _tenant, _organization, new(1, false));
        revoked.Value.IsActive.Should().BeFalse();
        (await automation.ListActiveScopesAsync("erp-monthly")).Total.Should().Be(0);
        await Error(() => automation.ExecuteOccurrenceAsync(
            "erp-monthly", _tenant, _organization, 1, rule.Id, new DateOnly(2026, 9, 1)),
            "BUSINESS_ACCESS_DENIED");
        (await automation.SaveScopeAsync(
            "admin", "erp-monthly", _tenant, _organization, new(2, true, null, 0))).IsSuccess.Should().BeTrue();
        await Error(() => automation.ExecuteOccurrenceAsync(
            "erp-monthly", _tenant, _organization, 1, rule.Id, new DateOnly(2026, 9, 1)),
            "BUSINESS_ACCESS_DENIED");
        (await automation.SavePrincipalAsync(
            "admin", "erp-monthly", new(1, "Monthly ERP", false))).Value.IsActive.Should().BeFalse();
        await Error(() => automation.ListActiveScopesAsync("erp-monthly"), "BUSINESS_ACCESS_DENIED");
        await Error(() => automation.ExecuteOccurrenceAsync(
            "erp-monthly", _tenant, _organization, 3, rule.Id, new DateOnly(2026, 9, 1)),
            "BUSINESS_ACCESS_DENIED");
        Count("ERP_RECURRING_SERVICE_PRINCIPAL_AUDIT").Should().Be(2);
        Count("ERP_RECURRING_SERVICE_SCOPE_AUDIT").Should().Be(3);
        Scalar<string>("SELECT TIME_ZONE_ID FROM ERP_RECURRING_SERVICE_SCOPE_AUDIT WHERE SCOPE_VERSION=1")
            .Should().Be("Asia/Seoul");
        Scalar<long>("SELECT CATCH_UP_MONTHS FROM ERP_RECURRING_SERVICE_SCOPE_AUDIT WHERE SCOPE_VERSION=1")
            .Should().Be(2);
    }

    [Fact]
    public async Task Service_principal_scope_rejects_invalid_calendar_policy()
    {
        IRecurringAutomationBridge automation = _bridge;
        (await automation.SavePrincipalAsync("admin", "erp-monthly", new(0, "Monthly ERP", true)))
            .IsSuccess.Should().BeTrue();

        (await automation.SaveScopeAsync("admin", "erp-monthly", _tenant, _organization,
            new(0, true, "Not/A-Time-Zone", 0))).Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        (await automation.SaveScopeAsync("admin", "erp-monthly", _tenant, _organization,
            new(0, true, "UTC", 25))).Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        Count("ERP_RECURRING_SERVICE_SCOPE").Should().Be(0);
    }
}
