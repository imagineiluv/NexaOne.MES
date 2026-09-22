using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Server.Components.Pages;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Radzen;
using Xunit;
using WorkerDto = NexaOne.ServiceContracts.Mdm.WorkerDto;

namespace NexaOne.ServerTests;

public sealed class InventoryWorkspaceTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly UiTextService _ui = new();
    private readonly Action<string> _signIn;
    private readonly Action _signOut;
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public InventoryWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        var authorization = this.AddAuthorization();
        authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, "operator"));
        authorization.SetAuthorized("operator");
        // bUnit SetClaims already publishes an authenticated principal, including after sign-out.
        _signIn = name => authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, name));
        _signOut = () => authorization.SetNotAuthorized();
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_ui);
        Services.AddSingleton(new ProtectedSessionStorage(new InventorySessionStorageJs(), new EphemeralDataProtectionProvider()));
        Reads<InventoryAccessScope>(_ => new([], 0));
        Reads<Product>(_ => new([], 0));
        Reads<Warehouse>(_ => new([], 0));
        Reads<SharedEquipment>(_ => new([], 0));
        Reads<EquipmentBooking>(_ => new([], 0));
        Reads<WorkerDto>(_ => new([], 0));
        Reads<StockReservation>(_ => new([], 0));
        Reads<StockBalance>(_ => new([], 0));
    }

    [Fact]
    public void No_scopes_is_a_successful_empty_state_with_disabled_page_buttons()
    {
        var cut = Render<HostInventoryWorkspace>();

        cut.WaitForAssertion(() => cut.Find("#inventory-scopes [data-empty]").TextContent.Should().Contain("접근 가능한"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
        cut.Find("#inventory-scopes-previous").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#inventory-scopes-next").HasAttribute("disabled").Should().BeTrue();
        Paths<InventoryAccessScope>().Should().Equal("api/v1/ivt/scopes/me?offset=0&limit=50");
    }

    [Fact]
    public void Scopes_beyond_first_page_remain_selectable_and_same_tenant_organizations_are_distinct()
    {
        var scopes = Enumerable.Range(1, 51).Select(i => Scope(i, "stock.post")).ToArray();
        Reads<InventoryAccessScope>(path => new(path.Contains("offset=50") ? scopes.Skip(50).ToArray() : scopes.Take(50).ToArray(), 51));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.FindAll("[data-scope]").Should().HaveCount(50));
        cut.FindAll("[data-scope]").Select(e => e.GetAttribute("data-scope")).Distinct().Should().HaveCount(50);
        cut.Find("[data-scope]").TextContent.Should().Contain("Plant 1").And.Contain("P1").And.Contain(Tenant.ToString("D"));

        cut.Find("#inventory-scopes-next").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-scope]").Should().ContainSingle());
        cut.Find("#inventory-scopes-next").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-scope]").Click();
        cut.Find("#selected-inventory-scope").TextContent.Should().Contain(scopes[50].Membership.OrganizationId.ToString("D"));
        cut.Find("#inventory-no-read-access").TextContent.Should().Contain("권한이 없습니다");
        cut.Find("#inventory-scopes-previous").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-scope]").Should().HaveCount(50));
        cut.Find("#selected-inventory-scope").TextContent.Should().Contain(scopes[50].Membership.OrganizationId.ToString("D"));
    }

    [Theory]
    [InlineData("stock.read", "products")]
    [InlineData("stock.warehouse.read", "warehouses")]
    [InlineData("equipment.read", "assets")]
    [InlineData("equipment.booking.read", "bookings")]
    [InlineData("equipment.booking.request", "workers")]
    [InlineData("stock.post", null)]
    public void Independent_grants_expose_only_the_corresponding_list(string grant, string? section)
    {
        ShowScopes(Scope(1, grant));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();

        foreach (var name in new[] { "products", "warehouses", "assets", "bookings", "workers" })
            cut.FindAll("section#inventory-" + name).Count.Should().Be(name == section ? 1 : 0);
        cut.FindAll("#inventory-no-read-access").Count.Should().Be(section is null ? 1 : 0);
        _api.Invocations.Should().OnlyContain(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync));
        _api.Invocations.Count.Should().Be(section is null ? 1 : 2);
    }

    [Fact]
    public void Product_search_keeps_literal_text_and_long_total_and_paging_uses_applied_filter()
    {
        var scope = Scope(1, "stock.read");
        ShowScopes(scope);
        var product = new Product(Guid.NewGuid(), Business(scope), Guid.NewGuid(), new("MASTER-1", "Filtered master"));
        var total = (long)int.MaxValue + 75;
        Reads<Product>(path => new(path.Contains("offset=50") ? [] : [product], path.Contains("offset=50") ? 50 : total));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#inventory-products [data-total]").GetAttribute("data-total").Should().Be(total.ToString()));
        cut.Find("#inventory-products-text").GetAttribute("maxlength").Should().Be("255");
        const string literal = " %_[한글] + ";
        cut.Find("#inventory-products-text").Input(literal);
        cut.Find("#inventory-products form").Submit();
        Paths<Product>().Last().Should().Contain("text=" + Uri.EscapeDataString(literal)).And.Contain("offset=0");
        cut.Find("#inventory-products-text").Input("not applied");

        cut.Find("#inventory-products-next").Click();

        cut.WaitForAssertion(() => cut.Find("#inventory-products [data-empty]").Should().NotBeNull());
        Paths<Product>().Last().Should().Contain("offset=50").And.Contain("text=" + Uri.EscapeDataString(literal));
        cut.Find("#inventory-products [data-total]").GetAttribute("data-total").Should().Be("50");
        cut.Find("#inventory-products-next").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#inventory-products-previous").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#inventory-products form").Submit();
        Paths<Product>().Last().Should().Contain("offset=0").And.Contain("text=not%20applied");
    }

    [Fact]
    public void Warehouse_and_asset_lists_keep_independent_filters_and_render_domain_fields()
    {
        var scope = Scope(1, "stock.warehouse.read", "equipment.read");
        ShowScopes(scope);
        Reads<Warehouse>(_ => new([new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "WH-1", "Warehouse name", false)], 1));
        Reads<SharedEquipment>(_ => new([new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "EQ-1", "Shared asset", 3, true)], 1));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#inventory-warehouses tbody").TextContent.Should().Contain("WH-1").And.Contain("Warehouse name").And.Contain("비활성");
        cut.Find("#inventory-assets tbody").TextContent.Should().Contain("EQ-1").And.Contain("Shared asset").And.Contain("3");
        cut.Find("#inventory-warehouses-text").Input("warehouse literal ");
        cut.Find("#inventory-warehouses input[type=checkbox]").Change(true);
        cut.Find("#inventory-warehouses form").Submit();

        Paths<Warehouse>().Last().Should().Contain("text=warehouse%20literal%20").And.Contain("includeInactive=true");
        Paths<SharedEquipment>().Should().ContainSingle();
        cut.Find("#inventory-assets-text").Input("asset %_ ");
        cut.Find("#inventory-assets form").Submit();
        Paths<SharedEquipment>().Last().Should().Contain("text=asset%20%25_%20").And.Contain("includeInactive=false");
        Paths<Warehouse>().Should().HaveCount(2);
        cut.Find("#inventory-assets-text").GetAttribute("maxlength").Should().Be("255");
        cut.Find("#inventory-warehouses-text").GetAttribute("maxlength").Should().Be("255");
    }

    [Fact]
    public void Worker_selection_is_local_and_cleared_on_search_and_page_changes()
    {
        ShowScopes(Scope(1, "equipment.booking.request"));
        Reads<WorkerDto>(path => new([new(path.Contains("offset=50") ? "W51" : "W1", "Worker", "P1", true)], 51));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#inventory-workers-text").GetAttribute("maxlength").Should().Be("256");
        var calls = _api.Invocations.Count;
        cut.Find("[data-worker=W1]").Click();
        cut.Find("#inventory-worker-selection").TextContent.Should().Contain("W1");
        _api.Invocations.Count.Should().Be(calls, "selecting a worker must not perform a write or identity lookup");
        cut.Find("[data-worker=W1]").GetAttribute("aria-pressed").Should().Be("true");

        cut.Find("#inventory-workers-next").Click();

        cut.WaitForAssertion(() => cut.Find("[data-worker=W51]").Should().NotBeNull());
        cut.FindAll("#inventory-worker-selection").Should().BeEmpty();
        cut.Find("#inventory-workers-next").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-worker=W51]").Click();
        var literal = new string(' ', 256);
        cut.Find("#inventory-workers-text").Input(literal);
        cut.Find("#inventory-workers form").Submit();
        Paths<WorkerDto>().Last().Should().Contain("text=" + Uri.EscapeDataString(literal)).And.Contain("offset=0");
        cut.FindAll("#inventory-worker-selection").Should().BeEmpty();
        _api.Invocations.Should().OnlyContain(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync));
    }

    [Theory]
    [InlineData(200, null)]
    [InlineData(403, "권한")]
    [InlineData(503, "서비스")]
    public void Empty_forbidden_and_unavailable_remain_distinct(int status, string? message)
    {
        ShowScopes(Scope(1, "stock.read"));
        _api.Setup(api => api.ReadInventoryAsync<BusinessPage<Product>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((BusinessPage<Product>?)(status == 200 ? new([], 0) : null), status, (string?)null, (string?)null));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();

        cut.FindAll("#inventory-products [data-empty]").Count.Should().Be(status == 200 ? 1 : 0);
        if (message is not null)
        {
            cut.Find("#inventory-products [role=alert]").GetAttribute("data-status").Should().Be(status.ToString());
            cut.Find("#inventory-products [role=alert]").TextContent.Should().Contain(message);
            cut.Find("#inventory-products-retry").Click();
            Paths<Product>().Should().HaveCount(2);
        }
        cut.Find("#inventory-products-next").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Booking_filter_sends_named_state_and_renders_stored_identifiers_without_invented_names()
    {
        var scope = Scope(1, "equipment.booking.read");
        ShowScopes(scope);
        var booking = new EquipmentBooking(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-20T12:00:00+09:00"), DateTimeOffset.Parse("2026-09-20T13:00:00+09:00"), 2,
            "business-requester-id", EquipmentBookingState.Returned);
        Reads<EquipmentBooking>(_ => new([booking], 1));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.Find("#booking-state").Change("Returned");
        cut.Find("#inventory-bookings form").Submit();

        Paths<EquipmentBooking>().Last().Should().Contain("state=Returned").And.Contain("offset=0");
        cut.Find("#inventory-bookings tbody").TextContent.Should().Contain(booking.EmployeeId.ToString())
            .And.Contain(booking.EquipmentId.ToString()).And.Contain("business-requester-id").And.Contain("2026-09-20 03:00");
        cut.FindAll("#inventory-workers").Should().BeEmpty();
        cut.Find("#booking-state").Change("");
        cut.Find("#inventory-bookings form").Submit();
        Paths<EquipmentBooking>().Last().Should().NotContain("state=");
    }

    [Fact]
    public async Task A_late_old_scope_response_cannot_replace_new_scope_rows_or_end_its_busy_state()
    {
        var first = Scope(1, "stock.read"); var second = Scope(2, "stock.read");
        ShowScopes(first, second);
        var oldReply = new TaskCompletionSource<(BusinessPage<Product>?, int, string?, string?)>();
        var newReply = new TaskCompletionSource<(BusinessPage<Product>?, int, string?, string?)>();
        CancellationToken oldToken = default;
        _api.Setup(api => api.ReadInventoryAsync<BusinessPage<Product>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken token) =>
            {
                if (path.Contains(first.Membership.OrganizationId.ToString("D"))) { oldToken = token; return oldReply.Task; }
                return newReply.Task;
            });
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.FindAll("[data-scope]").Should().HaveCount(2));
        var oldSelection = cut.FindAll("[data-scope]")[0].ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#inventory-products").GetAttribute("aria-busy").Should().Be("true"));
        var newSelection = cut.FindAll("[data-scope]")[1].ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => oldToken.IsCancellationRequested.Should().BeTrue());
        oldReply.SetResult(Ok(new BusinessPage<Product>([Product(first, "old scope")], 1)));
        await oldSelection;

        cut.Find("#inventory-products").GetAttribute("aria-busy").Should().Be("true");
        cut.Find("#inventory-products-next").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().NotContain("old scope");
        newReply.SetResult(Ok(new BusinessPage<Product>([Product(second, "new scope")], 1)));
        await newSelection;
        cut.WaitForAssertion(() => cut.Find("#inventory-products tbody").TextContent.Should().Contain("new scope").And.NotContain("old scope"));
        cut.Find("#inventory-products").GetAttribute("aria-busy").Should().Be("false");
    }

    [Fact]
    public async Task Authentication_change_clears_selection_and_cancels_old_identity_reads()
    {
        var scope = Scope(1, "stock.read", "equipment.booking.request");
        ShowScopes(scope);
        Reads<WorkerDto>(_ => new([new("W1", "Worker", "P1", true)], 1));
        var pending = new TaskCompletionSource<(BusinessPage<Product>?, int, string?, string?)>();
        CancellationToken token = default;
        _api.Setup(api => api.ReadInventoryAsync<BusinessPage<Product>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => { token = ct; return pending.Task; });
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        var selection = cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("[data-worker=W1]").Should().NotBeNull());
        cut.Find("[data-worker=W1]").Click();
        await cut.InvokeAsync(() => _signIn("different-user"));
        cut.WaitForAssertion(() => token.IsCancellationRequested.Should().BeTrue());
        cut.FindAll("#selected-inventory-scope, #inventory-worker-selection, #inventory-products").Should().BeEmpty();
        pending.SetResult(Ok(new BusinessPage<Product>([Product(scope, "old identity")], 1)));
        await selection;
        cut.Markup.Should().NotContain("old identity");
        Paths<InventoryAccessScope>().Should().HaveCount(2);
        await cut.InvokeAsync(_signOut);
        cut.WaitForAssertion(() => cut.FindAll("[data-scope]").Should().BeEmpty());
    }

    [Fact]
    public async Task Disposing_cancels_pending_reads_without_turning_cancellation_into_a_visible_failure()
    {
        ShowScopes(Scope(1, "stock.read"));
        var reply = new TaskCompletionSource<(BusinessPage<Product>?, int, string?, string?)>();
        CancellationToken token = default;
        _api.Setup(api => api.ReadInventoryAsync<BusinessPage<Product>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => { token = ct; return reply.Task; });
        var cut = Render<HostInventoryWorkspace>();
        try
        {
            cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
            var selection = cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
            cut.WaitForAssertion(() => token.CanBeCanceled.Should().BeTrue());

            // Renderer teardown invokes the page's disposal lifecycle; cut.Dispose() only clears bUnit's wrapper.
            await DisposeComponentsAsync();

            token.IsCancellationRequested.Should().BeTrue();
            reply.SetCanceled(token);
            await selection;
        }
        finally
        {
            // Settle a pending read on failure without masking the original assertion with cancellation.
            reply.TrySetResult(Ok(new BusinessPage<Product>([], 0)));
        }
    }

    [Fact]
    public async Task Initially_anonymous_page_loads_scopes_when_interactive_authentication_is_restored()
    {
        _signOut();
        ShowScopes(Scope(1, "stock.read"));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("다시 로그인"));
        _api.Invocations.Should().BeEmpty();

        await cut.InvokeAsync(() => _signIn("operator"));

        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        Paths<InventoryAccessScope>().Should().ContainSingle();
    }

    [Fact]
    public async Task Language_changes_use_English_fallbacks_and_respect_registered_translations()
    {
        var cut = Render<HostInventoryWorkspace>();
        cut.Find("h1").TextContent.Should().Be("재고 작업 공간");
        await cut.InvokeAsync(() => _ui.Load("EnUs", new()));
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Inventory workspace"));
        cut.Find("#inventory-scopes [data-empty]").TextContent.Should().Contain("No inventory scopes");
        await cut.InvokeAsync(() => _ui.Load("EnUs", new() { ["inventory.title"] = "Custom title" }));
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Custom title"));
    }

    [Fact]
    public async Task Equipment_and_booking_rows_feed_the_panel_and_confirmed_save_refreshes_current_lists()
    {
        const string actualUser = "equipment-operator";
        _signIn(actualUser);
        var scope = Scope(1, "equipment.read", "equipment.write", "equipment.booking.read", "equipment.booking.request");
        scope = scope with { Membership = scope.Membership with { UserId = actualUser } };
        var authentication = await Services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        authentication.User.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(scope.Membership.UserId);
        authentication.User.Identity!.Name.Should().NotBe(actualUser, "the parent must use the identity claim, not the display name");
        var asset = new SharedEquipment(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "EQ-1", "Original asset", 2, true);
        var updated = asset with { Name = "Saved asset", Version = Guid.NewGuid() };
        var current = asset;
        var start = new DateTimeOffset(2030, 1, 2, 3, 0, 0, TimeSpan.Zero);
        var booking = new EquipmentBooking(Guid.NewGuid(), Business(scope), Guid.NewGuid(), asset.Id, Guid.NewGuid(),
            start, start.AddHours(1), 1, Guid.NewGuid().ToString("D"), EquipmentBookingState.Requested);
        ShowScopes(scope);
        Reads<SharedEquipment>(_ => new([current], 1));
        Reads<EquipmentBooking>(_ => new([booking], 1));
        Reads<WorkerDto>(_ => new([new("W1", "Selected worker", "P1", true)], 1));
        var path = $"api/v1/ivt/shared-equipment/{Tenant:D}/{scope.Membership.OrganizationId:D}/assets/{asset.Id:D}";
        _api.Setup(api => api.WriteInventoryAsync<SharedEquipment>(HttpMethod.Put, path, It.IsAny<object>(), actualUser, It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                body.Should().Be(new EquipmentSharingController.EquipmentChange("EQ-1", "Saved asset", 2, true, asset.Version));
                current = updated;
                return Task.FromResult<(SharedEquipment?, int, string?, string?)>((updated, 200, null, null));
            });
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        await cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<EquipmentWorkflowPanel>().Instance.UserId.Should().Be(actualUser);
            cut.Find("#equipment-new").HasAttribute("disabled").Should().BeFalse();
        });
        cut.Find("[data-worker=W1]").Click();

        await cut.Find($"[data-manage-equipment='{asset.Id:D}']").ClickAsync(new MouseEventArgs());

        cut.Find("#equipment-selected-worker").TextContent.Should().Contain("W1");
        cut.Find("#equipment-name").Change("Saved asset");
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => cut.Find("#inventory-assets tbody").TextContent.Should().Contain("Saved asset"));
        Paths<SharedEquipment>().Should().HaveCount(2);
        Paths<EquipmentBooking>().Should().HaveCount(2);
        Paths<WorkerDto>().Should().ContainSingle();
        await cut.Find($"[data-manage-booking='{booking.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.Find("#equipment-booking-detail").TextContent.Should().Contain(booking.Id.ToString("D"));
        _api.Invocations.Count(call => call.Method.Name == nameof(IApiClient.WriteInventoryAsync)).Should().Be(1);
    }

    [Fact]
    public async Task Reselecting_current_scope_keeps_equipment_and_booking_row_actions_connected()
    {
        var scope = Scope(1, "equipment.read", "equipment.booking.read");
        var first = new SharedEquipment(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "EQ-1", "First asset", 1, true);
        var second = first with { Id = Guid.NewGuid(), Code = "EQ-2", Name = "Second asset" };
        var start = new DateTimeOffset(2030, 1, 2, 3, 0, 0, TimeSpan.Zero);
        var booking = new EquipmentBooking(Guid.NewGuid(), Business(scope), Guid.NewGuid(), second.Id, Guid.NewGuid(),
            start, start.AddHours(1), 1, Guid.NewGuid().ToString("D"), EquipmentBookingState.Requested);
        ShowScopes(scope);
        Reads<SharedEquipment>(_ => new([first, second], 2));
        Reads<EquipmentBooking>(_ => new([booking], 1));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        await cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        await cut.Find($"[data-manage-equipment='{first.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.Find("#equipment-selected-asset").TextContent.Should().Contain("First asset");

        await cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        await cut.Find($"[data-manage-equipment='{second.Id:D}']").ClickAsync(new MouseEventArgs());

        cut.Find("#equipment-selected-asset").TextContent.Should().Contain("Second asset").And.NotContain("First asset");
        await cut.Find($"[data-manage-booking='{booking.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.Find("#equipment-booking-detail").TextContent.Should().Contain(booking.Id.ToString("D"));
        _api.Invocations.Should().OnlyContain(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync));
    }

    [Fact]
    public async Task Product_and_warehouse_rows_feed_the_stock_panel_and_a_selected_variant_lists_its_movements()
    {
        var scope = Scope(1, "stock.read", "stock.warehouse.read", "stock.post", "stock.reverse");
        var product = Product(scope, "P-100");
        var warehouse = new Warehouse(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "WH-1", "Main warehouse");
        var variant = new ProductVariant(Guid.NewGuid(), Business(scope), product.Version, product.Id, "P-100", "EA", Array.Empty<OptionSelection>());
        var movement = new StockMovement(Guid.NewGuid(), Business(scope), Guid.NewGuid(),
            new StockPosting(Guid.NewGuid(), variant.Id, StockMovementKind.Receipt, 3m, null, warehouse.Id, "GRN-3"), "creator", [new StockDelta(warehouse.Id, 3m)]);
        var movements = new List<StockMovement>();
        ShowScopes(scope);
        Reads<Product>(_ => new([product], 1));
        Reads<Warehouse>(_ => new([warehouse], 1));
        Reads<StockMovement>(_ => new(movements.ToArray(), movements.Count));
        var root = $"api/v1/ivt/stock/{Tenant:D}/{scope.Membership.OrganizationId:D}";
        _api.Setup(api => api.ReadInventoryAsync<ProductVariant>(root + "/products/P-100", It.IsAny<CancellationToken>()))
            .ReturnsAsync((variant, 200, null, null));
        _api.Setup(api => api.WriteInventoryAsync<StockMovement>(HttpMethod.Post, root + "/movements", It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                var posting = (StockPosting)body;
                posting.Should().Be(new StockPosting(posting.OperationId, variant.Id, StockMovementKind.Receipt, 3m, null, warehouse.Id, "GRN-3"));
                movements.Add(movement with { Posting = posting });
                return Task.FromResult<(StockMovement?, int, string?, string?)>((movement with { Posting = posting }, 200, null, null));
            });
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        await cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#inventory-stock-workflows").Should().NotBeNull());
        cut.FindAll("#inventory-movements").Should().BeEmpty("movements need a selected variant");
        cut.FindAll("#inventory-equipment-workflows").Should().BeEmpty("no equipment grant is present");

        await cut.Find($"[data-manage-product='{product.Id:D}']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("#stock-selected-variant").TextContent.Should().Contain("P-100"));
        cut.WaitForAssertion(() => cut.Find("#inventory-movements").Should().NotBeNull());
        Paths<StockMovement>().Should().ContainSingle().Which.Should().Be(root + $"/movements?variantId={variant.Id:D}&offset=0&limit=50");
        await cut.Find($"[data-manage-warehouse='{warehouse.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-selected-warehouse").TextContent.Should().Contain("Main warehouse");
        cut.FindAll("#stock-warehouse-form").Should().BeEmpty("no warehouse write grant is present");

        cut.Find("#stock-start-posting").Click();
        cut.Find("#stock-posting-kind").Change("Receipt");
        cut.Find("#stock-posting-to").Change(warehouse.Id.ToString("D"));
        cut.Find("#stock-posting-quantity").Change("3");
        cut.Find("#stock-posting-reference").Change("GRN-3");
        await cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);

        cut.WaitForAssertion(() => cut.Find("#inventory-movements tbody").TextContent.Should().Contain("GRN-3"));
        Paths<StockMovement>().Should().HaveCount(2);
        Paths<Warehouse>().Should().HaveCount(2, "a confirmed save refreshes the warehouse list");
        await cut.Find($"[data-manage-movement='{movement.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-movement-detail").TextContent.Should().Contain("GRN-3");
        cut.Find("#stock-reverse-movement").Should().NotBeNull();
        _api.Invocations.Count(call => call.Method.Name == nameof(IApiClient.WriteInventoryAsync)).Should().Be(1);
    }

    [Fact]
    public async Task Selected_variant_lists_reservations_with_a_state_filter_and_rows_feed_the_stock_panel()
    {
        var scope = Scope(1, "stock.read", "stock.warehouse.read", "stock.release");
        var product = Product(scope, "P-200");
        var warehouse = new Warehouse(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "WH-1", "Main warehouse");
        var variant = new ProductVariant(Guid.NewGuid(), Business(scope), product.Version, product.Id, "P-200", "EA", Array.Empty<OptionSelection>());
        var active = new StockReservation(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(), variant.Id, warehouse.Id, 2m, "WO-9", Guid.NewGuid().ToString("D"), StockReservationState.Active);
        var released = active with { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Reference = "WO-8", State = StockReservationState.Released };
        ShowScopes(scope);
        Reads<Product>(_ => new([product], 1));
        Reads<Warehouse>(_ => new([warehouse], 1));
        Reads<StockMovement>(_ => new([], 0));
        Reads<StockReservation>(path => path.Contains("state=Active") ? new([active], 1) : new([active, released], 2));
        var root = $"api/v1/ivt/stock/{Tenant:D}/{scope.Membership.OrganizationId:D}";
        _api.Setup(api => api.ReadInventoryAsync<ProductVariant>(root + "/products/P-200", It.IsAny<CancellationToken>()))
            .ReturnsAsync((variant, 200, null, null));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        await cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#inventory-stock-workflows").Should().NotBeNull());
        cut.FindAll("#inventory-reservations").Should().BeEmpty("reservations need a selected variant");

        await cut.Find($"[data-manage-product='{product.Id:D}']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("#inventory-reservations tbody").TextContent.Should().Contain("WO-9").And.Contain("WO-8"));
        Paths<StockReservation>().Should().ContainSingle().Which.Should().Be(root + $"/reservations?variantId={variant.Id:D}&offset=0&limit=50");
        cut.Find("#reservation-state").Change("Active");
        await cut.Find("#inventory-reservations form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => cut.Find("#inventory-reservations tbody").TextContent.Should().Contain("WO-9").And.NotContain("WO-8"));
        Paths<StockReservation>().Last().Should().Be(root + $"/reservations?variantId={variant.Id:D}&offset=0&limit=50&state=Active");

        await cut.Find($"[data-manage-reservation='{active.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-reservation-detail").TextContent.Should().Contain("WO-9");
        cut.Find("#stock-release-reservation").Should().NotBeNull();
        _api.Invocations.Should().OnlyContain(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync));
    }

    [Fact]
    public async Task Selected_warehouse_lists_its_recorded_balances_including_zero_and_a_confirmed_save_refreshes_them()
    {
        var scope = Scope(1, "stock.read", "stock.warehouse.read", "stock.warehouse.write");
        var warehouse = new Warehouse(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "WH-1", "Main warehouse");
        var other = warehouse with { Id = Guid.NewGuid(), Code = "WH-2", Name = "Other warehouse" };
        var stocked = new StockBalance(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(), warehouse.Id, 12.5m, 2.5m);
        var empty = new StockBalance(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(), warehouse.Id, 0m, 0m);
        ShowScopes(scope);
        Reads<Warehouse>(_ => new([warehouse, other], 2));
        Reads<StockBalance>(path => path.Contains(warehouse.Id.ToString("D")) ? new([stocked, empty], 2) : new([], 0));
        var root = $"api/v1/ivt/stock/{Tenant:D}/{scope.Membership.OrganizationId:D}";
        _api.Setup(api => api.WriteInventoryAsync<Warehouse>(HttpMethod.Put, root + $"/warehouses/{warehouse.Id:D}", It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync((warehouse with { Name = "Renamed", Version = Guid.NewGuid() }, 200, null, null));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        await cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#inventory-stock-workflows").Should().NotBeNull());
        cut.FindAll("#inventory-balances").Should().BeEmpty("balances need a selected warehouse");

        await cut.Find($"[data-manage-warehouse='{warehouse.Id:D}']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("#inventory-balances tbody").TextContent.Should().Contain("12.5").And.Contain("2.5").And.Contain("10"));
        cut.Find("#inventory-balances tbody").TextContent.Should().Contain(empty.VariantId.ToString("D"), "zero balances stay listed");
        Paths<StockBalance>().Should().ContainSingle().Which.Should().Be(root + $"/warehouses/{warehouse.Id:D}/balances?offset=0&limit=50");

        cut.Find("#stock-warehouse-name").Change("Renamed");
        await cut.Find("#stock-warehouse-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => Paths<StockBalance>().Should().HaveCount(2, "a confirmed save refreshes the balance list"));

        await cut.Find($"[data-manage-warehouse='{other.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#inventory-balances [data-empty]").Should().NotBeNull());
        Paths<StockBalance>().Last().Should().Be(root + $"/warehouses/{other.Id:D}/balances?offset=0&limit=50");
    }

    [Fact]
    public async Task Balance_and_reservation_rows_select_their_variant_into_the_stock_panel()
    {
        var scope = Scope(1, "stock.read", "stock.warehouse.read");
        var warehouse = new Warehouse(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "WH-1", "Main warehouse");
        var product = Product(scope, "P-300");
        var variant = new ProductVariant(Guid.NewGuid(), Business(scope), product.Version, product.Id, "P-300", "EA", Array.Empty<OptionSelection>());
        var balance = new StockBalance(Guid.NewGuid(), Business(scope), Guid.NewGuid(), variant.Id, warehouse.Id, 5m, 0m);
        var reservation = new StockReservation(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(), variant.Id, warehouse.Id, 1m, "WO-1", Guid.NewGuid().ToString("D"), StockReservationState.Active);
        ShowScopes(scope);
        Reads<Warehouse>(_ => new([warehouse], 1));
        Reads<StockBalance>(_ => new([balance], 1));
        Reads<StockMovement>(_ => new([], 0));
        Reads<StockReservation>(_ => new([reservation], 1));
        var root = $"api/v1/ivt/stock/{Tenant:D}/{scope.Membership.OrganizationId:D}";
        _api.Setup(api => api.ReadInventoryAsync<ProductVariant>(root + $"/variants/{variant.Id:D}", It.IsAny<CancellationToken>()))
            .ReturnsAsync((variant, 200, null, null));
        var cut = Render<HostInventoryWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        await cut.Find("[data-scope]").ClickAsync(new MouseEventArgs());
        await cut.Find($"[data-manage-warehouse='{warehouse.Id:D}']").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#inventory-balances tbody").TextContent.Should().Contain("5"));

        await cut.Find($"#inventory-balances [data-select-variant='{variant.Id:D}']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("#stock-selected-variant").TextContent.Should().Contain("P-300"));
        cut.WaitForAssertion(() => cut.Find("#inventory-reservations tbody").TextContent.Should().Contain("WO-1"));
        Paths<StockMovement>().Should().ContainSingle();
        cut.Find($"#inventory-reservations [data-select-variant='{variant.Id:D}']").Should().NotBeNull();
        _api.Invocations.Should().OnlyContain(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync));
    }

    private void Reads<T>(Func<string, BusinessPage<T>> response)
        => _api.Setup(api => api.ReadInventoryAsync<BusinessPage<T>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult(Ok(response(path))));
    private static (BusinessPage<T>?, int, string?, string?) Ok<T>(BusinessPage<T> page) => (page, 200, null, null);
    private string[] Paths<T>() => _api.Invocations.Where(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync)
        && call.Method.GetGenericArguments()[0] == typeof(BusinessPage<T>)).Select(call => (string)call.Arguments[0]).ToArray();
    private void ShowScopes(params InventoryAccessScope[] scopes) => Reads<InventoryAccessScope>(_ => new(scopes, scopes.Length));
    private static InventoryAccessScope Scope(int index, params string[] grants)
    {
        var organization = Guid.Parse($"20000000-0000-0000-0000-{index:000000000000}");
        return new(new BusinessMembership(Tenant, organization, "operator", Guid.NewGuid(), true, 1, grants),
            new(Tenant, organization, "P" + index, Guid.NewGuid(), true), new("P" + index, "Plant " + index, "", "KR", "Asia/Seoul"));
    }
    private static BusinessScope Business(InventoryAccessScope scope) => new("NexaOne.MES", Tenant.ToString("D"), scope.Membership.OrganizationId.ToString("D"));
    private static Product Product(InventoryAccessScope scope, string name) => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), new(name, name));
}
