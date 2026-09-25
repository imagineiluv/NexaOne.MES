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

    private string[] Paths<T>() => _api.Invocations.Where(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync)
        && call.Method.GetGenericArguments()[0] == typeof(BusinessPage<T>)).Select(call => call.Arguments[0]).OfType<string>().ToArray();

    private static T Property<T>(object value, string name)
        => (T)(value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(value) ?? throw new InvalidOperationException(name));

    private static ExpenseInput Input(ExpenseType type, Guid employeeId) => new(12500m, type,
        Category.Id, Vendor.Id, employeeId, type == ExpenseType.BillableToContact ? Guid.NewGuid() : null,
        null, "KRW", new DateOnly(2026, 9, 25));

    private static ExpenseRecord Expense(Guid operationId, ExpenseInput input,
        ExpenseStatus status = ExpenseStatus.NotBillable) => new(Guid.NewGuid(),
        new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), Guid.NewGuid(), operationId,
        input, "operator", status, ExpenseState.Active)
    { CreationInput = input, Amounts = new(input.Amount, 0, input.Amount) };
}
