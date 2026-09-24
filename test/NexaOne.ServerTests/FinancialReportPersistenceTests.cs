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

/// <summary>Real SQLite acceptance of the atomic billing, income and expense reporting read model.</summary>
public sealed class FinancialReportPersistenceTests
    : IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private static readonly string[] Grants =
    [
        "financial-report.read", "billing.read", "billing.write", "billing.pay",
        "expense.directory.read", "expense.directory.write", "expense.read", "expense.write",
        "recurring.read", "recurring.write", "recurring.execute"
    ];
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly BusinessMembershipBridge _memberships;
    private readonly BillingBridge _bridge;
    private Guid _employee;

    public FinancialReportPersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _memberships = new(DataSource());
        _bridge = NewBridge();
        Execute("""
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE)
                VALUES ('REPORT-CUSTOMER','Report customer',1);
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('REPORT-MEMBER','Report member','', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('report-user','Report owner','','','REPORT-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('report-reader','Report reader','','','REPORT-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }

    public async Task InitializeAsync()
    {
        var owner = await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "report-user", new(0, true, Grants));
        var reader = await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "report-reader", new(0, true, ["billing.read"]));
        owner.IsSuccess.Should().BeTrue();
        reader.IsSuccess.Should().BeTrue();
        _employee = owner.Value.BusinessUserId;
    }

    public Task DisposeAsync() { File.Delete(_path); return Task.CompletedTask; }
    private EesDataSource DataSource() => new()
        { Provider = new SqliteProvider(), ConnectionString = _connectionString };
    private BillingBridge NewBridge() => new(DataSource(), new BusinessMembershipBridge(DataSource()),
        new BusinessMasterDirectory(DataSource()));
    private void Execute(string sql, object? values = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute(sql, values);
    }
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);

    [Fact]
    public async Task Report_reads_one_scoped_snapshot_and_exports_currency_separated_totals()
    {
        var contact = await _bridge.EnrollContactAsync("report-user", _tenant, _organization,
            "REPORT-CUSTOMER");
        var invoice = await _bridge.CreateDocumentAsync("report-user", _tenant, _organization,
            Guid.NewGuid(), BillingKind.Invoice,
            new(contact.Id, new(2026, 9, 10), new(2026, 10, 10), "KRW", [new("Service", 100m, 1m)]));
        invoice = await _bridge.MarkSentAsync("report-user", _tenant, _organization,
            invoice.Id, invoice.Version);
        var payment = await _bridge.RecordPaymentAsync("report-user", _tenant, _organization, Guid.NewGuid(),
            new(invoice.Id, 40m, "KRW", new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero), PaymentMethod.BankTransfer));
        await _bridge.CreateDocumentAsync("report-user", _tenant, _organization, Guid.NewGuid(),
            BillingKind.Invoice,
            new(contact.Id, new(2026, 9, 14), new(2026, 10, 14), "KRW", [new("Draft", 500m, 1m)]));
        var outsideInvoice = await _bridge.CreateDocumentAsync("report-user", _tenant, _organization,
            Guid.NewGuid(), BillingKind.Invoice,
            new(contact.Id, new(2026, 10, 1), new(2026, 10, 31), "USD", [new("Outside", 700m, 1m)]));
        outsideInvoice = await _bridge.MarkSentAsync("report-user", _tenant, _organization,
            outsideInvoice.Id, outsideInvoice.Version);
        await _bridge.RecordPaymentAsync("report-user", _tenant, _organization, Guid.NewGuid(),
            new(outsideInvoice.Id, 5m, "USD", new(2026, 9, 30, 23, 59, 59, TimeSpan.Zero), PaymentMethod.Cash));

        var incomeRule = await _bridge.CreateRuleAsync("report-user", _tenant, _organization,
            Guid.NewGuid(), new("Monthly income", new(new(2026, 9, 1), null, 30),
                new RecurringIncomeTemplate(25.5m, contact.Id, _employee, "USD")));
        await _bridge.ExecuteOccurrenceAsync("report-user", _tenant, _organization,
            incomeRule.Id, new(2026, 9, 1));
        await _bridge.ExecuteOccurrenceAsync("report-user", _tenant, _organization,
            incomeRule.Id, new(2026, 10, 1));

        var category = await _bridge.CreateCategoryAsync("report-user", _tenant, _organization, new("Travel"));
        var vendor = await _bridge.CreateVendorAsync("report-user", _tenant, _organization, new("Rail"));
        var expense = await _bridge.CreateExpenseAsync("report-user", _tenant, _organization,
            Guid.NewGuid(), new(110m, ExpenseType.TaxDeductible, category.Id, vendor.Id, _employee,
                null, null, "KRW", new(2026, 9, 12), Tax: new(ExpenseTaxType.Percentage, 10m, "VAT")));
        var cancelled = await _bridge.CreateExpenseAsync("report-user", _tenant, _organization,
            Guid.NewGuid(), new(999m, ExpenseType.NotTaxDeductible, category.Id, vendor.Id, _employee,
                null, null, "KRW", new(2026, 9, 13)));
        await _bridge.CancelExpenseAsync("report-user", _tenant, _organization,
            cancelled.Id, cancelled.Version, "exclude");
        await _bridge.CreateExpenseAsync("report-user", _tenant, _organization,
            Guid.NewGuid(), new(800m, ExpenseType.NotTaxDeductible, category.Id, vendor.Id, _employee,
                null, null, "KRW", new(2026, 10, 1)));

        IFinancialReportBridge reporting = NewBridge();
        var period = new FinancialReportPeriod(new(2026, 9, 1), new(2026, 9, 30));
        var report = await reporting.BuildAsync("report-user", _tenant, _organization, period);

        report.Scope.Should().Be(new BusinessScope("NexaOne.MES", _tenant.ToString("D"), _organization.ToString("D")));
        report.Period.Should().Be(period);
        report.GeneratedAt.Offset.Should().Be(TimeSpan.Zero);
        report.Currencies.Should().Equal(
            new FinancialCurrencyTotals("KRW", 1, 100m, 40m, 60m, 0, 0m, 1, 110m, 10m, 100m),
            new FinancialCurrencyTotals("USD", 0, 0m, 0m, 0m, 1, 25.5m, 0, 0m, 0m, 0m));
        expense.State.Should().Be(ExpenseState.Active);

        var csv = await reporting.ExportCsvAsync("report-user", _tenant, _organization, period);
        csv.Should().Contain("KRW,1,100,40,60,0,0,1,110,10,100\r\n")
            .And.Contain("USD,0,0,0,0,1,25.5,0,0,0,0\r\n");
        csv.IndexOf("KRW,", StringComparison.Ordinal).Should()
            .BeLessThan(csv.IndexOf("USD,", StringComparison.Ordinal));

        var cashPeriod = new CashFlowReportPeriod(new(2026, 9, 1), new(2026, 9, 30));
        var cash = await reporting.BuildCashFlowAsync("report-user", _tenant, _organization, cashPeriod);
        cash.Currencies.Should().Equal(
            new CashFlowCurrencyTotals("KRW", 1, 40m),
            new CashFlowCurrencyTotals("USD", 1, 5m));
        (await reporting.ExportCashFlowCsvAsync("report-user", _tenant, _organization, cashPeriod))
            .Should().Contain("currency,payment_count,received\r\nKRW,1,40\r\nUSD,1,5\r\n");
        await _bridge.CancelPaymentAsync("report-user", _tenant, _organization,
            payment.Id, payment.Version, "exclude correction");
        (await reporting.BuildCashFlowAsync("report-user", _tenant, _organization, cashPeriod))
            .Currencies.Should().Equal(new CashFlowCurrencyTotals("USD", 1, 5m));
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN "
            + "('IX_ERP_BILLING_DOCUMENT_REPORT','IX_ERP_INCOME_REPORT','IX_ERP_EXPENSE_REPORT')")
            .Should().Be(3);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='index' "
            + "AND name='IX_ERP_BILLING_PAYMENT_CASH_FLOW'").Should().Be(1);
    }

    [Fact]
    public async Task Permission_and_scope_discovery_are_report_specific()
    {
        IFinancialReportBridge reporting = NewBridge();
        var scopes = await reporting.ListAccessibleScopesAsync("report-user");
        scopes.Total.Should().Be(1);
        scopes.Items.Single().OrganizationId.Should().Be(_organization);
        (await reporting.ListAccessibleScopesAsync("report-reader")).Total.Should().Be(0);
        await Error(() => reporting.BuildAsync("report-reader", _tenant, _organization,
            new(new(2026, 9, 1), new(2026, 9, 30))), "BUSINESS_ACCESS_DENIED");
        await Error(() => reporting.BuildAsync("report-user", _tenant, Guid.NewGuid(),
            new(new(2026, 9, 1), new(2026, 9, 30))), "BUSINESS_ACCESS_DENIED");
    }
}
