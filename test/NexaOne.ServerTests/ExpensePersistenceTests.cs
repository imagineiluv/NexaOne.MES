using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaFramework.Service.Projects;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.SYS.Infrastructure;
using NexaOne.ServiceContracts.Crm;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>Real SQLite acceptance of the organization-scoped expense adapter and its billing transaction.</summary>
public sealed class ExpensePersistenceTests : IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private static readonly string[] Grants =
    [
        "expense.directory.read", "expense.directory.write", "expense.read", "expense.write",
        "expense.reimburse", "expense.invoice", "billing.read", "billing.write",
        "crm.project.read", "crm.project.manage", "crm.project.all"
    ];
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly BusinessMembershipBridge _memberships;
    private readonly BillingBridge _bridge;
    private Guid _employee;
    private Guid _secondEmployee;
    private Guid _readerEmployee;

    public ExpensePersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _memberships = new(DataSource());
        _bridge = NewBridge();
        Execute("""
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE) VALUES ('EXP-CUSTOMER','Expense customer',1);
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('EXPENSE-MEMBER','Expense member','', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('expense-user','Expense owner','','','EXPENSE-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('expense-peer','Expense peer','','','EXPENSE-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('expense-reader','Expense reader','','','EXPENSE-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }

    public async Task InitializeAsync()
    {
        var owner = await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "expense-user", new(0, true, Grants));
        var peer = await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "expense-peer", new(0, true, ["expense.read"]));
        var reader = await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "expense-reader", new(0, true,
            ["expense.directory.read", "expense.read"]));
        owner.IsSuccess.Should().BeTrue(); peer.IsSuccess.Should().BeTrue(); reader.IsSuccess.Should().BeTrue();
        _employee = owner.Value.BusinessUserId; _secondEmployee = peer.Value.BusinessUserId;
        _readerEmployee = reader.Value.BusinessUserId;
    }

    public Task DisposeAsync() { File.Delete(_path); return Task.CompletedTask; }
    private EesDataSource DataSource() => new() { Provider = new SqliteProvider(), ConnectionString = _connectionString };
    private BillingBridge NewBridge()
    {
        var dataSource = DataSource();
        var memberships = new BusinessMembershipBridge(dataSource);
        var masters = new BusinessMasterDirectory(dataSource);
        var projects = new NexaOne.CRM.Module(dataSource, memberships, masters).GetBusinessProjectDirectory();
        return new(dataSource, memberships, masters, projects: projects);
    }
    private ICrmBridge NewCrmBridge()
    {
        var dataSource = DataSource();
        return new NexaOne.CRM.Module(dataSource, new BusinessMembershipBridge(dataSource),
            new BusinessMasterDirectory(dataSource)).GetCrmBridge();
    }
    private void Execute(string sql, object? values = null) { using var c = new SqliteConnection(_connectionString); c.Open(); c.Execute(sql, values); }
    private T Scalar<T>(string sql, object? values = null) { using var c = new SqliteConnection(_connectionString); c.Open(); return c.ExecuteScalar<T>(sql, values)!; }
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);
    private Task<ExpenseCategory> Category(string name = "Travel")
        => _bridge.CreateCategoryAsync("expense-user", _tenant, _organization, new(name));
    private Task<ExpenseVendor> Vendor(string name = "Rail")
        => _bridge.CreateVendorAsync("expense-user", _tenant, _organization, new(name));
    private static ExpenseInput Input(Guid category, Guid vendor, Guid? employee = null, decimal amount = 110m)
        => new(amount, ExpenseType.TaxDeductible, category, vendor, employee, null, null, "KRW",
            new(2026, 9, 23), Purpose: "출장", Tax: new(ExpenseTaxType.Percentage, 10m, "VAT"));

    [Fact]
    public async Task Directory_expense_and_reimbursement_persist_with_exact_amounts_and_replay()
    {
        var category = await Category(); var vendor = await Vendor();
        var operation = Guid.NewGuid(); var input = Input(category.Id, vendor.Id, _employee);
        var created = await _bridge.CreateExpenseAsync("expense-user", _tenant, _organization, operation, input);
        created.Amounts.Should().Be(new ExpenseAmounts(110m, 10m, 100m));
        created.Status.Should().Be(ExpenseStatus.NotBillable); created.CreatedBy.Should().Be(_employee.ToString("D"));
        (await NewBridge().CreateExpenseAsync("expense-user", _tenant, _organization, operation, input))
            .Should().BeEquivalentTo(created);
        await Error(() => NewBridge().CreateExpenseAsync("expense-user", _tenant, _organization, operation,
            input with { Amount = 111m }), "EXPENSE_OPERATION_CONFLICT");

        var paidAt = new DateTimeOffset(2026, 9, 23, 8, 30, 0, TimeSpan.Zero); var reimbursement = Guid.NewGuid();
        var paid = await _bridge.ReimburseExpenseAsync("expense-user", _tenant, _organization, reimbursement,
            created.Id, created.Version, paidAt, "BANK-1");
        paid.Status.Should().Be(ExpenseStatus.Paid);
        paid.Reimbursement.Should().Be(new ExpenseReimbursement(reimbursement, _employee, 110m,
            paidAt, _employee.ToString("D"), "BANK-1"));
        (await NewBridge().ReimburseExpenseAsync("expense-user", _tenant, _organization, reimbursement,
            created.Id, created.Version, paidAt, "BANK-1")).Should().BeEquivalentTo(paid);
        (await NewBridge().GetExpenseAsync("expense-reader", _tenant, _organization, created.Id)).Should().BeEquivalentTo(paid);
        (await NewBridge().ListExpensesAsync("expense-reader", _tenant, _organization,
            new(EmployeeId: _employee, Status: ExpenseStatus.Paid))).Items.Single().Should().BeEquivalentTo(paid);
        Scalar<string>("SELECT PAYLOAD FROM ERP_EXPENSE WHERE EXPENSE_ID=@id", new { id = created.Id.ToString("D") })
            .Should().Contain("\"Amounts\":{\"Gross\":110,\"Tax\":10,\"Net\":100}");
        Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_AUDIT WHERE RESOURCE_TYPE='expense'").Should().Be(2);
    }

    [Fact]
    public async Task Split_expense_snapshots_active_members_and_rejects_unowned_references()
    {
        var category = await Category(); var vendor = await Vendor();
        var split = Input(category.Id, vendor.Id, amount: 10m) with { SplitAcrossEmployees = true, Tax = null };
        var created = await _bridge.CreateExpenseAsync("expense-user", _tenant, _organization, Guid.NewGuid(), split);
        created.Allocations.Select(x => x.EmployeeId).Should().Equal(
            new[] { _employee, _secondEmployee, _readerEmployee }.Order());
        created.Allocations.Sum(x => x.Amount).Should().Be(10m);

        await Error(() => _bridge.CreateExpenseAsync("expense-user", _tenant, _organization, Guid.NewGuid(),
            Input(category.Id, vendor.Id, Guid.NewGuid())), "EXPENSE_EMPLOYEE_NOT_FOUND");
        await Error(() => _bridge.CreateExpenseAsync("expense-user", _tenant, _organization, Guid.NewGuid(),
            Input(category.Id, vendor.Id) with { ProjectId = Guid.NewGuid() }), "EXPENSE_PROJECT_NOT_FOUND");
        var project = await NewCrmBridge().CreateProjectAsync("expense-user", _tenant, _organization,
            new("Expense project", Code: "EXP-1"), new(null, [], []));
        var projectExpense = await _bridge.CreateExpenseAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), Input(category.Id, vendor.Id) with { ProjectId = project.Id });
        projectExpense.Input.ProjectId.Should().Be(project.Id);
        await Error(() => _bridge.CreateCategoryAsync("expense-user", _tenant, _organization,
            new("Tagged", [Guid.NewGuid()])), "EXPENSE_TAG_NOT_FOUND");
        await Error(() => _bridge.ReimburseExpenseAsync("expense-user", _tenant, _organization, Guid.NewGuid(),
            created.Id, created.Version, new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero)), "EXPENSE_NOT_REIMBURSABLE");
    }

    [Fact]
    public async Task Scoped_tags_support_lifecycle_paging_and_active_reference_validation()
    {
        var travel = await _bridge.CreateTagAsync("expense-user", _tenant, _organization, new("Travel"));
        var meals = await _bridge.CreateTagAsync("expense-user", _tenant, _organization, new("Meals"));
        await Error(() => _bridge.CreateTagAsync("expense-user", _tenant, _organization, new("travel")),
            "EXPENSE_TAG_NAME_EXISTS");
        await Error(() => _bridge.CreateTagAsync("expense-reader", _tenant, _organization, new("Denied")),
            "BUSINESS_ACCESS_DENIED");

        var page = await _bridge.ListTagsAsync("expense-reader", _tenant, _organization,
            new(Offset: 1, Limit: 1));
        page.Total.Should().Be(2);
        page.Items.Single().Input.Name.Should().Be("Travel");
        (await NewBridge().GetTagAsync("expense-reader", _tenant, _organization, travel.Id))
            .Should().BeEquivalentTo(travel);

        var renamed = await _bridge.UpdateTagAsync("expense-user", _tenant, _organization,
            meals.Id, meals.Version, new("Food"));
        renamed.Input.Name.Should().Be("Food");
        await Error(() => _bridge.UpdateTagAsync("expense-user", _tenant, _organization,
            meals.Id, meals.Version, new("Stale")), "BUSINESS_VERSION_CONFLICT");

        var category = await _bridge.CreateCategoryAsync("expense-user", _tenant, _organization,
            new("Tagged travel", [travel.Id]));
        var vendor = await _bridge.CreateVendorAsync("expense-user", _tenant, _organization,
            new("Tagged rail", TagIds: [travel.Id]));
        var expense = await _bridge.CreateExpenseAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), Input(category.Id, vendor.Id) with { TagIds = [travel.Id] });
        expense.Input.TagIds.Should().Equal(travel.Id);

        var inactive = await _bridge.SetTagActiveAsync("expense-user", _tenant, _organization,
            travel.Id, travel.Version, false);
        (await _bridge.ListTagsAsync("expense-reader", _tenant, _organization)).Total.Should().Be(1);
        (await _bridge.ListTagsAsync("expense-reader", _tenant, _organization,
            new(IncludeInactive: true))).Items.Should().ContainEquivalentOf(inactive);
        await Error(() => _bridge.CreateExpenseAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), Input(category.Id, vendor.Id) with { TagIds = [travel.Id] }),
            "EXPENSE_TAG_NOT_FOUND");
        (await _bridge.GetExpenseAsync("expense-reader", _tenant, _organization, expense.Id))
            .Input.TagIds.Should().Equal(travel.Id);
        Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_AUDIT WHERE RESOURCE_TYPE='expense-tag'")
            .Should().Be(4);
    }

    [Fact]
    public async Task Billable_expense_links_and_unlinks_a_draft_invoice_atomically()
    {
        var category = await Category(); var vendor = await Vendor();
        var contact = await _bridge.EnrollContactAsync("expense-user", _tenant, _organization, "EXP-CUSTOMER");
        var expenseInput = Input(category.Id, vendor.Id, amount: 25.5m) with
        { Type = ExpenseType.BillableToContact, ContactId = contact.Id, Tax = null };
        var expense = await _bridge.CreateExpenseAsync("expense-user", _tenant, _organization, Guid.NewGuid(), expenseInput);
        var invoiceInput = new BillingDocumentInput(contact.Id, new(2026, 9, 23), new(2026, 10, 23), "KRW",
            [new("Base fee", 1m, 1m)]);
        var invoice = await _bridge.CreateDocumentAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), BillingKind.Invoice, invoiceInput);

        var linkOperation = Guid.NewGuid();
        var linked = await _bridge.LinkInvoiceAsync("expense-user", _tenant, _organization, linkOperation,
            expense.Id, expense.Version, invoice.Id, invoice.Version, "Rebill travel");
        linked.Expense.Status.Should().Be(ExpenseStatus.Invoiced); linked.Expense.InvoiceId.Should().Be(invoice.Id);
        linked.Invoice.Input.Lines.Single(x => x.ExpenseId == expense.Id).Should().Be(
            new BillingLine("Rebill travel", 25.5m, 1m, false, false, expense.Id));
        linked.Invoice.Totals.Total.Should().Be(26.5m);
        (await NewBridge().LinkInvoiceAsync("expense-user", _tenant, _organization, linkOperation,
            expense.Id, expense.Version, invoice.Id, invoice.Version, "Rebill travel")).Should().BeEquivalentTo(linked);
        Scalar<string>("SELECT EXPENSE_ID FROM ERP_BILLING_LINE WHERE EXPENSE_ID IS NOT NULL").Should().Be(expense.Id.ToString("D"));

        var unlinkOperation = Guid.NewGuid();
        var unlinked = await NewBridge().UnlinkInvoiceAsync("expense-user", _tenant, _organization, unlinkOperation,
            expense.Id, linked.Expense.Version, invoice.Id, linked.Invoice.Version);
        unlinked.Expense.Status.Should().Be(ExpenseStatus.Uninvoiced); unlinked.Expense.InvoiceId.Should().BeNull();
        unlinked.Invoice.Input.Lines.Should().ContainSingle().Which.ExpenseId.Should().BeNull();
        (await NewBridge().UnlinkInvoiceAsync("expense-user", _tenant, _organization, unlinkOperation,
            expense.Id, linked.Expense.Version, invoice.Id, linked.Invoice.Version)).Should().BeEquivalentTo(unlinked);
    }

    [Fact]
    public async Task Automatic_billing_claims_uninvoiced_expenses_once_and_replays_after_restart()
    {
        var category = await Category(); var vendor = await Vendor();
        var contact = await _bridge.EnrollContactAsync("expense-user", _tenant, _organization, "EXP-CUSTOMER");
        async Task<ExpenseRecord> Expense(decimal amount, DateOnly date, string purpose)
            => await _bridge.CreateExpenseAsync("expense-user", _tenant, _organization, Guid.NewGuid(),
                Input(category.Id, vendor.Id, amount: amount) with
                {
                    Type = ExpenseType.BillableToContact,
                    ContactId = contact.Id,
                    ValueDate = date,
                    Purpose = purpose,
                    Tax = null
                });
        var first = await Expense(25.5m, new(2026, 9, 20), "Rail");
        var second = await Expense(10m, new(2026, 9, 21), "Meal");
        await Expense(99m, new(2026, 8, 31), "Outside period");
        var operation = Guid.NewGuid();
        var request = new AutomaticBillingRequest(contact.Id, BillingInvoiceType.DetailedItems,
            new(2026, 9, 1), new(2026, 9, 30), new(2026, 9, 30), new(2026, 10, 30), "KRW");
        var generated = await _bridge.GenerateAutomaticInvoiceAsync("expense-user", _tenant, _organization,
            operation, request);
        generated.Document.Totals.Total.Should().Be(35.5m);
        generated.Document.Input.Lines.Select(line => (line.Description, line.ExpenseId)).Should()
            .Equal(("Rail", (Guid?)first.Id), ("Meal", (Guid?)second.Id));
        generated.Generation.Sources.Select(source => source.SourceId).Should().Equal(first.Id, second.Id);
        var replay = await NewBridge().GenerateAutomaticInvoiceAsync("expense-user", _tenant, _organization,
            operation, request);
        replay.Document.Id.Should().Be(generated.Document.Id);
        replay.Generation.Id.Should().Be(generated.Generation.Id);
        var linkedFirst = await NewBridge().GetExpenseAsync("expense-user", _tenant, _organization, first.Id);
        linkedFirst.Should().Match<ExpenseRecord>(value => value.Status == ExpenseStatus.Invoiced
            && value.InvoiceId == generated.Document.Id && value.InvoiceOperationId == operation);
        Scalar<long>("SELECT COUNT(*) FROM ERP_AUTOMATIC_BILLING_SOURCE").Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM ERP_AUTOMATIC_BILLING_GENERATION").Should().Be(1);
        await Error(() => NewBridge().GenerateAutomaticInvoiceAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), request), "AUTOMATIC_BILLING_SOURCE_NOT_FOUND");
        await Error(() => NewBridge().GenerateAutomaticInvoiceAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), request with { InvoiceType = BillingInvoiceType.ByProducts }),
            "AUTOMATIC_BILLING_SOURCE_NOT_FOUND");
        await Error(() => NewBridge().GenerateAutomaticInvoiceAsync("expense-reader", _tenant, _organization,
            Guid.NewGuid(), request), "BUSINESS_ACCESS_DENIED");
        var unlinked = await NewBridge().UnlinkInvoiceAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), first.Id, linkedFirst.Version, generated.Document.Id, generated.Document.Version);
        unlinked.Expense.Status.Should().Be(ExpenseStatus.Uninvoiced);
        await Error(() => NewBridge().GenerateAutomaticInvoiceAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), request), "AUTOMATIC_BILLING_SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Scope_permissions_directory_lifecycle_cas_and_cancellation_are_enforced()
    {
        var category = await Category("Meals"); var vendor = await Vendor("Cafe");
        var scopes = await ((NexaOne.ServiceContracts.Erp.IExpenseBridge)_bridge).ListAccessibleScopesAsync("expense-user");
        scopes.Total.Should().Be(1); scopes.Items.Single().OrganizationId.Should().Be(_organization);
        var employees = await _bridge.ListEmployeesAsync("expense-user", _tenant, _organization);
        employees.Total.Should().Be(3);
        employees.Items.Select(value => value.UserId).Should()
            .BeEquivalentTo("expense-user", "expense-peer", "expense-reader");
        await Error(() => _bridge.ListEmployeesAsync("expense-reader", _tenant, _organization),
            "BUSINESS_ACCESS_DENIED");
        await Error(() => _bridge.CreateVendorAsync("expense-reader", _tenant, _organization, new("Denied")),
            "BUSINESS_ACCESS_DENIED");
        (await _bridge.ListVendorsAsync("expense-reader", _tenant, _organization)).Items.Single().Should().BeEquivalentTo(vendor);

        var inactive = await _bridge.SetCategoryActiveAsync("expense-user", _tenant, _organization,
            category.Id, category.Version, false);
        await Error(() => _bridge.SetCategoryActiveAsync("expense-user", _tenant, _organization,
            category.Id, category.Version, true), "BUSINESS_VERSION_CONFLICT");
        (await _bridge.ListCategoriesAsync("expense-reader", _tenant, _organization)).Total.Should().Be(0);
        (await _bridge.ListCategoriesAsync("expense-reader", _tenant, _organization,
            new(IncludeInactive: true))).Items.Single().Should().BeEquivalentTo(inactive);

        var active = await _bridge.SetCategoryActiveAsync("expense-user", _tenant, _organization,
            category.Id, inactive.Version, true);
        var expense = await _bridge.CreateExpenseAsync("expense-user", _tenant, _organization,
            Guid.NewGuid(), Input(active.Id, vendor.Id));
        var cancelled = await _bridge.CancelExpenseAsync("expense-user", _tenant, _organization,
            expense.Id, expense.Version, "duplicate");
        cancelled.State.Should().Be(ExpenseState.Cancelled); cancelled.CancelledBy.Should().Be(_employee.ToString("D"));
        (await NewBridge().CancelExpenseAsync("expense-user", _tenant, _organization,
            expense.Id, expense.Version, "ignored on replay")).Should().BeEquivalentTo(cancelled);
        await Error(() => _bridge.UpdateExpenseAsync("expense-user", _tenant, _organization,
            expense.Id, cancelled.Version, Input(active.Id, vendor.Id, amount: 2m)), "EXPENSE_LOCKED");
    }

    [Fact]
    public async Task Directory_queries_filter_literal_text_order_and_page_in_storage()
    {
        var names = new[] { "Zulu", "alpha", "Alpha%_[]", "Beta", "Inactive" };
        var categories = new List<ExpenseCategory>();
        foreach (var name in names) categories.Add(await Category(name));
        var inactive = categories.Single(x => x.Input.Name == "Inactive");
        await _bridge.SetCategoryActiveAsync("expense-user", _tenant, _organization,
            inactive.Id, inactive.Version, false);

        var page = await _bridge.ListCategoriesAsync("expense-reader", _tenant, _organization,
            new(Offset: 1, Limit: 2));
        page.Total.Should().Be(4);
        page.Items.Select(x => x.Input.Name).Should().Equal("Beta", "Zulu");

        var literal = await _bridge.ListCategoriesAsync("expense-reader", _tenant, _organization,
            new(Text: "%_[", IncludeInactive: true));
        literal.Total.Should().Be(1);
        literal.Items.Single().Input.Name.Should().Be("Alpha%_[]");
    }
}
