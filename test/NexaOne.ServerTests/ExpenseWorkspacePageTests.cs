using System.Reflection;
using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Components.Pages;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class ExpenseWorkspacePageTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly Action<string> _changeUser;
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly ExpenseCategory Category = new(Guid.Parse("30000000-0000-0000-0000-000000000001"),
        new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), Guid.NewGuid(), new("Travel"));
    private static readonly ExpenseVendor Vendor = new(Guid.Parse("40000000-0000-0000-0000-000000000001"),
        new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), Guid.NewGuid(), new("Rail"));

    public ExpenseWorkspacePageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var authorization = this.AddAuthorization();
        authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, "operator"));
        authorization.SetAuthorized("operator");
        _changeUser = name => authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, name));
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new UiTextService());
        Read<BusinessMembership>(_ => new([], 0));
        Read<ExpenseCategory>(_ => new([], 0));
        Read<ExpenseVendor>(_ => new([], 0));
        Read<ExpenseRecord>(_ => new([], 0));
        Read<BillingDocument>(_ => new([], 0));
    }

    [Fact]
    public void Empty_scope_list_is_a_successful_state()
    {
        var cut = Render<HostExpenseWorkspace>();

        cut.WaitForAssertion(() => Paths<BusinessMembership>().Should()
            .Equal("api/v1/erp/expenses/scopes/me?offset=0&limit=50"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
        cut.Find("[data-empty]").TextContent.Should().Contain("접근 가능한 비용 범위가 없습니다");
    }

    [Fact]
    public void Selected_scope_loads_only_permitted_directories_and_ledger()
    {
        ShowScope("expense.directory.read", "expense.read");
        Read<ExpenseCategory>(_ => new([Category], 1));
        Read<ExpenseVendor>(_ => new([Vendor], 1));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());

        cut.Find("[data-scope]").Click();

        cut.WaitForAssertion(() => cut.Find("#expense-list-heading").Should().NotBeNull());
        Paths<ExpenseCategory>().Should().Equal($"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/categories?offset=0&limit=50");
        Paths<ExpenseVendor>().Should().Equal($"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/vendors?offset=0&limit=50");
        Paths<ExpenseRecord>().Should().Equal($"api/v1/erp/expenses/{Tenant:D}/{Organization:D}?offset=0&limit=50");
        cut.FindAll("#expense-create").Should().BeEmpty("expense.write is absent");
        cut.FindAll("[data-edit-category], [data-edit-vendor]").Should().BeEmpty("expense.directory.write is absent");
    }

    [Fact]
    public void Ledger_filters_and_paging_use_the_server_query_and_clear_stale_selection()
    {
        ShowScope("expense.directory.read", "expense.read");
        Read<ExpenseCategory>(_ => new([Category], 1));
        Read<ExpenseVendor>(_ => new([Vendor], 1));
        var first = Expense(Guid.NewGuid(), Input(ExpenseType.NotTaxDeductible, Guid.NewGuid()));
        var second = Expense(Guid.NewGuid(), Input(ExpenseType.BillableToContact, Guid.NewGuid()),
            ExpenseStatus.Uninvoiced);
        var filtered = Expense(Guid.NewGuid(), Input(ExpenseType.TaxDeductible, Guid.NewGuid()),
            ExpenseStatus.Paid);
        Read<ExpenseRecord>(path => path.Contains("start=2026-01-01", StringComparison.Ordinal)
            ? new([filtered], 1)
            : path.Contains("offset=50", StringComparison.Ordinal) ? new([second], 51) : new([first], 51));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-ledger-next").Should().NotBeNull());

        cut.Find("#expense-ledger-next").Click();

        var root = $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}";
        cut.WaitForAssertion(() => Paths<ExpenseRecord>().Last().Should().Be($"{root}?offset=50&limit=50"));
        cut.Find("[data-select-expense]").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-selected-heading").Should().NotBeNull());

        cut.Find("#expense-ledger-start").Change("2026-01-01");
        cut.Find("#expense-ledger-end").Change("2026-12-31");
        cut.Find("#expense-ledger-category").Change(Category.Id.ToString("D"));
        cut.Find("#expense-ledger-vendor").Change(Vendor.Id.ToString("D"));
        cut.Find("#expense-ledger-type").Change(ExpenseType.TaxDeductible.ToString());
        cut.Find("#expense-ledger-status").Change(ExpenseStatus.Paid.ToString());
        cut.Find("#expense-ledger-state").Change(ExpenseState.Active.ToString());
        cut.Find("#expense-ledger-search").Click();

        cut.WaitForAssertion(() => Paths<ExpenseRecord>().Last().Should().Be(root
            + $"?start=2026-01-01&end=2026-12-31&categoryId={Category.Id:D}&vendorId={Vendor.Id:D}"
            + "&type=TaxDeductible&status=Paid&state=Active&offset=0&limit=50"));
        cut.FindAll("#expense-selected-heading").Should().BeEmpty("changing the page contract clears stale detail");
        cut.Find("#expense-ledger-previous").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".expense-ledger-pages [data-total]").TextContent.Should().Contain("1–1 / 1");

        cut.Find("#expense-ledger-clear").Click();

        cut.WaitForAssertion(() => Paths<ExpenseRecord>().Last().Should().Be($"{root}?offset=0&limit=50"));
        cut.Find("#expense-ledger-start").GetAttribute("value").Should().BeEmpty();
    }

    [Fact]
    public void Invalid_ledger_date_range_is_rejected_without_a_request()
    {
        ShowScope("expense.read");
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => Paths<ExpenseRecord>().Should().HaveCount(1));

        cut.Find("#expense-ledger-start").Change("2026-01-01");
        cut.Find("#expense-ledger-search").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("시작일과 종료일을 함께"));
        Paths<ExpenseRecord>().Should().HaveCount(1);
    }

    [Fact]
    public void Refresh_clamps_a_page_that_no_longer_exists()
    {
        ShowScope("expense.read");
        var item = Expense(Guid.NewGuid(), Input(ExpenseType.TaxDeductible, Guid.NewGuid()));
        var secondPageReads = 0;
        Read<ExpenseRecord>(path =>
        {
            if (!path.Contains("offset=50", StringComparison.Ordinal)) return new([item], 1);
            secondPageReads++;
            return secondPageReads == 1 ? new([item], 51) : new([], 1);
        });
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-ledger-next").Should().NotBeNull());

        // Simulate a previously full first page by returning a total that enables the next button.
        Read<ExpenseRecord>(path =>
        {
            if (path.Contains("offset=50", StringComparison.Ordinal))
            {
                secondPageReads++;
                return secondPageReads == 1 ? new([item], 51) : new([], 1);
            }
            return new([item], secondPageReads == 0 ? 51 : 1);
        });
        cut.Find("#expense-list-refresh").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-ledger-next").HasAttribute("disabled").Should().BeFalse());
        cut.Find("#expense-ledger-next").Click();
        cut.WaitForAssertion(() => Paths<ExpenseRecord>().Last().Should().Contain("offset=50"));

        cut.Find("#expense-list-refresh").Click();

        cut.WaitForAssertion(() => Paths<ExpenseRecord>().TakeLast(2).Should().Equal(
            $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}?offset=50&limit=50",
            $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}?offset=0&limit=50"));
        cut.Find(".expense-ledger-pages [data-total]").TextContent.Should().Contain("1–1 / 1");
    }

    [Fact]
    public void Category_edit_uses_current_version_and_preserves_unedited_tags()
    {
        ShowScope("expense.directory.read", "expense.directory.write");
        var tag = Guid.NewGuid();
        var category = Category with { Input = new("Travel", [tag]) };
        Read<ExpenseCategory>(_ => new([category], 1));
        _api.Setup(api => api.WriteInventoryAsync<ExpenseCategory>(HttpMethod.Put,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/categories/{category.Id:D}",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                Property<Guid>(body, "Version").Should().Be(category.Version);
                var input = Property<ExpenseCategoryInput>(body, "Input");
                input.Name.Should().Be("Business travel");
                input.TagIds.Should().Equal(tag);
                return Task.FromResult<(ExpenseCategory?, int, string?, string?)>(
                    (category with { Version = Guid.NewGuid(), Input = input }, 200, null, null));
            });
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-edit-category]").Should().NotBeNull());

        cut.Find("[data-edit-category]").Click();
        cut.Find("#expense-category-edit-name").Change("Business travel");
        cut.Find("#expense-category-save").Click();

        cut.WaitForAssertion(() => cut.Find("[data-edit-category]").ParentElement!.TextContent
            .Should().Contain("Business travel"));
        cut.Find("[role=status]").TextContent.Should().Contain("카테고리를 수정했습니다");
    }

    [Fact]
    public void Vendor_deactivation_keeps_history_visible_but_removes_new_expense_choice()
    {
        ShowScope("expense.directory.read", "expense.directory.write", "expense.write");
        Read<ExpenseCategory>(_ => new([Category], 1));
        Read<ExpenseVendor>(_ => new([Vendor], 1));
        _api.Setup(api => api.WriteInventoryAsync<ExpenseVendor>(HttpMethod.Post,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/vendors/{Vendor.Id:D}/active",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                Property<Guid>(body, "Version").Should().Be(Vendor.Version);
                Property<bool>(body, "Active").Should().BeFalse();
                return Task.FromResult<(ExpenseVendor?, int, string?, string?)>(
                    (Vendor with { Version = Guid.NewGuid(), Active = false }, 200, null, null));
            });
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-edit-vendor]").Should().NotBeNull());

        cut.Find("[data-edit-vendor]").Click();
        cut.Find("#expense-vendor-active").Click();

        cut.WaitForAssertion(() => cut.Find("[data-edit-vendor]").ParentElement!.TextContent
            .Should().Contain("비활성"));
        cut.Find("#expense-vendor").TextContent.Should().NotContain("Rail");
        cut.Find("#expense-vendor-active").TextContent.Should().Contain("재활성화");
    }

    [Fact]
    public void Version_conflict_reloads_latest_category_without_reporting_success()
    {
        ShowScope("expense.directory.read", "expense.directory.write");
        Read<ExpenseCategory>(_ => new([Category], 1));
        var latest = Category with { Version = Guid.NewGuid(), Input = new("Peer edit") };
        ReadOne<ExpenseCategory>(path => path.EndsWith(Category.Id.ToString("D"), StringComparison.Ordinal)
            ? latest : null);
        _api.Setup(api => api.WriteInventoryAsync<ExpenseCategory>(HttpMethod.Put,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/categories/{Category.Id:D}",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((null, 409, "BUSINESS_VERSION_CONFLICT", "version conflict"));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-edit-category]").Should().NotBeNull());

        cut.Find("[data-edit-category]").Click();
        cut.Find("#expense-category-edit-name").Change("My edit");
        cut.Find("#expense-category-save").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("version conflict"));
        cut.Find("#expense-category-edit-name").GetAttribute("value").Should().Be("Peer edit");
        cut.FindAll("[role=status]").Should().NotContain(element =>
            element.TextContent.Contains("카테고리를 수정했습니다", StringComparison.Ordinal));
    }

    [Fact]
    public void Uncertain_vendor_update_recovers_committed_state_from_single_read()
    {
        ShowScope("expense.directory.read", "expense.directory.write");
        var vendor = Vendor with { Input = new("Rail", Phone: "010-0000-0000") };
        var committed = vendor with { Version = Guid.NewGuid(), Input = vendor.Input with { Name = "Metro" } };
        Read<ExpenseVendor>(_ => new([vendor], 1));
        ReadOne<ExpenseVendor>(path => path.EndsWith(vendor.Id.ToString("D"), StringComparison.Ordinal)
            ? committed : null);
        _api.Setup(api => api.WriteInventoryAsync<ExpenseVendor>(HttpMethod.Put,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/vendors/{vendor.Id:D}",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((null, 503, "INVENTORY_RESPONSE_UNAVAILABLE", "unknown outcome"));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-edit-vendor]").Should().NotBeNull());

        cut.Find("[data-edit-vendor]").Click();
        cut.Find("#expense-vendor-edit-name").Change("Metro");
        cut.Find("#expense-vendor-save").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role=alert]").Should().BeEmpty());
        cut.Find("[role=status]").TextContent.Should().Contain("저장된 기준정보 결과를 확인했습니다");
        cut.Find("#expense-vendor-edit-phone").GetAttribute("value").Should().Be("010-0000-0000");
    }

    [Fact]
    public void Uncertain_create_reuses_operation_and_recovers_from_ledger()
    {
        ShowScope("expense.directory.read", "expense.read", "expense.write");
        Read<ExpenseCategory>(_ => new([Category], 1));
        Read<ExpenseVendor>(_ => new([Vendor], 1));
        Guid? firstOperation = null;
        ExpenseInput? firstInput = null;
        var writes = 0;
        _api.Setup(api => api.WriteInventoryAsync<ExpenseRecord>(HttpMethod.Post,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}", It.IsAny<object>(), "operator",
                It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                var operation = Property<Guid>(body, "OperationId");
                var input = Property<ExpenseInput>(body, "Input");
                firstOperation ??= operation;
                firstInput ??= input;
                operation.Should().Be(firstOperation.Value);
                input.Should().Be(firstInput);
                writes++;
                return Task.FromResult<(ExpenseRecord?, int, string?, string?)>(
                    (null, 503, "INVENTORY_RESPONSE_UNAVAILABLE", "unknown outcome"));
            });
        _api.Setup(api => api.ReadInventoryAsync<BusinessPage<ExpenseRecord>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => Task.FromResult<(BusinessPage<ExpenseRecord>?, int, string?, string?)>(
                (writes < 2 || firstOperation is null || firstInput is null
                    ? new([], 0) : new([Expense(firstOperation.Value, firstInput with { TagIds = Array.Empty<Guid>() })], 1), 200, null, null)));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-create").Should().NotBeNull());
        cut.Find("#expense-amount").Change("12500");
        cut.Find("#expense-category").Change(Category.Id.ToString("D"));
        cut.Find("#expense-vendor").Change(Vendor.Id.ToString("D"));

        cut.Find("#expense-create").Click();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("unknown outcome"));
        cut.Find("#expense-amount").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#expense-create").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role=alert]").Should().BeEmpty());
        cut.Find("#expense-amount").HasAttribute("disabled").Should().BeFalse();
        writes.Should().Be(2);
    }

    [Fact]
    public void Paid_expense_hides_every_invalid_lifecycle_action()
    {
        ShowScope("expense.read", "expense.write", "expense.reimburse");
        var input = Input(ExpenseType.TaxDeductible, Guid.NewGuid());
        Read<ExpenseRecord>(_ => new([Expense(Guid.NewGuid(), input, ExpenseStatus.Paid)], 1));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());

        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-expense]").Should().NotBeNull());
        cut.Find("[data-select-expense]").Click();

        cut.FindAll("#expense-mark-invoiced, #expense-mark-paid, #expense-reimburse, #expense-cancel")
            .Should().BeEmpty();
    }

    [Fact]
    public void Uninvoiced_billable_expense_offers_only_invoice_and_cancel_actions()
    {
        ShowScope("expense.read", "expense.write", "expense.reimburse");
        var input = Input(ExpenseType.BillableToContact, Guid.NewGuid());
        Read<ExpenseRecord>(_ => new([Expense(Guid.NewGuid(), input, ExpenseStatus.Uninvoiced)], 1));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());

        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-expense]").Should().NotBeNull());
        cut.Find("[data-select-expense]").Click();

        cut.Find("#expense-mark-invoiced").Should().NotBeNull();
        cut.Find("#expense-cancel").Should().NotBeNull();
        cut.FindAll("#expense-mark-paid, #expense-reimburse").Should().BeEmpty();
    }

    [Fact]
    public void Invoice_link_retry_reuses_operation_and_both_versions()
    {
        ShowScope("expense.read", "expense.invoice", "billing.read");
        var input = Input(ExpenseType.BillableToContact, Guid.NewGuid());
        var expense = Expense(Guid.NewGuid(), input, ExpenseStatus.Uninvoiced);
        var invoice = Invoice(input.ContactId!.Value);
        Read<ExpenseRecord>(_ => new([expense], 1));
        Read<BillingDocument>(_ => new([invoice], 1));
        ReadOne<ExpenseRecord>(path => path.EndsWith(expense.Id.ToString("D"), StringComparison.Ordinal)
            ? expense : null);
        Guid? operationId = null;
        var writes = 0;
        _api.Setup(api => api.WriteInventoryAsync<ExpenseInvoiceLink>(HttpMethod.Post,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/{expense.Id:D}/invoice-link",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                var operation = Property<Guid>(body, "OperationId");
                operationId ??= operation;
                operation.Should().Be(operationId.Value);
                Property<Guid>(body, "ExpenseVersion").Should().Be(expense.Version);
                Property<Guid>(body, "InvoiceId").Should().Be(invoice.Id);
                Property<Guid>(body, "InvoiceVersion").Should().Be(invoice.Version);
                Property<string?>(body, "Description").Should().Be(input.Purpose);
                writes++;
                if (writes == 1)
                    return Task.FromResult<(ExpenseInvoiceLink?, int, string?, string?)>(
                        (null, 503, "INVENTORY_RESPONSE_UNAVAILABLE", "unknown outcome"));
                var linkedExpense = expense with
                {
                    Version = Guid.NewGuid(), Status = ExpenseStatus.Invoiced,
                    InvoiceId = invoice.Id, InvoiceOperationId = operation
                };
                var linkedInvoice = invoice with
                {
                    Version = Guid.NewGuid(),
                    Input = invoice.Input with
                    {
                        Lines = [.. invoice.Input.Lines, new BillingLine(input.Purpose!, input.Amount, 1m,
                            ExpenseId: expense.Id)]
                    }
                };
                return Task.FromResult<(ExpenseInvoiceLink?, int, string?, string?)>(
                    (new(linkedExpense, linkedInvoice), 200, null, null));
            });
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-expense]").Should().NotBeNull());
        cut.Find("[data-select-expense]").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-invoice-link").Should().NotBeNull());

        cut.Find("#expense-invoice-link").Click();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("unknown outcome"));
        cut.Find("#expense-invoice-select").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#expense-invoice-link").Click();

        cut.WaitForAssertion(() => cut.Find(".expense-invoice-current").TextContent.Should().Contain($"#{invoice.Number}"));
        cut.Find("[role=status]").TextContent.Should().Contain("비용을 청구서에 연결했습니다");
        writes.Should().Be(2);
    }

    [Fact]
    public void Draft_invoice_can_be_unlinked_with_current_versions()
    {
        ShowScope("expense.read", "expense.invoice", "billing.read");
        var input = Input(ExpenseType.BillableToContact, Guid.NewGuid());
        var invoiceOperation = Guid.NewGuid();
        var invoice = Invoice(input.ContactId!.Value);
        var expense = Expense(Guid.NewGuid(), input, ExpenseStatus.Invoiced) with
        {
            InvoiceId = invoice.Id, InvoiceOperationId = invoiceOperation
        };
        invoice = invoice with
        {
            Input = invoice.Input with
            {
                Lines = [.. invoice.Input.Lines, new BillingLine(input.Purpose!, input.Amount, 1m,
                    ExpenseId: expense.Id)]
            }
        };
        Read<ExpenseRecord>(_ => new([expense], 1));
        ReadOne<BillingDocument>(path => path.EndsWith(invoice.Id.ToString("D"), StringComparison.Ordinal)
            ? invoice : null);
        _api.Setup(api => api.WriteInventoryAsync<ExpenseInvoiceLink>(HttpMethod.Post,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/{expense.Id:D}/invoice-unlink",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                var operation = Property<Guid>(body, "OperationId");
                Property<Guid>(body, "ExpenseVersion").Should().Be(expense.Version);
                Property<Guid>(body, "InvoiceId").Should().Be(invoice.Id);
                Property<Guid>(body, "InvoiceVersion").Should().Be(invoice.Version);
                var unlinkedExpense = expense with
                {
                    Version = Guid.NewGuid(), Status = ExpenseStatus.Uninvoiced, InvoiceId = null,
                    InvoiceOperationId = null, UnlinkedInvoiceId = invoice.Id,
                    InvoiceUnlinkOperationId = operation
                };
                var unlinkedInvoice = invoice with
                {
                    Version = Guid.NewGuid(),
                    Input = invoice.Input with
                    {
                        Lines = invoice.Input.Lines.Where(line => line.ExpenseId != expense.Id).ToArray()
                    }
                };
                return Task.FromResult<(ExpenseInvoiceLink?, int, string?, string?)>(
                    (new(unlinkedExpense, unlinkedInvoice), 200, null, null));
            });
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-expense]").Should().NotBeNull());
        cut.Find("[data-select-expense]").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-invoice-unlink").Should().NotBeNull());

        cut.Find("#expense-invoice-unlink").Click();

        cut.WaitForAssertion(() => cut.Find("#expense-invoices-load").Should().NotBeNull());
        cut.Find("[role=status]").TextContent.Should().Contain("연결을 해제했습니다");
    }

    [Fact]
    public void Uncertain_invoice_link_recovers_an_already_committed_result()
    {
        ShowScope("expense.read", "expense.invoice", "billing.read");
        var input = Input(ExpenseType.BillableToContact, Guid.NewGuid());
        var expense = Expense(Guid.NewGuid(), input, ExpenseStatus.Uninvoiced);
        var invoice = Invoice(input.ContactId!.Value);
        Read<ExpenseRecord>(_ => new([expense], 1));
        Read<BillingDocument>(_ => new([invoice], 1));
        ExpenseRecord? committedExpense = null;
        BillingDocument? committedInvoice = null;
        ReadOne<ExpenseRecord>(_ => committedExpense);
        ReadOne<BillingDocument>(_ => committedInvoice);
        var writes = 0;
        _api.Setup(api => api.WriteInventoryAsync<ExpenseInvoiceLink>(HttpMethod.Post,
                $"api/v1/erp/expenses/{Tenant:D}/{Organization:D}/{expense.Id:D}/invoice-link",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                var operation = Property<Guid>(body, "OperationId");
                committedExpense = expense with
                {
                    Version = Guid.NewGuid(), Status = ExpenseStatus.Invoiced,
                    InvoiceId = invoice.Id, InvoiceOperationId = operation
                };
                committedInvoice = invoice with
                {
                    Version = Guid.NewGuid(),
                    Input = invoice.Input with
                    {
                        Lines = [.. invoice.Input.Lines, new BillingLine(input.Purpose!, input.Amount, 1m,
                            ExpenseId: expense.Id)]
                    }
                };
                writes++;
                return Task.FromResult<(ExpenseInvoiceLink?, int, string?, string?)>(
                    (null, 503, "INVENTORY_RESPONSE_UNAVAILABLE", "unknown outcome"));
            });
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-expense]").Should().NotBeNull());
        cut.Find("[data-select-expense]").Click();
        cut.WaitForAssertion(() => cut.Find("#expense-invoice-link").Should().NotBeNull());

        cut.Find("#expense-invoice-link").Click();

        cut.WaitForAssertion(() => cut.Find(".expense-invoice-current").TextContent.Should().Contain($"#{invoice.Number}"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
        writes.Should().Be(1);
    }

    [Fact]
    public void Invoice_permission_without_billing_read_explains_the_missing_capability()
    {
        ShowScope("expense.read", "expense.invoice");
        var input = Input(ExpenseType.BillableToContact, Guid.NewGuid());
        Read<ExpenseRecord>(_ => new([Expense(Guid.NewGuid(), input, ExpenseStatus.Uninvoiced)], 1));
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-expense]").Should().NotBeNull());
        cut.Find("[data-select-expense]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role=status]").Should()
            .Contain(element => element.TextContent.Contains("billing.read", StringComparison.Ordinal)));
        Paths<BillingDocument>().Should().BeEmpty();
        cut.FindAll("#expense-invoice-link, #expense-invoice-unlink").Should().BeEmpty();
    }

    [Fact]
    public async Task Authentication_change_rejects_a_late_scope_response()
    {
        var oldScope = Scope("operator", Tenant, Organization, "expense.read");
        var replacementOrganization = Guid.NewGuid();
        var newScope = Scope("replacement", Tenant, replacementOrganization, "expense.read");
        var pending = new TaskCompletionSource<(BusinessPage<BusinessMembership>?, int, string?, string?)>();
        CancellationToken oldToken = default;
        var calls = 0;
        _api.Setup(api => api.ReadInventoryAsync<BusinessPage<BusinessMembership>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken token) =>
            {
                if (calls++ == 0) { oldToken = token; return pending.Task; }
                return Task.FromResult<(BusinessPage<BusinessMembership>?, int, string?, string?)>(
                    (new([newScope], 1), 200, null, null));
            });
        var cut = Render<HostExpenseWorkspace>();
        cut.WaitForAssertion(() => oldToken.CanBeCanceled.Should().BeTrue());

        await cut.InvokeAsync(() => _changeUser("replacement"));
        cut.WaitForAssertion(() => oldToken.IsCancellationRequested.Should().BeTrue());
        pending.SetResult((new([oldScope], 1), 200, null, null));

        cut.WaitForAssertion(() => cut.Find("[data-scope]").GetAttribute("data-scope").Should()
            .Be($"{Tenant:D}/{replacementOrganization:D}"));
    }

    private void ShowScope(params string[] permissions)
        => Read<BusinessMembership>(_ => new([Scope("operator", Tenant, Organization, permissions)], 1));

    private static BusinessMembership Scope(string user, Guid tenant, Guid organization, params string[] permissions)
        => new(tenant, organization, user, Guid.NewGuid(), true, 1, permissions);

    private void Read<T>(Func<string, BusinessPage<T>> response)
        => _api.Setup(api => api.ReadInventoryAsync<BusinessPage<T>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult<(BusinessPage<T>?, int, string?, string?)>((response(path), 200, null, null)));

    private void ReadOne<T>(Func<string, T?> response) where T : class
        => _api.Setup(api => api.ReadInventoryAsync<T>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult<(T?, int, string?, string?)>((response(path), 200, null, null)));

    private string[] Paths<T>() => _api.Invocations.Where(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync)
        && call.Method.GetGenericArguments()[0] == typeof(BusinessPage<T>)).Select(call => call.Arguments[0]).OfType<string>().ToArray();

    private static T Property<T>(object value, string name)
        => (T)(value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(value) ?? throw new InvalidOperationException(name));

    private static ExpenseInput Input(ExpenseType type, Guid employeeId) => new(12500m, type,
        Category.Id, Vendor.Id, employeeId, type == ExpenseType.BillableToContact ? Guid.NewGuid() : null,
        null, "KRW", new DateOnly(2026, 9, 25), "Travel");

    private static ExpenseRecord Expense(Guid operationId, ExpenseInput input,
        ExpenseStatus status = ExpenseStatus.NotBillable) => new(Guid.NewGuid(),
        new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), Guid.NewGuid(), operationId,
        input, "operator", status, ExpenseState.Active)
    { CreationInput = input, Amounts = new(input.Amount, 0, input.Amount) };

    private static BillingDocument Invoice(Guid contactId) => new(Guid.NewGuid(),
        new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), Guid.NewGuid(), Guid.NewGuid(),
        BillingKind.Invoice, 42, new(contactId, new(2026, 9, 25), new(2026, 10, 25), "KRW",
            [new("Service", 100m, 1m)]), new(100m, 0m, 0m, 100m), BillingStatus.Draft, "operator");
}
