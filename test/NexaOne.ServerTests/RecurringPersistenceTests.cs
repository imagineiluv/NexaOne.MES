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
}
