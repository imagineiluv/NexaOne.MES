using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Server.Components.Pages;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;
using WorkerDto = NexaOne.ServiceContracts.Mdm.WorkerDto;

namespace NexaOne.ServerTests;

public sealed class EquipmentWorkflowPanelTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly InventorySessionStorageJs _browser = new();
    private readonly ProtectedSessionStorage _storage;
    private readonly List<(HttpMethod Method, string Path, object Body, string Owner, CancellationToken Token)> _writes = [];
    private int _saved;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid BusinessUser = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUser = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Start = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public EquipmentWorkflowPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _storage = new ProtectedSessionStorage(_browser, new EphemeralDataProtectionProvider());
        var ui = new UiTextService();
        ui.Load("EnUs", new());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(ui);
        Services.AddSingleton(_storage);
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Failure<SharedEquipment>(503)));
        Writes<EquipmentBooking>((_, _, _, _, _) => Task.FromResult(Failure<EquipmentBooking>(503)));
    }

    [Fact]
    public async Task Asset_writer_can_create_update_and_toggle_using_each_returned_version()
    {
        var scope = Scope("equipment.write", "equipment.read");
        var created = Asset(scope);
        var updated = created with { Name = "Updated press", Capacity = 3, Version = Guid.NewGuid() };
        var inactive = updated with { Active = false, Version = Guid.NewGuid() };
        var active = inactive with { Active = true, Version = Guid.NewGuid() };
        var responses = new Queue<SharedEquipment>([created, updated, inactive, active]);
        Writes<SharedEquipment>(async (_, _, body, owner, _) =>
        {
            var intent = await StoredIntent();
            intent.UserId.Should().Be("operator");
            intent.BusinessUserId.Should().Be(BusinessUser);
            intent.TenantId.Should().Be(Tenant);
            intent.OrganizationId.Should().Be(scope.Membership.OrganizationId);
            intent.Payload.GetRawText().Should().Be(JsonSerializer.Serialize(body, Json));
            intent.Confirmed.Should().BeFalse();
            owner.Should().Be("operator");
            return Ok(responses.Dequeue());
        });
        var cut = Panel(scope);
        FillNewAsset(cut);

        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);

        var create = _writes[0].Body.Should().BeOfType<EquipmentSharingController.EquipmentCreate>().Which;
        create.OperationId.Should().NotBeEmpty();
        create.Should().Be(new EquipmentSharingController.EquipmentCreate(create.OperationId, "PRESS-1", "Press one", 2, true));
        _writes[0].Method.Should().Be(HttpMethod.Post);
        _writes[0].Path.Should().Be(Root(scope) + "/assets");
        cut.Find("#equipment-selected-asset").TextContent.Should().Contain("Press one");
        cut.Find("#equipment-name").Change("Updated press");
        cut.Find("#equipment-capacity").Change("3");
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        _writes[1].Body.Should().Be(new EquipmentSharingController.EquipmentChange("PRESS-1", "Updated press", 3, true, created.Version));
        _writes[1].Path.Should().Be(Root(scope) + $"/assets/{created.Id:D}");

        await cut.Find("#equipment-toggle-active").ClickAsync(new MouseEventArgs());
        _writes[2].Body.Should().Be(new EquipmentSharingController.ActiveChange(updated.Version, false));
        cut.Find("#equipment-toggle-active").TextContent.Should().Contain("Activate equipment");
        await cut.Find("#equipment-toggle-active").ClickAsync(new MouseEventArgs());
        _writes[3].Body.Should().Be(new EquipmentSharingController.ActiveChange(inactive.Version, true));
        _writes.Skip(1).Should().OnlyContain(write => write.Method == HttpMethod.Put);
        _writes.Skip(2).Should().OnlyContain(write => write.Path == Root(scope) + $"/assets/{created.Id:D}/active");
        _saved.Should().Be(4);
        _browser.Values.Should().BeEmpty();
        cut.FindAll("#equipment-recovery, #equipment-write-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Booking_uses_exact_worker_and_UTC_values_and_replays_original_ID_and_body()
    {
        var scope = Scope("equipment.read", "equipment.booking.request");
        var asset = Asset(scope);
        var worker = new WorkerDto("W-한글-01", "Current worker", "P1", true);
        var cut = Panel(scope, worker);
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(asset));
        cut.Find("#equipment-start-booking").Click();
        cut.Find("#equipment-booking-start").Change("2030-01-02T03:04:05");
        cut.Find("#equipment-booking-end").Change("2030-01-02T04:05:06");
        cut.Find("#equipment-booking-quantity").Change("2");

        await cut.Find("#equipment-booking-form").SubmitAsync(EventArgs.Empty);

        var sent = _writes.Should().ContainSingle().Which;
        sent.Method.Should().Be(HttpMethod.Put);
        var id = Guid.Parse(sent.Path.Split('/').Last());
        sent.Path.Should().Be(Root(scope) + $"/bookings/{id:D}");
        var expected = new EquipmentSharingController.BookingRequest(asset.Id, "W-한글-01", Start,
            new DateTimeOffset(2030, 1, 2, 4, 5, 6, TimeSpan.Zero), 2);
        sent.Body.Should().Be(expected);
        (await StoredIntent()).Id.Should().Be(id);
        cut.Render(parameters => parameters.Add(p => p.Worker, new WorkerDto("W-OTHER", "Other worker", "P1", true)));
        Writes<EquipmentBooking>((_, _, _, _, _) => Task.FromResult(Ok(Booking(scope, EquipmentBookingState.Requested) with
        { Id = id, EquipmentId = asset.Id, RequestedBy = BusinessUser.ToString("D"), Start = expected.Start, End = expected.End, Quantity = 2 })));

        await cut.Find("#equipment-retry-write").ClickAsync(new MouseEventArgs());

        _writes.Should().HaveCount(2);
        _writes[1].Path.Should().Be(sent.Path);
        _writes[1].Body.Should().Be(expected);
        _writes[1].Owner.Should().Be("operator");
        cut.Find("#equipment-booking-detail").TextContent.Should().Contain(id.ToString("D")).And.Contain("Requested");
        _saved.Should().Be(1);
        _browser.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("approve", "equipment.booking.decide", EquipmentBookingState.Requested, EquipmentBookingState.Approved, "decide")]
    [InlineData("approve", "equipment.booking.decide", EquipmentBookingState.Requested, EquipmentBookingState.CheckedOut, "decide")]
    [InlineData("approve", "equipment.booking.decide", EquipmentBookingState.Requested, EquipmentBookingState.Returned, "decide")]
    [InlineData("deny", "equipment.booking.decide", EquipmentBookingState.Requested, EquipmentBookingState.Denied, "decide")]
    [InlineData("cancel", "equipment.booking.cancel", EquipmentBookingState.Approved, EquipmentBookingState.Cancelled, "cancel")]
    [InlineData("checkout", "equipment.booking.checkout", EquipmentBookingState.Approved, EquipmentBookingState.CheckedOut, "check-out")]
    [InlineData("checkout", "equipment.booking.checkout", EquipmentBookingState.Approved, EquipmentBookingState.Returned, "check-out")]
    [InlineData("return", "equipment.booking.return", EquipmentBookingState.CheckedOut, EquipmentBookingState.Returned, "return")]
    public async Task Transitions_use_only_their_own_grant_and_selected_booking_version(string action, string grant,
        EquipmentBookingState initial, EquipmentBookingState final, string route)
    {
        var scope = Scope("equipment.booking.read", grant);
        var booking = Booking(scope, initial);
        var result = booking with { State = final, Version = Guid.NewGuid(), ReturnedAt = final == EquipmentBookingState.Returned ? Start.AddMinutes(30) : null };
        Writes<EquipmentBooking>((_, _, _, _, _) => Task.FromResult(Ok(result)));
        var cut = Panel(scope);
        await cut.InvokeAsync(() => cut.Instance.SelectBookingAsync(booking));
        foreach (var other in new[] { "approve", "deny", "cancel", "checkout", "return" })
            if (other != action && !(grant == "equipment.booking.decide" && other is "approve" or "deny"))
                cut.FindAll("#equipment-" + other).Should().BeEmpty();

        await cut.Find("#equipment-" + action).ClickAsync(new MouseEventArgs());

        var sent = _writes.Should().ContainSingle().Which;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Path.Should().Be(Root(scope) + $"/bookings/{booking.Id:D}/{route}");
        if (action is "approve" or "deny") sent.Body.Should().Be(new EquipmentSharingController.BookingDecision(booking.Version, action == "approve"));
        else sent.Body.Should().Be(new EquipmentSharingController.VersionedCommand(booking.Version));
        _saved.Should().Be(1);
        _browser.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("approve", EquipmentBookingState.Approved)]
    [InlineData("deny", EquipmentBookingState.Denied)]
    [InlineData("cancel", EquipmentBookingState.CheckedOut)]
    [InlineData("checkout", EquipmentBookingState.Requested)]
    [InlineData("return", EquipmentBookingState.Approved)]
    public async Task Ineligible_transition_state_blocks_the_command(string action, EquipmentBookingState state)
    {
        var scope = Scope("equipment.booking.read", "equipment.booking.decide", "equipment.booking.cancel", "equipment.booking.checkout", "equipment.booking.return");
        var cut = Panel(scope);
        await cut.InvokeAsync(() => cut.Instance.SelectBookingAsync(Booking(scope, state)));

        cut.Find("#equipment-" + action).HasAttribute("disabled").Should().BeTrue();
        await cut.Find("#equipment-" + action).ClickAsync(new MouseEventArgs());

        _writes.Should().BeEmpty();
        _browser.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Own_request_cannot_be_approved_or_denied_even_with_decide_grant()
    {
        var scope = Scope("equipment.booking.read", "equipment.booking.decide");
        var cut = Panel(scope);
        await cut.InvokeAsync(() => cut.Instance.SelectBookingAsync(Booking(scope, EquipmentBookingState.Requested) with { RequestedBy = BusinessUser.ToString("D") }));
        cut.Find("#equipment-approve").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#equipment-deny").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#equipment-booking-detail").TextContent.Should().Contain("own request");
        await cut.Find("#equipment-approve").ClickAsync(new MouseEventArgs());
        await cut.Find("#equipment-deny").ClickAsync(new MouseEventArgs());
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true, "P1")]
    [InlineData(true, false, "P1")]
    [InlineData(true, true, "FOREIGN")]
    public async Task Booking_requires_active_selected_equipment_and_worker_from_current_plant(bool assetActive, bool workerActive, string plant)
    {
        var scope = Scope("equipment.read", "equipment.booking.request");
        var cut = Panel(scope, new WorkerDto("W1", "Worker", plant, workerActive));
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(Asset(scope) with { Active = assetActive }));
        cut.Find("#equipment-start-booking").HasAttribute("disabled").Should().BeTrue();
        await cut.Find("#equipment-start-booking").ClickAsync(new MouseEventArgs());
        cut.FindAll("#equipment-booking-form").Should().BeEmpty();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_grants_do_not_infer_write_grants_and_foreign_selections_are_rejected()
    {
        var scope = Scope("equipment.read", "equipment.booking.read");
        var cut = Panel(scope, new WorkerDto("W1", "Worker", "P1", true));
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(Asset(scope)));
        cut.FindAll("#equipment-new, #equipment-save-asset, #equipment-toggle-active, #equipment-start-booking").Should().BeEmpty();
        await cut.InvokeAsync(() => cut.Instance.SelectBookingAsync(Booking(scope, EquipmentBookingState.Requested)));
        cut.FindAll("#equipment-approve, #equipment-deny, #equipment-cancel, #equipment-checkout, #equipment-return").Should().BeEmpty();
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(Asset(ScopeAt(2, "equipment.read"))));
        cut.Find("#equipment-write-error").TextContent.Should().Contain("scope or request");
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(400, "required code/name")]
    [InlineData(403, "permission changed")]
    public async Task First_known_rejection_clears_intent_and_allows_a_new_request(int status, string message)
    {
        var cut = Panel(Scope("equipment.write"));
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Failure<SharedEquipment>(status)));
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        cut.Find("#equipment-write-error").TextContent.Should().Contain(message);
        cut.FindAll("#equipment-recovery").Should().BeEmpty();
        cut.Find("#equipment-new").HasAttribute("disabled").Should().BeFalse();
        _browser.Values.Should().BeEmpty();
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        _writes.Should().HaveCount(2);
        var first = (EquipmentSharingController.EquipmentCreate)_writes[0].Body;
        var second = (EquipmentSharingController.EquipmentCreate)_writes[1].Body;
        second.OperationId.Should().NotBe(first.OperationId);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    [InlineData("throttled")]
    [InlineData("null-success")]
    [InlineData("foreign-scope")]
    [InlineData("empty-version")]
    public async Task Uncertain_or_invalid_success_keeps_recovery_and_blocks_new_commands(string response)
    {
        var scope = Scope("equipment.write");
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(response switch
        {
            "null-success" => (null, 200, "INVALID_INVENTORY_RESPONSE", "Invalid response"),
            "foreign-scope" => Ok(Asset(ScopeAt(2, "equipment.write"))),
            "empty-version" => Ok(Asset(scope) with { Version = Guid.Empty }),
            "timeout" => Failure<SharedEquipment>(408),
            "throttled" => Failure<SharedEquipment>(429),
            _ => Failure<SharedEquipment>(503)
        }));
        var cut = Panel(scope);
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        cut.Find("#equipment-recovery").TextContent.Should().Contain("outcome");
        cut.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
        (await StoredIntent()).Confirmed.Should().BeFalse();
        _saved.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_or_active_response_loss_survives_reload_and_replay409_does_not_resolve_it(bool activeChange)
    {
        var scope = Scope("equipment.read", "equipment.write");
        var asset = Asset(scope);
        var cut = Panel(scope);
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(asset));
        if (activeChange) await cut.Find("#equipment-toggle-active").ClickAsync(new MouseEventArgs());
        else
        {
            cut.Find("#equipment-name").Change("New name");
            await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        }
        var original = _writes.Should().ContainSingle().Which;
        var originalJson = JsonSerializer.Serialize(original.Body, Json);
        var key = _browser.Values.Keys.Should().ContainSingle().Which;
        var persisted = _browser.Values[key];
        persisted.Should().NotContain("New name").And.NotContain("operator");
        await DisposeComponentsAsync();

        var restored = Panel(scope);
        restored.WaitForAssertion(() => restored.Find("#equipment-retry-write").Should().NotBeNull());
        _writes.Should().ContainSingle("recovery must never replay automatically");
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Failure<SharedEquipment>(409, "BUSINESS_VERSION_CONFLICT")));
        await restored.Find("#equipment-retry-write").ClickAsync(new MouseEventArgs());

        _writes.Should().HaveCount(2);
        _writes[1].Method.Should().Be(original.Method);
        _writes[1].Path.Should().Be(original.Path);
        _writes[1].Owner.Should().Be(original.Owner);
        JsonSerializer.Serialize(_writes[1].Body, Json).Should().Be(originalJson);
        _browser.Values[key].Should().Be(persisted);
        restored.Find("#equipment-recovery").Should().NotBeNull();
        restored.Find("#equipment-write-error").TextContent.Should().Contain("version changed");
        var current = asset with { Version = Guid.NewGuid(), Name = "New name", Active = !activeChange };
        _api.Setup(api => api.ReadInventoryAsync<SharedEquipment>(Root(scope) + $"/assets/{asset.Id:D}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(current));
        await restored.Find("#equipment-read-current").ClickAsync(new MouseEventArgs());
        restored.Find("#equipment-write-message").TextContent.Should().Contain("does not prove");
        restored.Find("#equipment-retry-write").Should().NotBeNull();
        restored.Find("#equipment-adopt-current").HasAttribute("disabled").Should().BeTrue();
        restored.Find("#equipment-clear-recovery").HasAttribute("disabled").Should().BeTrue();
        restored.Find("#equipment-acknowledge").Change(true);
        await restored.Find("#equipment-clear-recovery").ClickAsync(new MouseEventArgs());
        _browser.Values.Should().BeEmpty();
        _writes.Should().HaveCount(2, "dismissing recovery cannot undo or repeat server data");
        _saved.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_version_conflict_allows_comparison_but_preserves_draft_and_version_until_explicit_adoption(bool adopt)
    {
        var scope = Scope("equipment.read", "equipment.write");
        var asset = Asset(scope);
        var current = asset with { Name = "Someone else's change", Capacity = 5, Version = Guid.NewGuid() };
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Failure<SharedEquipment>(409, "BUSINESS_VERSION_CONFLICT")));
        _api.Setup(api => api.ReadInventoryAsync<SharedEquipment>(Root(scope) + $"/assets/{asset.Id:D}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(current));
        var cut = Panel(scope);
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(asset));
        cut.Find("#equipment-name").Change("My unsaved draft");
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        cut.FindAll("#equipment-recovery").Should().BeEmpty();

        await cut.Find("#equipment-read-selected").ClickAsync(new MouseEventArgs());

        cut.Find("#equipment-current-comparison").TextContent.Should().Contain("Someone else's change");
        cut.Find("#equipment-name").GetAttribute("value").Should().Be("My unsaved draft");
        _writes.Should().ContainSingle("a read-back never writes or automatically retries a conflict");
        if (adopt)
        {
            cut.Find("#equipment-adopt-current").Click();
            cut.Find("#equipment-name").GetAttribute("value").Should().Be("Someone else's change");
            _writes.Should().ContainSingle("adoption changes the draft, not the server");
        }
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        var sent = _writes[1].Body.Should().BeOfType<EquipmentSharingController.EquipmentChange>().Which;
        sent.ExpectedVersion.Should().Be(adopt ? current.Version : asset.Version);
        sent.Name.Should().Be(adopt ? "Someone else's change" : "My unsaved draft");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("actor")]
    [InlineData("equipment")]
    [InlineData("interval")]
    [InlineData("quantity")]
    [InlineData("state")]
    [InlineData("version")]
    public async Task Booking_success_must_match_original_request_and_valid_domain_state(string mismatch)
    {
        var scope = Scope("equipment.read", "equipment.booking.request");
        var asset = Asset(scope);
        Writes<EquipmentBooking>((_, path, body, _, _) =>
        {
            var request = (EquipmentSharingController.BookingRequest)body;
            var result = Booking(scope, EquipmentBookingState.Requested) with
            {
                Id = Guid.Parse(path.Split('/').Last()), EquipmentId = request.EquipmentId,
                RequestedBy = BusinessUser.ToString("D"), Start = request.Start, End = request.End, Quantity = request.Quantity
            };
            result = mismatch switch
            {
                "id" => result with { Id = Guid.NewGuid() },
                "actor" => result with { RequestedBy = OtherUser.ToString("D") },
                "equipment" => result with { EquipmentId = Guid.NewGuid() },
                "interval" => result with { End = request.End.AddHours(1) },
                "quantity" => result with { Quantity = request.Quantity + 1 },
                "state" => result with { State = (EquipmentBookingState)99 },
                "version" => result with { Version = Guid.Empty },
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(Ok(result));
        });
        var cut = Panel(scope, new WorkerDto("W1", "Worker", "P1", true));
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(asset));
        cut.Find("#equipment-start-booking").Click();
        cut.Find("#equipment-booking-start").Change("2030-01-02T03:04:05");
        cut.Find("#equipment-booking-end").Change("2030-01-02T04:04:05");

        await cut.Find("#equipment-booking-form").SubmitAsync(EventArgs.Empty);

        cut.Find("#equipment-write-error").TextContent.Should().Contain("scope or request");
        cut.Find("#equipment-retry-write").Should().NotBeNull();
        (await StoredIntent()).Confirmed.Should().BeFalse();
        _saved.Should().Be(0);
    }

    [Theory]
    [InlineData("checkout", EquipmentBookingState.Approved)]
    [InlineData("return", EquipmentBookingState.CheckedOut)]
    public async Task Valid_booking_with_incompatible_action_outcome_is_not_confirmation(string action, EquipmentBookingState initial)
    {
        var scope = Scope("equipment.booking.read", action == "checkout" ? "equipment.booking.checkout" : "equipment.booking.return");
        var booking = Booking(scope, initial);
        Writes<EquipmentBooking>((_, _, _, _, _) => Task.FromResult(Ok(booking with { Version = Guid.NewGuid() })));
        var cut = Panel(scope);
        await cut.InvokeAsync(() => cut.Instance.SelectBookingAsync(booking));

        await cut.Find("#equipment-" + action).ClickAsync(new MouseEventArgs());

        cut.Find("#equipment-write-error").TextContent.Should().Contain("scope or request");
        cut.Find("#equipment-retry-write").Should().NotBeNull();
        cut.FindAll("#equipment-write-message").Should().BeEmpty();
        (await StoredIntent()).Confirmed.Should().BeFalse();
        _saved.Should().Be(0);
    }

    [Theory]
    [InlineData("unchanged-version")]
    [InlineData("different-fields")]
    [InlineData("opposite-active")]
    public async Task Valid_asset_with_incompatible_action_outcome_is_not_confirmation(string mismatch)
    {
        var scope = Scope("equipment.read", "equipment.write");
        var asset = Asset(scope);
        var response = mismatch == "unchanged-version"
            ? asset with { Name = "Wanted name" }
            : asset with { Version = Guid.NewGuid() };
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Ok(response)));
        var cut = Panel(scope);
        await cut.InvokeAsync(() => cut.Instance.SelectEquipmentAsync(asset));

        if (mismatch == "opposite-active") await cut.Find("#equipment-toggle-active").ClickAsync(new MouseEventArgs());
        else
        {
            cut.Find("#equipment-name").Change("Wanted name");
            await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        }

        cut.Find("#equipment-write-error").TextContent.Should().Contain("scope or request");
        cut.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#equipment-retry-write").Should().NotBeNull();
        (await StoredIntent()).Confirmed.Should().BeFalse();
        _saved.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_is_isolated_by_both_user_and_business_identity(bool changeUser)
    {
        var scope = Scope("equipment.write");
        var cut = Panel(scope);
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        var original = await StoredIntent();
        var otherScope = scope with { Membership = scope.Membership with { UserId = changeUser ? "other-user" : "operator", BusinessUserId = OtherUser } };

        cut.Render(p => p.Add(c => c.Scope, otherScope).Add(c => c.UserId, otherScope.Membership.UserId));

        cut.WaitForAssertion(() => cut.Find("#equipment-new").HasAttribute("disabled").Should().BeFalse());
        cut.FindAll("#equipment-recovery").Should().BeEmpty();
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        _writes.Should().HaveCount(2);
        _writes[1].Owner.Should().Be(otherScope.Membership.UserId);
        _browser.Values.Should().HaveCount(2);
        cut.Render(p => p.Add(c => c.Scope, scope).Add(c => c.UserId, "operator"));
        cut.WaitForAssertion(() => cut.Find("#equipment-recovery").TextContent.Should().Contain(original.Id.ToString("D")));
        _writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Revoked_action_grant_disables_replay_without_discarding_uncertain_intent()
    {
        var scope = Scope("equipment.write");
        var cut = Panel(scope);
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        var original = await StoredIntent();

        cut.Render(p => p.Add(c => c.Scope, scope with { Membership = scope.Membership with { Permissions = ["equipment.read"] } }));

        cut.Find("#equipment-retry-write").HasAttribute("disabled").Should().BeTrue();
        await cut.Find("#equipment-retry-write").ClickAsync(new MouseEventArgs());
        _writes.Should().ContainSingle();
        (await StoredIntent()).Id.Should().Be(original.Id);
    }

    [Fact]
    public async Task Durable_storage_acknowledgement_precedes_send_and_double_submit_cannot_duplicate_it()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _browser.Before = (operation, _) => operation == "sessionStorage.setItem" ? gate.Task : Task.CompletedTask;
        var cut = Panel(Scope("equipment.write"));
        FillNewAsset(cut);
        try
        {
            var first = cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
            cut.WaitForAssertion(() => _browser.Calls.Should().Contain("sessionStorage.setItem"));
            _browser.Values.Should().BeEmpty();
            _writes.Should().BeEmpty("no command may leave before the durable intent is acknowledged");
            cut.Find("#equipment-asset-form fieldset").HasAttribute("disabled").Should().BeTrue();
            await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
            _writes.Should().BeEmpty();
            gate.SetResult();
            await first;
            _writes.Should().ContainSingle();
            (await StoredIntent()).Id.Should().Be(((EquipmentSharingController.EquipmentCreate)_writes[0].Body).OperationId);
        }
        finally { gate.TrySetResult(); }
    }

    [Theory]
    [InlineData("sessionStorage.getItem")]
    [InlineData("sessionStorage.setItem")]
    public async Task Storage_failure_blocks_HTTP_and_new_commands(string operation)
    {
        _browser.Before = (name, _) => name == operation ? Task.FromException(new JSException("Storage unavailable")) : Task.CompletedTask;
        var cut = Panel(Scope("equipment.write"));
        if (operation == "sessionStorage.setItem")
        {
            FillNewAsset(cut);
            await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        }
        cut.WaitForAssertion(() => cut.Find("#equipment-write-error").TextContent.Should().Contain("recovery record"));
        cut.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("sessionStorage.getItem", false)]
    [InlineData("sessionStorage.setItem", false)]
    [InlineData("sessionStorage.setItem", true)]
    public async Task Storage_timeout_blocks_new_commands_until_explicit_reload_even_if_set_was_applied(string operation, bool applied)
    {
        Func<string, string, Task> timeout = (name, _) => name == operation
            ? Task.FromException(new TaskCanceledException("JS acknowledgement timed out")) : Task.CompletedTask;
        if (applied) _browser.After = timeout;
        else _browser.Before = timeout;
        var cut = Panel(Scope("equipment.write"));
        if (operation == "sessionStorage.setItem")
        {
            FillNewAsset(cut);
            await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        }

        cut.WaitForAssertion(() => cut.Find("#equipment-write-error").TextContent.Should().Contain("recovery record"));
        cut.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
        _writes.Should().BeEmpty();
        _browser.Values.Should().HaveCount(applied ? 1 : 0);
        _browser.Before = null;
        _browser.After = null;
        var original = applied ? await StoredIntent() : null;
        await cut.Find("#equipment-reload-recovery").ClickAsync(new MouseEventArgs());

        _writes.Should().BeEmpty("reloading storage never automatically replays an uncertain intent");
        if (applied)
        {
            cut.Find("#equipment-recovery").TextContent.Should().Contain(original!.Id.ToString("D"));
            cut.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
            await cut.Find("#equipment-retry-write").ClickAsync(new MouseEventArgs());
            var create = _writes.Should().ContainSingle().Which.Body.Should().BeOfType<EquipmentSharingController.EquipmentCreate>().Which;
            create.OperationId.Should().Be(original!.Id);
        }
        else cut.Find("#equipment-new").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Corrupt_protected_record_blocks_writes_instead_of_starting_a_new_intent()
    {
        var scope = Scope("equipment.write");
        var cut = Panel(scope);
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        var key = _browser.Values.Keys.Single();
        await DisposeComponentsAsync();
        _browser.Values[key] = "not a protected payload";

        var restored = Panel(scope);

        restored.WaitForAssertion(() => restored.Find("#equipment-write-error").TextContent.Should().Contain("recovery record"));
        restored.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
        restored.FindAll("#equipment-retry-write").Should().BeEmpty();
        _writes.Should().ContainSingle();
        _browser.Values[key].Should().Be("not a protected payload");
    }

    [Fact]
    public async Task Failed_recovery_cleanup_preserves_confirmed_success_across_reload_without_replay()
    {
        var scope = Scope("equipment.write");
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Ok(Asset(scope))));
        _browser.Before = (name, _) => name == "sessionStorage.removeItem" ? Task.FromException(new JSException("Cannot remove")) : Task.CompletedTask;
        var cut = Panel(scope);
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        cut.Find("#equipment-write-message").TextContent.Should().Contain("Saved");
        cut.Find("#equipment-write-error").TextContent.Should().Contain("write succeeded");
        (await StoredIntent()).Confirmed.Should().BeTrue();
        _saved.Should().Be(1);
        await DisposeComponentsAsync();

        var restored = Panel(scope);

        restored.WaitForAssertion(() => restored.Find("#equipment-recovery").TextContent.Should().Contain("Write confirmed"));
        restored.FindAll("#equipment-retry-write").Should().BeEmpty();
        _writes.Should().ContainSingle();
        _browser.Before = null;
        await restored.Find("#equipment-clear-recovery").ClickAsync(new MouseEventArgs());
        _browser.Values.Should().BeEmpty();
        _writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_failure_does_not_turn_a_confirmed_write_into_a_retryable_failure()
    {
        var scope = Scope("equipment.write");
        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Ok(Asset(scope))));
        var cut = Panel(scope, saved: () => Task.FromException(new InvalidOperationException("Refresh failed")));
        FillNewAsset(cut);
        await cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
        cut.Find("#equipment-write-message").TextContent.Should().Contain("Saved");
        cut.Find("#equipment-write-error").TextContent.Should().Contain("write succeeded").And.Contain("refreshed");
        cut.FindAll("#equipment-retry-write").Should().BeEmpty();
        _browser.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OPERATOR")]
    [InlineData("different-user")]
    public void Owner_must_match_membership_exactly_before_reading_recovery_or_writing(string? owner)
    {
        var cut = Render<EquipmentWorkflowPanel>(p => p.Add(c => c.Scope, Scope("equipment.write")).Add(c => c.UserId, owner));
        cut.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("current user and business scope");
        _browser.Calls.Should().BeEmpty();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Owner_change_while_persisting_prevents_send_and_original_owner_alone_can_recover()
    {
        var scope = Scope("equipment.write");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _browser.Before = (operation, _) => operation == "sessionStorage.setItem" ? gate.Task : Task.CompletedTask;
        var cut = Panel(scope);
        FillNewAsset(cut);
        try
        {
            var save = cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
            cut.WaitForAssertion(() => _browser.Calls.Should().Contain("sessionStorage.setItem"));
            cut.Render(p => p.Add(c => c.UserId, "other-user"));
            gate.SetResult();
            await save;
            _writes.Should().BeEmpty();
            cut.FindAll("#equipment-recovery, #equipment-asset-form").Should().BeEmpty();
            _browser.Values.Should().ContainSingle();
            cut.Render(p => p.Add(c => c.UserId, "operator"));
            cut.WaitForAssertion(() => cut.Find("#equipment-retry-write").Should().NotBeNull());
            _writes.Should().BeEmpty("returning to the original owner still requires an explicit replay");
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public async Task Late_old_scope_success_cannot_clear_a_new_scope_command_or_publish_success()
    {
        var oldScope = Scope("equipment.write");
        var nextScope = ScopeAt(2, "equipment.write");
        var oldReply = Reply<SharedEquipment>();
        var nextReply = Reply<SharedEquipment>();
        Writes<SharedEquipment>((_, path, _, _, _) => path.StartsWith(Root(oldScope), StringComparison.Ordinal) ? oldReply.Task : nextReply.Task);
        var cut = Panel(oldScope);
        FillNewAsset(cut);
        try
        {
            var first = cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
            cut.WaitForAssertion(() => _writes.Should().ContainSingle());
            var oldKey = _browser.Values.Keys.Single();
            cut.Render(p => p.Add(c => c.Scope, nextScope));
            cut.WaitForAssertion(() => cut.Find("#equipment-new").HasAttribute("disabled").Should().BeFalse());
            _writes[0].Token.IsCancellationRequested.Should().BeTrue();
            FillNewAsset(cut);
            var second = cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
            cut.WaitForAssertion(() => _writes.Should().HaveCount(2));
            var nextKey = _browser.Values.Keys.Single(key => key != oldKey);
            var nextRecord = _browser.Values[nextKey];
            oldReply.SetResult(Ok(Asset(oldScope) with { Name = "Old result" }));
            await first;
            cut.FindAll("#equipment-write-message").Should().BeEmpty();
            cut.Markup.Should().NotContain("Old result");
            cut.Find("#equipment-new").HasAttribute("disabled").Should().BeTrue();
            _browser.Values[nextKey].Should().Be(nextRecord);
            _browser.Values.Should().ContainKey(oldKey);
            _saved.Should().Be(0);
            nextReply.SetResult(Ok(Asset(nextScope)));
            await second;
            _saved.Should().Be(1);
            _browser.Values.Keys.Should().Equal(oldKey);
        }
        finally
        {
            oldReply.TrySetResult(Failure<SharedEquipment>(503));
            nextReply.TrySetResult(Failure<SharedEquipment>(503));
        }
    }

    [Fact]
    public async Task Disposing_cancels_send_but_retains_durable_intent_for_explicit_reload_recovery()
    {
        var scope = Scope("equipment.write");
        var reply = Reply<SharedEquipment>();
        Writes<SharedEquipment>((_, _, _, _, _) => reply.Task);
        var cut = Panel(scope);
        FillNewAsset(cut);
        try
        {
            var save = cut.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);
            cut.WaitForAssertion(() => _writes.Should().ContainSingle());
            await DisposeComponentsAsync();
            _writes[0].Token.IsCancellationRequested.Should().BeTrue();
            reply.SetCanceled(_writes[0].Token);
            await save;
            _browser.Values.Should().ContainSingle();
            _saved.Should().Be(0);
            var restored = Panel(scope);
            restored.WaitForAssertion(() => restored.Find("#equipment-retry-write").Should().NotBeNull());
            _writes.Should().ContainSingle();
        }
        finally { reply.TrySetResult(Failure<SharedEquipment>(503)); }
    }

    private IRenderedComponent<EquipmentWorkflowPanel> Panel(InventoryAccessScope scope, WorkerDto? worker = null, Func<Task>? saved = null)
    {
        var cut = Render<EquipmentWorkflowPanel>(p => p.Add(c => c.Scope, scope).Add(c => c.UserId, "operator")
            .Add(c => c.Worker, worker).Add(c => c.Saved, saved ?? (() => { ++_saved; return Task.CompletedTask; })));
        cut.WaitForAssertion(() => cut.Find("#inventory-equipment-workflows").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    private static void FillNewAsset(IRenderedComponent<EquipmentWorkflowPanel> cut)
    {
        cut.Find("#equipment-new").Click();
        cut.Find("#equipment-code").Change("PRESS-1");
        cut.Find("#equipment-name").Change("Press one");
        cut.Find("#equipment-capacity").Change("2");
        cut.Find("#equipment-requires-approval").Change(true);
    }

    private void Writes<T>(Func<HttpMethod, string, object, string, CancellationToken, Task<(T?, int, string?, string?)>> respond) where T : class
        => _api.Setup(api => api.WriteInventoryAsync<T>(It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod method, string path, object body, string owner, CancellationToken ct) =>
            {
                _writes.Add((method, path, body, owner, ct));
                return respond(method, path, body, owner, ct);
            });

    private async Task<EquipmentWorkflowPanel.PendingWrite> StoredIntent()
    {
        var stored = await _storage.GetAsync<EquipmentWorkflowPanel.PendingWrite>(_browser.Values.Keys.Single());
        stored.Success.Should().BeTrue();
        return stored.Value!;
    }

    private static (T?, int, string?, string?) Ok<T>(T value) where T : class => (value, 200, null, null);
    private static (T?, int, string?, string?) Failure<T>(int status, string? code = null) where T : class => (null, status, code, "Request failed");
    private static TaskCompletionSource<(T?, int, string?, string?)> Reply<T>() where T : class => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InventoryAccessScope Scope(params string[] grants) => ScopeAt(1, grants);
    private static InventoryAccessScope ScopeAt(int index, params string[] grants)
    {
        var organization = Guid.Parse($"20000000-0000-0000-0000-{index:000000000000}");
        return new(new BusinessMembership(Tenant, organization, "operator", BusinessUser, true, 1, grants),
            new(Tenant, organization, "P" + index, Guid.NewGuid(), true), new("P" + index, "Plant " + index, "", "KR", "Asia/Seoul"));
    }
    private static BusinessScope Business(InventoryAccessScope scope) => new("NexaOne.MES", scope.Membership.TenantId.ToString("D"), scope.Membership.OrganizationId.ToString("D"));
    private static string Root(InventoryAccessScope scope) => $"api/v1/ivt/shared-equipment/{scope.Membership.TenantId:D}/{scope.Membership.OrganizationId:D}";
    private static SharedEquipment Asset(InventoryAccessScope scope) => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "PRESS-1", "Press one", 2, true);
    private static EquipmentBooking Booking(InventoryAccessScope scope, EquipmentBookingState state) => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddHours(1), 1, OtherUser.ToString("D"), state);
}

// Exercise ProtectedSessionStorage's real encryption/serialization; only the browser I/O is replaced.
internal sealed class InventorySessionStorageJs : IJSRuntime
{
    public Dictionary<string, string> Values { get; } = new();
    public List<string> Calls { get; } = [];
    public Func<string, string, Task>? Before { get; set; }
    public Func<string, string, Task>? After { get; set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (string)args![0]!;
        Calls.Add(identifier);
        if (Before is not null) await Before(identifier, key);
        switch (identifier)
        {
            case "sessionStorage.getItem":
                return Values.TryGetValue(key, out var value) ? (TValue)(object)value : default!;
            case "sessionStorage.setItem":
                Values[key] = (string)args[1]!;
                break;
            case "sessionStorage.removeItem":
                Values.Remove(key);
                break;
            default:
                throw new InvalidOperationException("Unexpected browser storage operation: " + identifier);
        }
        if (After is not null) await After(identifier, key);
        return default!;
    }
}
