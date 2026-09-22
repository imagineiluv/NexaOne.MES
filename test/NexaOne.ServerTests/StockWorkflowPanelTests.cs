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

namespace NexaOne.ServerTests;

public sealed class StockWorkflowPanelTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly InventorySessionStorageJs _browser = new();
    private readonly ProtectedSessionStorage _storage;
    private readonly List<(HttpMethod Method, string Path, object Body, string Owner, CancellationToken Token)> _writes = [];
    private readonly List<string> _reads = [];
    private int _saved;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid BusinessUser = Guid.Parse("30000000-0000-0000-0000-000000000001");

    public StockWorkflowPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _storage = new ProtectedSessionStorage(_browser, new EphemeralDataProtectionProvider());
        var ui = new UiTextService();
        ui.Load("EnUs", new());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(ui);
        Services.AddSingleton(_storage);
        Writes<Warehouse>((_, _, _, _, _) => Task.FromResult(Failure<Warehouse>(503)));
        Writes<StockMovement>((_, _, _, _, _) => Task.FromResult(Failure<StockMovement>(503)));
        Writes<StockReservation>((_, _, _, _, _) => Task.FromResult(Failure<StockReservation>(503)));
    }

    [Fact]
    public async Task Receipt_posting_sends_selected_variant_and_warehouse_after_durable_intent_and_shows_returned_movement()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.post");
        var warehouse = Warehouse(scope, "WH-1");
        var product = Product(scope, "P-001");
        var variant = Variant(scope, product);
        Reads<ProductVariant>(path => path == Root(scope) + "/products/P-001" ? Ok(variant) : Failure<ProductVariant>(404));
        Writes<StockMovement>(async (_, _, body, owner, _) =>
        {
            var intent = await StoredIntent();
            intent.UserId.Should().Be("operator");
            intent.BusinessUserId.Should().Be(BusinessUser);
            intent.TenantId.Should().Be(Tenant);
            intent.OrganizationId.Should().Be(scope.Membership.OrganizationId);
            intent.Payload.GetRawText().Should().Be(JsonSerializer.Serialize(body, Json));
            intent.Confirmed.Should().BeFalse();
            owner.Should().Be("operator");
            var posting = (StockPosting)body;
            return Ok(Movement(scope, posting));
        });
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-selected-variant").TextContent.Should().Contain(variant.Sku).And.Contain(variant.Unit);

        cut.Find("#stock-start-posting").Click();
        cut.Find("#stock-posting-kind").Change("Receipt");
        cut.Find("#stock-posting-to").Change(warehouse.Id.ToString("D"));
        cut.Find("#stock-posting-quantity").Change("12.5");
        cut.Find("#stock-posting-reference").Change("GRN-1");
        await cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);

        var sent = _writes.Should().ContainSingle().Which;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Path.Should().Be(Root(scope) + "/movements");
        var posting = sent.Body.Should().BeOfType<StockPosting>().Which;
        posting.OperationId.Should().NotBeEmpty();
        posting.Should().Be(new StockPosting(posting.OperationId, variant.Id, StockMovementKind.Receipt, 12.5m, null, warehouse.Id, "GRN-1"));
        cut.Find("#stock-movement-detail").TextContent.Should().Contain("GRN-1").And.Contain("12.5");
        _saved.Should().Be(1);
        _browser.Values.Should().BeEmpty();
        cut.FindAll("#stock-recovery, #stock-write-error").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Issue", "A", "", "3", "A", null, "3")]
    [InlineData("Issue", "A", "B", "3", "A", null, "3")]
    [InlineData("Transfer", "A", "B", "0.000001", "A", "B", "0.000001")]
    [InlineData("Adjustment", "", "B", "-4.25", null, "B", "-4.25")]
    [InlineData("Receipt", "A", "B", "7", null, "B", "7")]
    public async Task Each_movement_kind_sends_only_the_warehouses_it_uses_and_keeps_exact_decimal_quantity(
        string kind, string from, string to, string quantity, string? expectedFrom, string? expectedTo, string expectedQuantity)
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.post");
        var warehouses = new Dictionary<string, Warehouse> { ["A"] = Warehouse(scope, "WH-A"), ["B"] = Warehouse(scope, "WH-B") };
        var product = Product(scope, "P-001");
        var variant = Variant(scope, product);
        Reads<ProductVariant>(_ => Ok(variant));
        Writes<StockMovement>((_, _, body, _, _) => Task.FromResult(Ok(Movement(scope, (StockPosting)body))));
        var cut = Panel(scope, warehouses.Values.ToArray());
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-start-posting").Click();
        cut.Find("#stock-posting-kind").Change(kind);
        cut.Find("#stock-posting-from").Change(from.Length == 0 ? "" : warehouses[from].Id.ToString("D"));
        cut.Find("#stock-posting-to").Change(to.Length == 0 ? "" : warehouses[to].Id.ToString("D"));
        cut.Find("#stock-posting-quantity").Change(quantity);
        cut.Find("#stock-posting-reference").Change("REF");

        await cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);

        var posting = _writes.Should().ContainSingle().Which.Body.Should().BeOfType<StockPosting>().Which;
        posting.Kind.Should().Be(Enum.Parse<StockMovementKind>(kind));
        posting.FromWarehouseId.Should().Be(expectedFrom is null ? null : warehouses[expectedFrom].Id);
        posting.ToWarehouseId.Should().Be(expectedTo is null ? null : warehouses[expectedTo].Id);
        posting.Quantity.Should().Be(decimal.Parse(expectedQuantity, System.Globalization.CultureInfo.InvariantCulture));
        posting.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(expectedQuantity);
        cut.Find("#stock-movement-detail").TextContent.Should().Contain("REF");
    }

    [Theory]
    [InlineData("Receipt", "", "", "1")]
    [InlineData("Receipt", "", "B", "0")]
    [InlineData("Receipt", "", "B", "-1")]
    [InlineData("Receipt", "", "B", "1.2345678")]
    [InlineData("Receipt", "", "B", "1,5")]
    [InlineData("Issue", "", "B", "1")]
    [InlineData("Transfer", "A", "A", "1")]
    [InlineData("Transfer", "A", "", "1")]
    [InlineData("Adjustment", "", "B", "0")]
    [InlineData("Adjustment", "", "inactive", "1")]
    public async Task Invalid_posting_input_is_rejected_locally_without_HTTP_or_a_recovery_record(string kind, string from, string to, string quantity)
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.post");
        var warehouses = new Dictionary<string, Warehouse>
        {
            ["A"] = Warehouse(scope, "WH-A"), ["B"] = Warehouse(scope, "WH-B"), ["inactive"] = Warehouse(scope, "WH-X") with { Active = false }
        };
        var product = Product(scope, "P-001");
        Reads<ProductVariant>(_ => Ok(Variant(scope, product)));
        var cut = Panel(scope, warehouses.Values.ToArray());
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-start-posting").Click();
        cut.Find("#stock-posting-kind").Change(kind);
        cut.Find("#stock-posting-from").Change(from.Length == 0 ? "" : warehouses[from].Id.ToString("D"));
        cut.Find("#stock-posting-to").Change(to.Length == 0 ? "" : warehouses[to].Id.ToString("D"));
        cut.Find("#stock-posting-quantity").Change(quantity);
        cut.Find("#stock-posting-reference").Change("REF");

        await cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);

        _writes.Should().BeEmpty();
        _browser.Values.Should().BeEmpty();
        cut.Find("#stock-write-error").TextContent.Should().Contain("nonzero quantity");
        cut.Find("#stock-posting-quantity").GetAttribute("value").Should().Be(quantity);
    }

    [Fact]
    public async Task Reversal_uses_selected_movement_version_with_a_new_operation_ID_and_the_entered_reference()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.reverse");
        var warehouse = Warehouse(scope, "WH-1");
        var original = Movement(scope, new StockPosting(Guid.NewGuid(), Guid.NewGuid(), StockMovementKind.Receipt, 5m, null, warehouse.Id, "GRN-1"));
        Writes<StockMovement>((_, _, body, _, _) =>
        {
            var command = (StockController.ReversalCommand)body;
            var reversal = original with
            {
                Id = Guid.NewGuid(), Version = Guid.NewGuid(), ReversalOfId = original.Id,
                Posting = original.Posting with { OperationId = command.OperationId, Kind = StockMovementKind.Reversal, Reference = command.Reference },
                Deltas = [new StockDelta(warehouse.Id, -5m)]
            };
            return Task.FromResult(Ok(reversal));
        });
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectMovementAsync(original));
        cut.Find("#stock-movement-detail").TextContent.Should().Contain("GRN-1").And.Contain("Receipt");
        cut.Find("#stock-reversal-reference").Change("RET-9");

        await cut.Find("#stock-reverse-movement").ClickAsync(new MouseEventArgs());

        var sent = _writes.Should().ContainSingle().Which;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Path.Should().Be(Root(scope) + $"/movements/{original.Id:D}/reverse");
        var command = sent.Body.Should().BeOfType<StockController.ReversalCommand>().Which;
        command.Version.Should().Be(original.Version);
        command.OperationId.Should().NotBeEmpty().And.NotBe(original.Posting.OperationId);
        command.Reference.Should().Be("RET-9");
        (await StoredIntentOrNull()).Should().BeNull();
        cut.Find("#stock-movement-detail").TextContent.Should().Contain("Reversal").And.Contain("RET-9");
        cut.FindAll("#stock-reverse-movement").Should().BeEmpty("a reversal entry cannot be reversed again");
        _saved.Should().Be(1);
    }

    [Fact]
    public async Task Already_reversed_movement_and_missing_reverse_grant_hide_the_reversal_action()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.reverse");
        var warehouse = Warehouse(scope, "WH-1");
        var reversed = Movement(scope, new StockPosting(Guid.NewGuid(), Guid.NewGuid(), StockMovementKind.Issue, 2m, warehouse.Id, null, "ISS-1"))
            with { ReversedById = Guid.NewGuid() };
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectMovementAsync(reversed));
        cut.Find("#stock-movement-detail").TextContent.Should().Contain("ISS-1");
        cut.FindAll("#stock-reverse-movement").Should().BeEmpty();

        var reader = Panel(Scope("stock.read", "stock.warehouse.read"), [warehouse]);
        await reader.InvokeAsync(() => reader.Instance.SelectMovementAsync(reversed with { ReversedById = null }));
        reader.Find("#stock-movement-detail").TextContent.Should().Contain("ISS-1");
        reader.FindAll("#stock-reverse-movement, #stock-reversal-reference").Should().BeEmpty();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Reservation_is_requested_with_a_fresh_operation_ID_and_released_with_its_returned_version()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.reserve", "stock.release");
        var warehouse = Warehouse(scope, "WH-1");
        var product = Product(scope, "P-001");
        var variant = Variant(scope, product);
        Reads<ProductVariant>(_ => Ok(variant));
        StockReservation? reserved = null;
        Writes<StockReservation>((_, path, body, _, _) =>
        {
            if (body is StockController.ReservationCommand command)
                return Task.FromResult(Ok(reserved = Reservation(scope, command)));
            path.Should().EndWith($"/reservations/{reserved!.Id:D}/release");
            ((StockController.VersionedCommand)body).Version.Should().Be(reserved.Version);
            return Task.FromResult(Ok(reserved with { Version = Guid.NewGuid(), State = StockReservationState.Released }));
        });
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-start-reservation").Click();
        cut.Find("#stock-reservation-warehouse").Change(warehouse.Id.ToString("D"));
        cut.Find("#stock-reservation-quantity").Change("2.5");
        cut.Find("#stock-reservation-reference").Change("WO-7");
        await cut.Find("#stock-reservation-form").SubmitAsync(EventArgs.Empty);

        var sent = _writes.Should().ContainSingle().Which;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Path.Should().Be(Root(scope) + "/reservations");
        var command = sent.Body.Should().BeOfType<StockController.ReservationCommand>().Which;
        command.OperationId.Should().NotBeEmpty();
        command.Should().Be(new StockController.ReservationCommand(command.OperationId, variant.Id, warehouse.Id, 2.5m, "WO-7"));
        cut.Find("#stock-reservation-detail").TextContent.Should().Contain("WO-7").And.Contain("Active");

        await cut.Find("#stock-release-reservation").ClickAsync(new MouseEventArgs());

        _writes.Should().HaveCount(2);
        cut.Find("#stock-reservation-detail").TextContent.Should().Contain("Released");
        cut.FindAll("#stock-release-reservation, #stock-consume-reservation").Should().BeEmpty();
        _saved.Should().Be(2);
        _browser.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("other-operation")]
    [InlineData("other-kind")]
    public async Task Consumption_is_confirmed_only_by_an_issue_under_the_reservation_operation(string outcome)
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.consume");
        var warehouse = Warehouse(scope, "WH-1");
        var reservation = Reservation(scope, new StockController.ReservationCommand(Guid.NewGuid(), Guid.NewGuid(), warehouse.Id, 3m, "WO-1"));
        Writes<StockMovement>((_, path, body, _, _) =>
        {
            path.Should().Be(Root(scope) + $"/reservations/{reservation.Id:D}/consume");
            ((StockController.VersionedCommand)body).Version.Should().Be(reservation.Version);
            var posting = new StockPosting(outcome == "other-operation" ? Guid.NewGuid() : reservation.OperationId, reservation.VariantId,
                outcome == "other-kind" ? StockMovementKind.Receipt : StockMovementKind.Issue, reservation.Quantity,
                outcome == "other-kind" ? null : warehouse.Id, outcome == "other-kind" ? warehouse.Id : null, reservation.Reference);
            return Task.FromResult(Ok(Movement(scope, posting)));
        });
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectReservationAsync(reservation));

        await cut.Find("#stock-consume-reservation").ClickAsync(new MouseEventArgs());

        _writes.Should().ContainSingle();
        if (outcome == "exact")
        {
            cut.Find("#stock-movement-detail").TextContent.Should().Contain("Issue").And.Contain("WO-1");
            cut.FindAll("#stock-recovery").Should().BeEmpty();
            _saved.Should().Be(1);
        }
        else
        {
            cut.Find("#stock-write-error").TextContent.Should().Contain("does not match");
            cut.Find("#stock-recovery").TextContent.Should().Contain("Consume reservation");
            _saved.Should().Be(0);
            (await StoredIntent()).Confirmed.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Reservation_lookup_by_ID_reads_the_current_record_and_rejects_foreign_scope()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.release");
        var warehouse = Warehouse(scope, "WH-1");
        var mine = Reservation(scope, new StockController.ReservationCommand(Guid.NewGuid(), Guid.NewGuid(), warehouse.Id, 1m, "WO-2"));
        var foreign = Reservation(ScopeAt(2, "stock.read"), new StockController.ReservationCommand(Guid.NewGuid(), Guid.NewGuid(), warehouse.Id, 1m, "WO-3"));
        Reads<StockReservation>(path => path == Root(scope) + $"/reservations/{mine.Id:D}" ? Ok(mine)
            : path == Root(scope) + $"/reservations/{foreign.Id:D}" ? Ok(foreign) : Failure<StockReservation>(404, "RESERVATION_NOT_FOUND"));
        var cut = Panel(scope, [warehouse]);

        cut.Find("#stock-reservation-id").Change(mine.Id.ToString("D"));
        await cut.Find("#stock-read-reservation").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-reservation-detail").TextContent.Should().Contain("WO-2");
        cut.FindAll("#stock-release-reservation").Should().ContainSingle();

        cut.Find("#stock-reservation-id").Change(foreign.Id.ToString("D"));
        await cut.Find("#stock-read-reservation").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-write-error").TextContent.Should().Contain("does not match");
        cut.Find("#stock-reservation-detail").TextContent.Should().Contain("WO-2", "a rejected read keeps the earlier selection");

        cut.Find("#stock-reservation-id").Change(Guid.NewGuid().ToString("D"));
        await cut.Find("#stock-read-reservation").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-write-error").TextContent.Should().Contain("not found");
        cut.Find("#stock-reservation-id").Change("not-a-guid");
        await cut.Find("#stock-read-reservation").ClickAsync(new MouseEventArgs());
        _reads.Should().HaveCount(3);
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Warehouse_writer_can_create_update_and_toggle_using_each_returned_version()
    {
        var scope = Scope("stock.warehouse.write", "stock.warehouse.read");
        var created = Warehouse(scope, "WH-9") with { Name = "Ninth" };
        var updated = created with { Name = "Ninth updated", Version = Guid.NewGuid() };
        var inactive = updated with { Active = false, Version = Guid.NewGuid() };
        var active = inactive with { Active = true, Version = Guid.NewGuid() };
        var responses = new Queue<Warehouse>([created, updated, inactive, active]);
        Writes<Warehouse>(async (_, _, body, owner, _) =>
        {
            var intent = await StoredIntent();
            intent.Payload.GetRawText().Should().Be(JsonSerializer.Serialize(body, Json));
            intent.Confirmed.Should().BeFalse();
            owner.Should().Be("operator");
            return Ok(responses.Dequeue());
        });
        var cut = Panel(scope);
        cut.Find("#stock-new-warehouse").Click();
        cut.Find("#stock-warehouse-code").Change("WH-9");
        cut.Find("#stock-warehouse-name").Change("Ninth");

        await cut.Find("#stock-warehouse-form").SubmitAsync(EventArgs.Empty);

        var create = _writes[0].Body.Should().BeOfType<StockController.WarehouseCreate>().Which;
        create.OperationId.Should().NotBeEmpty();
        create.Should().Be(new StockController.WarehouseCreate(create.OperationId, "WH-9", "Ninth"));
        _writes[0].Method.Should().Be(HttpMethod.Post);
        _writes[0].Path.Should().Be(Root(scope) + "/warehouses");
        cut.Find("#stock-selected-warehouse").TextContent.Should().Contain("Ninth");
        cut.Find("#stock-warehouse-name").Change("Ninth updated");
        await cut.Find("#stock-warehouse-form").SubmitAsync(EventArgs.Empty);
        _writes[1].Body.Should().Be(new StockController.WarehouseChange(created.Version, "WH-9", "Ninth updated"));
        _writes[1].Path.Should().Be(Root(scope) + $"/warehouses/{created.Id:D}");

        await cut.Find("#stock-toggle-warehouse-active").ClickAsync(new MouseEventArgs());
        _writes[2].Body.Should().Be(new StockController.ActiveChange(updated.Version, false));
        cut.Find("#stock-toggle-warehouse-active").TextContent.Should().Contain("Activate warehouse");
        await cut.Find("#stock-toggle-warehouse-active").ClickAsync(new MouseEventArgs());
        _writes[3].Body.Should().Be(new StockController.ActiveChange(inactive.Version, true));
        _writes.Skip(1).Should().OnlyContain(write => write.Method == HttpMethod.Put);
        _writes.Skip(2).Should().OnlyContain(write => write.Path == Root(scope) + $"/warehouses/{created.Id:D}/active");
        _saved.Should().Be(4);
        _browser.Values.Should().BeEmpty();
        cut.FindAll("#stock-recovery, #stock-write-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Selected_warehouse_row_loads_the_editor_and_a_reader_without_write_grant_gets_no_form()
    {
        var scope = Scope("stock.warehouse.write", "stock.warehouse.read");
        var warehouse = Warehouse(scope, "WH-2");
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectWarehouseAsync(warehouse));
        cut.Find("#stock-warehouse-code").GetAttribute("value").Should().Be("WH-2");
        cut.Find("#stock-warehouse-name").GetAttribute("value").Should().Be(warehouse.Name);

        var reader = Panel(Scope("stock.warehouse.read", "stock.read"), [warehouse]);
        await reader.InvokeAsync(() => reader.Instance.SelectWarehouseAsync(warehouse));
        reader.FindAll("#stock-new-warehouse, #stock-warehouse-form, #stock-toggle-warehouse-active").Should().BeEmpty();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Lost_posting_response_survives_reload_and_replaying_the_same_body_confirms_via_the_earlier_ledger_entry()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.post");
        var warehouse = Warehouse(scope, "WH-1");
        var product = Product(scope, "P-001");
        var variant = Variant(scope, product);
        Reads<ProductVariant>(_ => Ok(variant));
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-start-posting").Click();
        cut.Find("#stock-posting-kind").Change("Receipt");
        cut.Find("#stock-posting-to").Change(warehouse.Id.ToString("D"));
        cut.Find("#stock-posting-quantity").Change("4");
        cut.Find("#stock-posting-reference").Change("GRN-4");
        await cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);
        var original = (StockPosting)_writes.Single().Body;
        cut.Find("#stock-recovery").TextContent.Should().Contain("outcome needs checking").And.Contain(original.OperationId.ToString("D"));
        cut.FindAll("#stock-start-posting").Should().ContainSingle().Which.HasAttribute("disabled").Should().BeTrue();
        cut.Dispose();

        var reloaded = Panel(scope, [warehouse]);
        reloaded.Find("#stock-recovery").TextContent.Should().Contain("Post movement");
        reloaded.FindAll("#stock-read-current").Should().BeEmpty("an unconfirmed posting has no ledger ID to read");
        Writes<StockMovement>((_, _, body, _, _) => Task.FromResult(Ok(Movement(scope, (StockPosting)body))));

        await reloaded.Find("#stock-retry-write").ClickAsync(new MouseEventArgs());

        _writes.Should().HaveCount(2);
        _writes[1].Body.Should().Be(original);
        _writes[1].Path.Should().Be(Root(scope) + "/movements");
        reloaded.Find("#stock-movement-detail").TextContent.Should().Contain("GRN-4");
        reloaded.FindAll("#stock-recovery").Should().BeEmpty();
        _browser.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData(400, "nonzero quantity")]
    [InlineData(403, "permission changed")]
    [InlineData(404, "not found")]
    [InlineData(409, "conflicts with current data")]
    public async Task First_known_rejection_clears_the_intent_and_allows_a_new_request(int status, string message)
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.reserve");
        var warehouse = Warehouse(scope, "WH-1");
        var product = Product(scope, "P-001");
        Reads<ProductVariant>(_ => Ok(Variant(scope, product)));
        Writes<StockReservation>((_, _, _, _, _) => Task.FromResult(Failure<StockReservation>(status)));
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-start-reservation").Click();
        cut.Find("#stock-reservation-warehouse").Change(warehouse.Id.ToString("D"));
        cut.Find("#stock-reservation-quantity").Change("1");
        cut.Find("#stock-reservation-reference").Change("WO-1");

        await cut.Find("#stock-reservation-form").SubmitAsync(EventArgs.Empty);

        cut.Find("#stock-write-error").TextContent.Should().Contain(message);
        cut.FindAll("#stock-recovery").Should().BeEmpty();
        _browser.Values.Should().BeEmpty();
        cut.Find("#stock-reservation-reference").GetAttribute("value").Should().Be("WO-1", "a rejected draft is preserved");
        await cut.Find("#stock-reservation-form").SubmitAsync(EventArgs.Empty);
        _writes.Should().HaveCount(2);
        ((StockController.ReservationCommand)_writes[1].Body).OperationId.Should().NotBe(((StockController.ReservationCommand)_writes[0].Body).OperationId);
    }

    [Fact]
    public async Task Lost_release_response_is_not_resolved_by_a_replay_409_but_by_reading_and_explicitly_adopting_current_state()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.release");
        var warehouse = Warehouse(scope, "WH-1");
        var reservation = Reservation(scope, new StockController.ReservationCommand(Guid.NewGuid(), Guid.NewGuid(), warehouse.Id, 1m, "WO-5"));
        var released = reservation with { Version = Guid.NewGuid(), State = StockReservationState.Released };
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectReservationAsync(reservation));
        await cut.Find("#stock-release-reservation").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-recovery").TextContent.Should().Contain("Release reservation");
        cut.Dispose();

        var reloaded = Panel(scope, [warehouse]);
        Writes<StockReservation>((_, _, _, _, _) => Task.FromResult(Failure<StockReservation>(409, "BUSINESS_VERSION_CONFLICT")));
        await reloaded.Find("#stock-retry-write").ClickAsync(new MouseEventArgs());
        ((StockController.VersionedCommand)_writes[1].Body).Version.Should().Be(reservation.Version, "the original body is replayed unchanged");
        reloaded.Find("#stock-write-error").TextContent.Should().Contain("version changed");
        reloaded.Find("#stock-recovery").TextContent.Should().Contain("outcome needs checking", "a replay 409 does not prove the first attempt failed");
        (await StoredIntent()).Confirmed.Should().BeFalse();

        Reads<StockReservation>(_ => Ok(released));
        await reloaded.Find("#stock-read-current").ClickAsync(new MouseEventArgs());
        _reads.Should().ContainSingle().Which.Should().Be(Root(scope) + $"/reservations/{reservation.Id:D}");
        reloaded.Find("#stock-current-comparison").TextContent.Should().Contain("Released");
        reloaded.Find("#stock-write-message").TextContent.Should().Contain("does not prove");
        reloaded.Find("#stock-recovery").Should().NotBeNull();
        reloaded.FindAll("#stock-reservation-detail").Should().BeEmpty("reading never replaces the selection by itself");

        reloaded.Find("#stock-acknowledge").Change(true);
        await reloaded.Find("#stock-clear-recovery").ClickAsync(new MouseEventArgs());
        reloaded.FindAll("#stock-recovery").Should().BeEmpty();
        _browser.Values.Should().BeEmpty();
        reloaded.Find("#stock-adopt-current").Click();
        reloaded.Find("#stock-reservation-detail").TextContent.Should().Contain("Released");
        reloaded.FindAll("#stock-release-reservation").Should().BeEmpty();
        _writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Stock_and_equipment_recovery_records_use_distinct_keys_for_the_same_user_and_scope()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.post", "equipment.write", "equipment.read");
        var warehouse = Warehouse(scope, "WH-1");
        var product = Product(scope, "P-001");
        Reads<ProductVariant>(_ => Ok(Variant(scope, product)));
        var stock = Panel(scope, [warehouse]);
        await stock.InvokeAsync(() => stock.Instance.SelectProductAsync(product));
        stock.Find("#stock-start-posting").Click();
        stock.Find("#stock-posting-kind").Change("Receipt");
        stock.Find("#stock-posting-to").Change(warehouse.Id.ToString("D"));
        stock.Find("#stock-posting-quantity").Change("1");
        stock.Find("#stock-posting-reference").Change("GRN-1");
        await stock.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);
        var stockKey = _browser.Values.Keys.Single();
        stockKey.Should().StartWith("nexaone_stock_write_v1_");

        Writes<SharedEquipment>((_, _, _, _, _) => Task.FromResult(Failure<SharedEquipment>(503)));
        var equipment = Render<EquipmentWorkflowPanel>(p => p.Add(c => c.Scope, scope).Add(c => c.UserId, "operator"));
        equipment.WaitForAssertion(() => equipment.Find("#inventory-equipment-workflows").GetAttribute("aria-busy").Should().Be("false"));
        equipment.FindAll("#equipment-recovery").Should().BeEmpty("a stock intent is never shown as an equipment intent");
        equipment.Find("#equipment-new").Click();
        equipment.Find("#equipment-code").Change("PRESS-1");
        equipment.Find("#equipment-name").Change("Press");
        equipment.Find("#equipment-capacity").Change("1");
        await equipment.Find("#equipment-asset-form").SubmitAsync(EventArgs.Empty);

        _browser.Values.Should().HaveCount(2);
        _browser.Values.Keys.Should().Contain(stockKey);
        _browser.Values.Keys.Where(key => key != stockKey).Should().ContainSingle().Which.Should().StartWith("nexaone_inventory_write_v1_");
        stock.Find("#stock-recovery").TextContent.Should().Contain("Post movement");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_is_isolated_by_both_user_and_business_identity(bool changeUser)
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.reverse");
        var warehouse = Warehouse(scope, "WH-1");
        var movement = Movement(scope, new StockPosting(Guid.NewGuid(), Guid.NewGuid(), StockMovementKind.Receipt, 1m, null, warehouse.Id, "GRN-1"));
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectMovementAsync(movement));
        cut.Find("#stock-reversal-reference").Change("RET-1");
        await cut.Find("#stock-reverse-movement").ClickAsync(new MouseEventArgs());
        cut.Find("#stock-recovery").Should().NotBeNull();
        cut.Dispose();

        var other = changeUser
            ? scope with { Membership = scope.Membership with { UserId = "someone-else" } }
            : scope with { Membership = scope.Membership with { BusinessUserId = Guid.NewGuid() } };
        var switched = Render<StockWorkflowPanel>(p => p.Add(c => c.Scope, other).Add(c => c.UserId, changeUser ? "someone-else" : "operator")
            .Add(c => c.Warehouses, (IReadOnlyList<Warehouse>)[warehouse]));
        switched.WaitForAssertion(() => switched.Find("#inventory-stock-workflows").GetAttribute("aria-busy").Should().Be("false"));

        switched.FindAll("#stock-recovery, #stock-retry-write").Should().BeEmpty();
        _browser.Values.Should().ContainSingle("the original owner's intent is retained, not deleted or replayed");
        var original = Panel(scope, [warehouse]);
        original.Find("#stock-recovery").TextContent.Should().Contain("Reverse movement");
        _writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Storage_failure_blocks_HTTP_and_double_submit_cannot_duplicate_a_posting()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.post");
        var warehouse = Warehouse(scope, "WH-1");
        var product = Product(scope, "P-001");
        Reads<ProductVariant>(_ => Ok(Variant(scope, product)));
        _browser.Before = (operation, _) => operation == "sessionStorage.setItem" ? throw new JSException("storage unavailable") : Task.CompletedTask;
        var cut = Panel(scope, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-start-posting").Click();
        cut.Find("#stock-posting-kind").Change("Receipt");
        cut.Find("#stock-posting-to").Change(warehouse.Id.ToString("D"));
        cut.Find("#stock-posting-quantity").Change("1");
        cut.Find("#stock-posting-reference").Change("GRN-1");

        await cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);

        _writes.Should().BeEmpty("no HTTP is sent without a durable intent");
        cut.Find("#stock-write-error").TextContent.Should().Contain("recovery record could not be read or saved");
        cut.Find("#stock-reload-recovery").Should().NotBeNull();

        _browser.Before = null;
        await cut.Find("#stock-reload-recovery").ClickAsync(new MouseEventArgs());
        var reply = Reply<StockMovement>();
        Writes<StockMovement>((_, _, _, _, _) => reply.Task);
        var first = cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => _writes.Should().ContainSingle());
        await cut.Find("#stock-posting-form").SubmitAsync(EventArgs.Empty);
        _writes.Should().ContainSingle("a second submit while the first is unresolved sends nothing");
        reply.SetResult(Ok(Movement(scope, (StockPosting)_writes[0].Body)));
        await first;
        cut.Find("#stock-movement-detail").Should().NotBeNull();
        _saved.Should().Be(1);
    }

    [Fact]
    public async Task Late_old_scope_success_cannot_clear_a_new_scope_command_or_publish_success()
    {
        var first = Scope("stock.read", "stock.warehouse.read", "stock.reserve");
        var second = ScopeAt(2, "stock.read", "stock.warehouse.read", "stock.reserve");
        var warehouse = Warehouse(first, "WH-1");
        var product = Product(first, "P-001");
        Reads<ProductVariant>(_ => Ok(Variant(first, product)));
        var reply = Reply<StockReservation>();
        Writes<StockReservation>((_, _, _, _, _) => reply.Task);
        var cut = Panel(first, [warehouse]);
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find("#stock-start-reservation").Click();
        cut.Find("#stock-reservation-warehouse").Change(warehouse.Id.ToString("D"));
        cut.Find("#stock-reservation-quantity").Change("1");
        cut.Find("#stock-reservation-reference").Change("WO-1");
        var sending = cut.Find("#stock-reservation-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => _writes.Should().ContainSingle());
        var firstKey = _browser.Values.Keys.Single();

        cut.Render(parameters => parameters.Add(p => p.Scope, second));
        cut.WaitForAssertion(() => cut.Find("#inventory-stock-workflows").GetAttribute("aria-busy").Should().Be("false"));
        cut.FindAll("#stock-recovery").Should().BeEmpty("the new scope has no intent of its own");
        reply.SetResult(Ok(Reservation(first, (StockController.ReservationCommand)_writes[0].Body)));
        await sending;

        cut.FindAll("#stock-reservation-detail, #stock-write-message").Should().BeEmpty("an old scope's success is not published into the new scope");
        _browser.Values.Keys.Should().Equal(firstKey);
        (await StoredIntent()).Confirmed.Should().BeFalse("only the original scope may reconcile its own intent");
        _saved.Should().Be(0);
    }

    [Theory]
    [InlineData("stock.post", "#stock-start-posting")]
    [InlineData("stock.reserve", "#stock-start-reservation")]
    public async Task Write_grants_without_read_grants_cannot_start_a_variant_action(string grant, string control)
    {
        var scope = Scope(grant, "stock.read");
        var product = Product(scope, "P-001");
        Reads<ProductVariant>(_ => Ok(Variant(scope, product)));
        var cut = Panel(scope, [Warehouse(scope, "WH-1")]);
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));
        cut.Find(control).HasAttribute("disabled").Should().BeTrue("warehouse read is required to choose a warehouse");
        cut.Find("#stock-missing-warehouse-read").Should().NotBeNull();

        var noRead = Panel(Scope(grant, "stock.warehouse.read"));
        await noRead.InvokeAsync(() => noRead.Instance.SelectProductAsync(product));
        noRead.FindAll("#stock-selected-variant").Should().BeEmpty("product read is required to resolve the variant");
        noRead.Find("#stock-missing-read").Should().NotBeNull();
        _reads.Should().ContainSingle();
    }

    [Fact]
    public async Task Balance_lookup_reads_the_selected_variant_and_warehouse_and_distinguishes_no_recorded_balance()
    {
        var scope = Scope("stock.read", "stock.warehouse.read");
        var warehouses = new[] { Warehouse(scope, "WH-A"), Warehouse(scope, "WH-B") };
        var product = Product(scope, "P-001");
        var variant = Variant(scope, product);
        Reads<ProductVariant>(_ => Ok(variant));
        var balance = new StockBalance(Guid.NewGuid(), Business(scope), Guid.NewGuid(), variant.Id, warehouses[0].Id, 12.5m, 2.5m);
        Reads<StockBalance>(path => path == Root(scope) + $"/balances?variantId={variant.Id:D}&warehouseId={warehouses[0].Id:D}" ? Ok(balance)
            : Failure<StockBalance>(404, "STOCK_BALANCE_NOT_FOUND"));
        var cut = Panel(scope, warehouses);
        cut.FindAll("#stock-balance-form").Should().BeEmpty("a balance needs a selected variant");
        await cut.InvokeAsync(() => cut.Instance.SelectProductAsync(product));

        cut.Find("#stock-balance-warehouse").Change(warehouses[0].Id.ToString("D"));
        await cut.Find("#stock-balance-form").SubmitAsync(EventArgs.Empty);

        cut.Find("#stock-balance").TextContent.Should().Contain("12.5").And.Contain("2.5").And.Contain("10");
        cut.Find("#stock-balance-warehouse").Change(warehouses[1].Id.ToString("D"));
        await cut.Find("#stock-balance-form").SubmitAsync(EventArgs.Empty);
        cut.Find("#stock-balance").TextContent.Should().Contain("No recorded balance");
        cut.FindAll("#stock-write-error").Should().BeEmpty("an absent balance is a valid answer, not an error");
        _reads.Should().HaveCount(3);
        _writes.Should().BeEmpty();

        var noRead = Panel(Scope("stock.post", "stock.warehouse.read"), warehouses);
        noRead.FindAll("#stock-balance-form").Should().BeEmpty();
    }

    [Fact]
    public async Task Variant_selection_by_ID_reads_the_stored_variant_and_disables_new_operations_when_inactive()
    {
        var scope = Scope("stock.read", "stock.warehouse.read", "stock.post", "stock.reserve");
        var warehouse = Warehouse(scope, "WH-1");
        var product = Product(scope, "P-001");
        var active = Variant(scope, product);
        var inactive = Variant(scope, Product(scope, "P-OLD")) with { Active = false };
        var foreign = Variant(ScopeAt(2, "stock.read"), product);
        var changes = new List<ProductVariant?>();
        Reads<ProductVariant>(path => path == Root(scope) + $"/variants/{active.Id:D}" ? Ok(active)
            : path == Root(scope) + $"/variants/{inactive.Id:D}" ? Ok(inactive)
            : path == Root(scope) + $"/variants/{foreign.Id:D}" ? Ok(foreign) : Failure<ProductVariant>(404, "VARIANT_NOT_FOUND"));
        var cut = Render<StockWorkflowPanel>(p => p.Add(c => c.Scope, scope).Add(c => c.UserId, "operator")
            .Add(c => c.Warehouses, (IReadOnlyList<Warehouse>)[warehouse]).Add(c => c.VariantChanged, v => { changes.Add(v); return Task.CompletedTask; }));
        cut.WaitForAssertion(() => cut.Find("#inventory-stock-workflows").GetAttribute("aria-busy").Should().Be("false"));

        await cut.InvokeAsync(() => cut.Instance.SelectVariantAsync(active.Id));

        cut.Find("#stock-selected-variant").TextContent.Should().Contain("P-001").And.Contain("EA");
        cut.Find("#stock-start-posting").HasAttribute("disabled").Should().BeFalse();
        changes.Should().ContainSingle().Which.Should().Be(active);

        await cut.InvokeAsync(() => cut.Instance.SelectVariantAsync(inactive.Id));
        cut.Find("#stock-selected-variant").TextContent.Should().Contain("P-OLD").And.Contain("Inactive");
        cut.Find("#stock-start-posting").HasAttribute("disabled").Should().BeTrue("inactive variants cannot start new operations");
        cut.Find("#stock-start-reservation").HasAttribute("disabled").Should().BeTrue();
        changes.Should().HaveCount(2);

        await cut.InvokeAsync(() => cut.Instance.SelectVariantAsync(foreign.Id));
        cut.Find("#stock-write-error").TextContent.Should().Contain("does not match");
        cut.Find("#stock-selected-variant").TextContent.Should().Contain("P-OLD", "a rejected read keeps the earlier selection");
        await cut.InvokeAsync(() => cut.Instance.SelectVariantAsync(Guid.NewGuid()));
        cut.Find("#stock-write-error").TextContent.Should().Contain("not found");
        changes.Should().HaveCount(2);
        _reads.Should().HaveCount(4);
        _writes.Should().BeEmpty();
    }

    private IRenderedComponent<StockWorkflowPanel> Panel(InventoryAccessScope scope, IReadOnlyList<Warehouse>? warehouses = null, Func<Task>? saved = null)
    {
        var cut = Render<StockWorkflowPanel>(p => p.Add(c => c.Scope, scope).Add(c => c.UserId, "operator")
            .Add(c => c.Warehouses, warehouses).Add(c => c.Saved, saved ?? (() => { ++_saved; return Task.CompletedTask; })));
        cut.WaitForAssertion(() => cut.Find("#inventory-stock-workflows").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    private void Writes<T>(Func<HttpMethod, string, object, string, CancellationToken, Task<(T?, int, string?, string?)>> respond) where T : class
        => _api.Setup(api => api.WriteInventoryAsync<T>(It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod method, string path, object body, string owner, CancellationToken ct) =>
            {
                _writes.Add((method, path, body, owner, ct));
                return respond(method, path, body, owner, ct);
            });

    private void Reads<T>(Func<string, (T?, int, string?, string?)> respond) where T : class
        => _api.Setup(api => api.ReadInventoryAsync<T>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => { _reads.Add(path); return Task.FromResult(respond(path)); });

    private async Task<StockWorkflowPanel.PendingWrite?> StoredIntentOrNull()
        => _browser.Values.Count == 0 ? null : await StoredIntent();

    private async Task<StockWorkflowPanel.PendingWrite> StoredIntent()
    {
        var stored = await _storage.GetAsync<StockWorkflowPanel.PendingWrite>(_browser.Values.Keys.Single());
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
    private static string Root(InventoryAccessScope scope) => $"api/v1/ivt/stock/{scope.Membership.TenantId:D}/{scope.Membership.OrganizationId:D}";
    private static Warehouse Warehouse(InventoryAccessScope scope, string code) => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), code, "Warehouse " + code);
    private static Product Product(InventoryAccessScope scope, string code) => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), new(code, "Product " + code, ""), true);
    private static ProductVariant Variant(InventoryAccessScope scope, Product product) => new(Guid.NewGuid(), Business(scope), product.Version, product.Id, product.Input.Code, "EA", Array.Empty<OptionSelection>());
    private static StockReservation Reservation(InventoryAccessScope scope, StockController.ReservationCommand command)
        => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), command.OperationId, command.VariantId, command.WarehouseId,
            command.Quantity, command.Reference, BusinessUser.ToString("D"), StockReservationState.Active);
    private static StockMovement Movement(InventoryAccessScope scope, StockPosting posting)
    {
        var deltas = posting.Kind switch
        {
            StockMovementKind.Receipt or StockMovementKind.Adjustment => new[] { new StockDelta(posting.ToWarehouseId!.Value, posting.Quantity) },
            StockMovementKind.Issue => new[] { new StockDelta(posting.FromWarehouseId!.Value, -posting.Quantity) },
            _ => new[] { new StockDelta(posting.FromWarehouseId!.Value, -posting.Quantity), new StockDelta(posting.ToWarehouseId!.Value, posting.Quantity) }
        };
        return new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), posting, BusinessUser.ToString("D"), deltas);
    }
}
