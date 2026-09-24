using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Components.Pages;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Radzen;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class BillingWorkspaceTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly UiTextService _ui = new();
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public BillingWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        var authorization = this.AddAuthorization();
        authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, "operator"));
        authorization.SetAuthorized("operator");
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_ui);
        Services.AddSingleton(new ProtectedSessionStorage(new InventorySessionStorageJs(), new EphemeralDataProtectionProvider()));
        Reads<BusinessMembership>(_ => new([], 0));
        Reads<BillingContact>(_ => new([], 0));
        Reads<BillingDocument>(_ => new([], 0));
        Reads<PaymentRecord>(_ => new([], 0));
    }

    [Fact]
    public void No_scopes_is_a_successful_empty_state()
    {
        var cut = Render<HostBillingWorkspace>();
        cut.WaitForAssertion(() => cut.Find("#billing-scopes [data-empty]").TextContent.Should().Contain("접근 가능한"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
        Paths<BusinessMembership>().Should().Equal("api/v1/erp/billing/scopes/me?offset=0&limit=50");
    }

    [Theory]
    [InlineData("billing.read", true)]
    [InlineData("billing.write", false)]
    public void Read_grant_exposes_contact_and_document_panes(string grant, bool readable)
    {
        ShowScopes(Scope(1, grant));
        var cut = Render<HostBillingWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();

        cut.FindAll("section#billing-contacts").Count.Should().Be(readable ? 1 : 0);
        cut.FindAll("section#billing-documents").Count.Should().Be(readable ? 1 : 0);
        cut.FindAll("section#billing-payments").Should().BeEmpty("payments are keyed by a selected document");
        cut.FindAll("#billing-no-read-access").Count.Should().Be(readable ? 0 : 1);
        cut.FindAll("#billing-workflows").Should().ContainSingle();
        if (readable)
            Paths<BillingDocument>().Should().Equal($"api/v1/erp/billing/{Tenant:D}/{Scope(1, grant).OrganizationId:D}/documents?offset=0&limit=50");
    }

    [Fact]
    public void Document_filters_and_row_selection_drive_the_panel_and_the_payment_pane()
    {
        var scope = Scope(1, "billing.read", "billing.pay");
        ShowScopes(scope);
        var invoice = Document(scope, BillingKind.Invoice, 1, BillingStatus.Sent);
        var estimate = Document(scope, BillingKind.Estimate, 1, BillingStatus.Draft);
        Reads<BillingDocument>(path => path.Contains("kind=Invoice") ? new([invoice], 1) : new([estimate, invoice], 2));
        var payment = new PaymentRecord(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(),
            new(invoice.Id, 4m, "KRW", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), PaymentMethod.Cash), Guid.NewGuid().ToString("D"), PaymentState.Recorded);
        Reads<PaymentRecord>(_ => new([payment], 1));
        var cut = Render<HostBillingWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-select-document]").Should().HaveCount(2));

        cut.Find("#document-kind").Change("Invoice");
        cut.Find("#billing-documents-search").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-select-document]").Should().ContainSingle());
        Paths<BillingDocument>().Last().Should().Contain("kind=Invoice").And.Contain("offset=0");

        cut.Find("[data-select-document]").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("#1"));
        cut.WaitForAssertion(() => cut.FindAll("section#billing-payments").Should().ContainSingle());
        cut.WaitForAssertion(() => cut.FindAll("[data-select-payment]").Should().ContainSingle());
        Paths<PaymentRecord>().Should().Equal($"api/v1/erp/billing/{Tenant:D}/{scope.OrganizationId:D}/documents/{invoice.Id:D}/payments?offset=0&limit=50");
        cut.Find("[data-select-payment]").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-payment").TextContent.Should().Contain("4"));
    }

    [Fact]
    public void Contact_row_selection_feeds_the_document_editor()
    {
        var scope = Scope(1, "billing.read", "billing.write");
        ShowScopes(scope);
        var contact = new BillingContact(Guid.NewGuid(), Guid.NewGuid(), "CUST-1", "고객", true);
        Reads<BillingContact>(_ => new([contact], 1));
        var cut = Render<HostBillingWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-contact]").Should().NotBeNull());
        cut.Find("[data-select-contact]").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-contact").TextContent.Should().Contain("CUST-1"));
        cut.Find("#billing-start-estimate").HasAttribute("disabled").Should().BeFalse();
    }

    private void Reads<T>(Func<string, BusinessPage<T>> response)
        => _api.Setup(api => api.ReadInventoryAsync<BusinessPage<T>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult<(BusinessPage<T>?, int, string?, string?)>((response(path), 200, null, null)));
    private string[] Paths<T>() => _api.Invocations.Where(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync)
        && call.Method.GetGenericArguments()[0] == typeof(BusinessPage<T>)).Select(call => call.Arguments[0]).OfType<string>().ToArray();
    private void ShowScopes(params BusinessMembership[] scopes) => Reads<BusinessMembership>(_ => new(scopes, scopes.Length));
    private static BusinessMembership Scope(int index, params string[] grants)
        => new(Tenant, Guid.Parse($"20000000-0000-0000-0000-{index:000000000000}"), "operator", Guid.Parse("30000000-0000-0000-0000-000000000001"), true, 1, grants);
    private static BusinessScope Business(BusinessMembership scope) => new("NexaOne.MES", Tenant.ToString("D"), scope.OrganizationId.ToString("D"));
    private static BillingDocument Document(BusinessMembership scope, BillingKind kind, long number, BillingStatus status)
        => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(), kind, number,
            new(Guid.NewGuid(), new(2026, 9, 22), new(2026, 10, 22), "KRW", [new("A", 10m, 1m)]), new(10m, 0m, 0m, 10m), status, Guid.NewGuid().ToString("D"));
}
