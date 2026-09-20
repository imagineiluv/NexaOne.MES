using System.Data.Common;
using System.Globalization;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Infrastructure;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class StockPersistenceTests : IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly StockBridge _bridge;
    private readonly EquipmentSharingBridge _equipment;
    private readonly BusinessMembershipBridge _memberships;
    private InventoryScopeBinding _binding = null!;
    private static readonly string[] Grants = ["stock.product.write", "stock.warehouse.read", "stock.warehouse.write", "stock.read",
        "stock.post", "stock.reverse", "stock.reserve", "stock.consume", "stock.release"];

    public StockPersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _bridge = NewBridge(); _memberships = new(DataSource());
        _equipment = new(DataSource(), _memberships, new BusinessMasterDirectory(DataSource()));
        Execute("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES ('STOCK-P1','Stock plant'), ('STOCK-P2','Other plant');
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT)
                VALUES ('STOCK-PRODUCT','재고 상품','Finished','EA'), ('STOCK-OTHER','Other product','Finished','KG');
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('STOCK-MEMBER','Stock member','', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('stock-user','Operator','','','STOCK-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('stock-other','Other operator','','','STOCK-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }
    public async Task InitializeAsync()
    {
        _binding = await _equipment.BindScopeAsync("admin", _tenant, _organization, "STOCK-P1", null, true);
        foreach (var user in new[] { "stock-user", "stock-other" })
            (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, user, new(0, true, Grants))).IsSuccess.Should().BeTrue();
    }
    public Task DisposeAsync() { File.Delete(_path); return Task.CompletedTask; }
    private EesDataSource DataSource() => new() { Provider = new SqliteProvider(), ConnectionString = _connectionString };
    private StockBridge NewBridge() => new(DataSource(), new BusinessMembershipBridge(DataSource()), new BusinessMasterDirectory(DataSource()));
    private void Execute(string sql, object? values = null) { using var c = new SqliteConnection(_connectionString); c.Open(); c.Execute(sql, values); }
    private T Scalar<T>(string sql) { using var c = new SqliteConnection(_connectionString); c.Open(); return c.ExecuteScalar<T>(sql)!; }
    private long Count(string table) => Scalar<long>("SELECT COUNT(*) FROM " + table);
    private Task<ProductVariant> Product() => _bridge.EnrollProductAsync("stock-user", _tenant, _organization, "STOCK-PRODUCT", "EA");
    private Task<Warehouse> Warehouse(string code = "MAIN") => _bridge.CreateWarehouseAsync("stock-user", _tenant, _organization, Guid.NewGuid(), code, code);
    private Task<StockMovement> Post(ProductVariant product, Warehouse warehouse, decimal quantity, StockMovementKind kind = StockMovementKind.Receipt, Guid? operation = null)
        => _bridge.PostAsync("stock-user", _tenant, _organization, new(operation ?? Guid.NewGuid(), product.Id, kind, quantity,
            kind == StockMovementKind.Issue ? warehouse.Id : null, kind == StockMovementKind.Issue ? null : warehouse.Id, "reference"));
    private Task<StockReservation> Reserve(ProductVariant product, Warehouse warehouse, decimal quantity, Guid? operation = null)
        => _bridge.ReserveAsync("stock-user", _tenant, _organization, operation ?? Guid.NewGuid(), product.Id, warehouse.Id, quantity, "reserved");
    private Task<StockBalance?> Balance(ProductVariant product, Warehouse warehouse)
        => NewBridge().GetBalanceAsync("stock-user", _tenant, _organization, product.Id, warehouse.Id);
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);

    [Fact]
    public async Task Receipt_transfer_reservation_consume_reverse_and_release_survive_reconstruction()
    {
        var product = await Product(); var from = await Warehouse(); var to = await Warehouse("SECOND");
        var receipt = await Post(product, from, 100.123456m);
        var transfer = await _bridge.PostAsync("stock-user", _tenant, _organization,
            new(Guid.NewGuid(), product.Id, StockMovementKind.Transfer, 25.123456m, from.Id, to.Id, "transfer"));
        var reserve = await Reserve(product, to, 10.000001m);
        (await Balance(product, to))!.Available.Should().Be(15.123455m);
        var consumed = await NewBridge().ConsumeReservationAsync("stock-user", _tenant, _organization, reserve.Id, reserve.Version);
        (await Balance(product, to))!.OnHand.Should().Be(15.123455m);
        var reversed = await NewBridge().ReverseAsync("stock-user", _tenant, _organization, consumed.Id, consumed.Version, Guid.NewGuid(), "undo");
        var replay = await NewBridge().ConsumeReservationAsync("stock-user", _tenant, _organization, reserve.Id, reserve.Version);
        replay.Id.Should().Be(consumed.Id); replay.ReversedById.Should().Be(reversed.Id);
        (await Balance(product, to))!.OnHand.Should().Be(25.123456m);
        (await Balance(product, to))!.Reserved.Should().Be(0m);
        var release = await Reserve(product, from, 4m);
        var released = await NewBridge().ReleaseReservationAsync("stock-user", _tenant, _organization, release.Id, release.Version);
        released.State.Should().Be(StockReservationState.Released);
        (await Balance(product, from))!.OnHand.Should().Be(75m);
        (await Balance(product, from))!.Reserved.Should().Be(0m);
        (await NewBridge().GetMovementAsync("stock-user", _tenant, _organization, receipt.Id)).Deltas.Should().Equal(receipt.Deltas);
        (await NewBridge().GetMovementAsync("stock-user", _tenant, _organization, transfer.Id)).Deltas.Should().Equal(transfer.Deltas);
        Count("IVT_STOCK_MOVEMENT").Should().Be(4);
        Count("IVT_STOCK_AUDIT").Should().Be(11); // enrollment + 2 warehouses + 4 ledger + 4 reservation transitions
    }

    [Fact]
    public async Task Warehouse_creation_recovery_uses_original_actor_and_payload_after_rename()
    {
        var operation = Guid.NewGuid();
        var value = await _bridge.CreateWarehouseAsync("stock-user", _tenant, _organization, operation, "ORIGINAL", "Original name");
        var renamed = await _bridge.UpdateWarehouseAsync("stock-user", _tenant, _organization, value.Id, value.Version, "RENAMED", "Changed");
        Execute("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='stock.warehouse.write' WHERE USER_ID='stock-user'");
        (await NewBridge().CreateWarehouseAsync("stock-user", _tenant, _organization, operation, "ORIGINAL", "Original name")).Should().Be(renamed);
        await Error(() => _bridge.CreateWarehouseAsync("stock-user", _tenant, _organization, operation, "RENAMED", "Changed"), "WAREHOUSE_CREATE_OPERATION_CONFLICT");
        await Error(() => _bridge.CreateWarehouseAsync("stock-other", _tenant, _organization, operation, "ORIGINAL", "Original name"), "WAREHOUSE_CREATE_OPERATION_CONFLICT");
        await Error(() => Warehouse("RENAMED"), "WAREHOUSE_CODE_CONFLICT");
        Count("IVT_STOCK_WAREHOUSE").Should().Be(1); Count("IVT_STOCK_WAREHOUSE_CREATION").Should().Be(1);
        Count("IVT_STOCK_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Receipt_insertion_failure_rolls_back_warehouse_and_audit()
    {
        Execute("CREATE TRIGGER receipt_failure BEFORE INSERT ON IVT_STOCK_WAREHOUSE_CREATION BEGIN SELECT RAISE(ABORT, 'receipt unavailable'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Warehouse());
        Count("IVT_STOCK_WAREHOUSE").Should().Be(0); Count("IVT_STOCK_AUDIT").Should().Be(0);
    }

    [Fact]
    public async Task Enrollment_is_explicit_stable_and_unit_checked()
    {
        await Error(() => _bridge.GetProductAsync("stock-user", _tenant, _organization, "STOCK-PRODUCT"), "PRODUCT_NOT_ENROLLED");
        await Error(() => _bridge.EnrollProductAsync("stock-user", _tenant, _organization, "missing", "EA"), "PRODUCT_NOT_FOUND");
        await Error(() => _bridge.EnrollProductAsync("stock-user", _tenant, _organization, "STOCK-PRODUCT", "KG"), "PRODUCT_UNIT_CHANGED");
        var product = await Product();
        (await NewBridge().EnrollProductAsync("stock-user", _tenant, _organization, "STOCK-PRODUCT", "EA")).Should().BeEquivalentTo(product);
        (await NewBridge().GetProductAsync("stock-user", _tenant, _organization, "STOCK-PRODUCT")).Id.Should().Be(product.Id);
        Count("IVT_STOCK_PRODUCT").Should().Be(1); Count("IVT_STOCK_AUDIT").Should().Be(1);
        Execute("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='stock.warehouse.write' WHERE USER_ID='stock-user'");
        await Error(() => Product(), "BUSINESS_ACCESS_DENIED");
    }

    [Fact]
    public async Task Enrollment_audit_failure_does_not_leave_an_identity_mapping()
    {
        Execute("CREATE TRIGGER audit_failure BEFORE INSERT ON IVT_STOCK_AUDIT BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Product());
        Count("IVT_STOCK_PRODUCT").Should().Be(0);
    }

    [Theory]
    [InlineData("UNIT='KG'")]
    [InlineData("VALID_STATE='Invalid'")]
    public async Task Changed_master_blocks_new_stock_but_preserves_history_replay_and_release(string change)
    {
        var product = await Product(); var warehouse = await Warehouse(); var operation = Guid.NewGuid();
        var movement = await Post(product, warehouse, 10m, operation: operation); var reservation = await Reserve(product, warehouse, 2m);
        Execute("UPDATE MDM_PRODUCT SET " + change + " WHERE PRODUCT_ID='STOCK-PRODUCT'");
        (await _bridge.GetProductAsync("stock-user", _tenant, _organization, "STOCK-PRODUCT")).Active.Should().BeFalse();
        await Error(() => Post(product, warehouse, 1m), "VARIANT_INACTIVE");
        await Error(() => _bridge.ConsumeReservationAsync("stock-user", _tenant, _organization, reservation.Id, reservation.Version), "VARIANT_INACTIVE");
        (await Post(product, warehouse, 10m, operation: operation)).Id.Should().Be(movement.Id);
        (await _bridge.ReleaseReservationAsync("stock-user", _tenant, _organization, reservation.Id, reservation.Version)).State.Should().Be(StockReservationState.Released);
        (await Balance(product, warehouse))!.OnHand.Should().Be(10m);
    }

    [Theory]
    [InlineData("UPDATE SYS_BUSINESS_MEMBERSHIP SET IS_ACTIVE=0 WHERE USER_ID='stock-user'")]
    [InlineData("UPDATE SYS_USER SET IS_ACTIVE=0 WHERE USER_ID='stock-user'")]
    [InlineData("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='stock.read' WHERE USER_ID='stock-user'")]
    [InlineData("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='stock.post|*' WHERE USER_ID='stock-user'")]
    public async Task Every_request_rechecks_current_owner_authority(string revoke)
    {
        var product = await Product(); var warehouse = await Warehouse();
        var operation = Guid.NewGuid(); await Post(product, warehouse, 1m, operation: operation);
        Execute(revoke);
        await Error(() => Post(product, warehouse, 1m, operation: operation), "BUSINESS_ACCESS_DENIED");
        Count("IVT_STOCK_MOVEMENT").Should().Be(1);
    }

    [Fact]
    public async Task Scopes_keep_distinct_product_ids_and_cannot_read_or_move_each_others_stock()
    {
        var product = await Product(); var warehouse = await Warehouse(); await Post(product, warehouse, 5m);
        var other = Guid.NewGuid();
        await _equipment.BindScopeAsync("admin", _tenant, other, "STOCK-P2", null, true);
        (await _memberships.SaveMembershipAsync("admin", _tenant, other, "stock-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        var otherProduct = await _bridge.EnrollProductAsync("stock-user", _tenant, other, "STOCK-PRODUCT", "EA");
        otherProduct.Id.Should().NotBe(product.Id);
        (await _bridge.GetBalanceAsync("stock-user", _tenant, other, product.Id, warehouse.Id)).Should().BeNull();
        await Error(() => _bridge.PostAsync("stock-user", _tenant, other,
            new(Guid.NewGuid(), product.Id, StockMovementKind.Receipt, 1m, null, warehouse.Id, "cross scope")), "VARIANT_NOT_FOUND");
        await Error(() => _bridge.PostAsync("stock-user", _tenant, other,
            new(Guid.NewGuid(), otherProduct.Id, StockMovementKind.Receipt, 1m, null, warehouse.Id, "cross warehouse")), "WAREHOUSE_NOT_FOUND");
    }

    [Fact]
    public async Task Scope_and_warehouse_cannot_deactivate_with_stock_or_reservations()
    {
        var product = await Product(); var warehouse = await Warehouse(); await Post(product, warehouse, 5m);
        var reserved = await Reserve(product, warehouse, 5m);
        await Error(() => _equipment.BindScopeAsync("admin", _tenant, _organization, "STOCK-P1", _binding.Version, false), "SCOPE_HAS_STOCK_OR_RESERVATIONS");
        await Error(() => _bridge.SetWarehouseActiveAsync("stock-user", _tenant, _organization, warehouse.Id, warehouse.Version, false), "WAREHOUSE_IS_REFERENCED");
        await _bridge.ConsumeReservationAsync("stock-user", _tenant, _organization, reserved.Id, reserved.Version);
        await _bridge.SetWarehouseActiveAsync("stock-user", _tenant, _organization, warehouse.Id, warehouse.Version, false);
        await _equipment.BindScopeAsync("admin", _tenant, _organization, "STOCK-P1", _binding.Version, false);
        await Error(() => Balance(product, warehouse), "BUSINESS_ACCESS_DENIED");
    }

    [Fact]
    public async Task Audit_failure_rolls_back_stock_balance_and_ledger()
    {
        var product = await Product(); var warehouse = await Warehouse();
        Execute("CREATE TRIGGER audit_failure BEFORE INSERT ON IVT_STOCK_AUDIT BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Post(product, warehouse, 5m));
        Count("IVT_STOCK_BALANCE").Should().Be(0); Count("IVT_STOCK_MOVEMENT").Should().Be(0); Count("IVT_STOCK_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Later_transfer_write_failure_rolls_back_the_first_warehouse_write()
    {
        var product = await Product(); var first = await Warehouse(); var second = await Warehouse("SECOND");
        var sorted = new[] { first, second }.OrderBy(w => w.Id).ToArray();
        await Post(product, sorted[0], 5m); await Post(product, sorted[1], 5m);
        Execute($"CREATE TRIGGER balance_failure BEFORE UPDATE ON IVT_STOCK_BALANCE WHEN OLD.WAREHOUSE_ID='{sorted[1].Id:D}' BEGIN SELECT RAISE(ABORT, 'second write failed'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => _bridge.PostAsync("stock-user", _tenant, _organization,
            new(Guid.NewGuid(), product.Id, StockMovementKind.Transfer, 2m, sorted[0].Id, sorted[1].Id, "transfer")));
        (await Balance(product, first))!.OnHand.Should().Be(5m); (await Balance(product, second))!.OnHand.Should().Be(5m);
        Count("IVT_STOCK_MOVEMENT").Should().Be(2); Count("IVT_STOCK_AUDIT").Should().Be(5);
    }

    [Fact]
    public async Task Consume_failure_after_ledger_audit_rolls_back_every_effect()
    {
        var product = await Product(); var warehouse = await Warehouse(); await Post(product, warehouse, 5m);
        var reservation = await Reserve(product, warehouse, 3m);
        Execute("CREATE TRIGGER consume_failure BEFORE INSERT ON IVT_STOCK_AUDIT WHEN NEW.OPERATION='consumed' BEGIN SELECT RAISE(ABORT, 'reservation audit failed'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => _bridge.ConsumeReservationAsync("stock-user", _tenant, _organization, reservation.Id, reservation.Version));
        var balance = (await Balance(product, warehouse))!; balance.OnHand.Should().Be(5m); balance.Reserved.Should().Be(3m);
        (await _bridge.GetReservationAsync("stock-user", _tenant, _organization, reservation.Id)).State.Should().Be(StockReservationState.Active);
        Count("IVT_STOCK_MOVEMENT").Should().Be(1); Count("IVT_STOCK_AUDIT").Should().Be(4);
    }

    [Fact]
    public async Task Decimal_storage_preserves_large_accumulations_and_micro_quantities_without_float()
    {
        var product = await Product(); var warehouse = await Warehouse(); await Post(product, warehouse, 1m);
        Execute("UPDATE IVT_STOCK_BALANCE SET ON_HAND='9007199254740993.000001'");
        await Post(product, warehouse, 0.000001m);
        (await Balance(product, warehouse))!.OnHand.Should().Be(9007199254740993.000002m);
        Scalar<string>("SELECT ON_HAND FROM IVT_STOCK_BALANCE").Should().Be("9007199254740993.000002");
        Scalar<string>("SELECT typeof(ON_HAND) FROM IVT_STOCK_BALANCE").Should().Be("text");
        Execute("UPDATE IVT_STOCK_BALANCE SET ON_HAND='10000000000000000000000000000'");
        await Error(() => Post(product, warehouse, 0.000001m), "STOCK_BALANCE_OVERFLOW");
        Count("IVT_STOCK_MOVEMENT").Should().Be(2);
    }

    [Fact]
    public async Task Available_precision_loss_rejects_reservation_atomically_and_corrupt_persisted_balance()
    {
        var product = await Product();
        var warehouse = await Warehouse();
        await Post(product, warehouse, 1m);
        Execute("UPDATE IVT_STOCK_BALANCE SET ON_HAND='10000000000000000000000000000', RESERVED='0'");
        var before = (await Balance(product, warehouse))!;
        before.OnHand.Should().Be(10000000000000000000000000000m);
        before.Reserved.Should().Be(0m);
        before.Available.Should().Be(10000000000000000000000000000m);
        var auditBefore = Count("IVT_STOCK_AUDIT");
        var movementsBefore = Count("IVT_STOCK_MOVEMENT");

        await Error(() => Reserve(product, warehouse, 0.000001m), "STOCK_BALANCE_OVERFLOW");

        var afterRejected = (await Balance(product, warehouse))!;
        afterRejected.Should().Be(before);
        afterRejected.Version.Should().Be(before.Version);
        Count("IVT_STOCK_RESERVATION").Should().Be(0);
        Count("IVT_STOCK_AUDIT").Should().Be(auditBefore);
        Count("IVT_STOCK_MOVEMENT").Should().Be(movementsBefore);

        var reservation = await Reserve(product, warehouse, 1m);
        var reserved = (await Balance(product, warehouse))!;
        reserved.OnHand.Should().Be(before.OnHand);
        reserved.Reserved.Should().Be(1m);
        reserved.Available.Should().Be(9999999999999999999999999999m);
        var released = await NewBridge().ReleaseReservationAsync(
            "stock-user", _tenant, _organization, reservation.Id, reservation.Version);
        released.State.Should().Be(StockReservationState.Released);
        var restored = (await Balance(product, warehouse))!;
        restored.OnHand.Should().Be(before.OnHand);
        restored.Reserved.Should().Be(0m);
        restored.Available.Should().Be(before.OnHand);
        Count("IVT_STOCK_RESERVATION").Should().Be(1);
        Count("IVT_STOCK_AUDIT").Should().Be(auditBefore + 2);
        Count("IVT_STOCK_MOVEMENT").Should().Be(movementsBefore);

        Execute("UPDATE IVT_STOCK_BALANCE SET RESERVED='0.000001'");
        await Error(() => Balance(product, warehouse), "STORAGE_CONTRACT_VIOLATION");
    }

    [Fact]
    public async Task Stale_warehouse_versions_conflict_without_changing_name()
    {
        var warehouse = await Warehouse();
        var next = await _bridge.UpdateWarehouseAsync("stock-user", _tenant, _organization, warehouse.Id, warehouse.Version, "NEXT", "Next");
        await Error(() => _bridge.UpdateWarehouseAsync("stock-user", _tenant, _organization, warehouse.Id, warehouse.Version, "STALE", "Stale"), "BUSINESS_VERSION_CONFLICT");
        (await _bridge.GetWarehouseAsync("stock-user", _tenant, _organization, warehouse.Id)).Should().Be(next);
        Count("IVT_STOCK_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Replays_do_not_duplicate_effects_and_operation_ids_cannot_cross_post_and_reservation()
    {
        var product = await Product(); var warehouse = await Warehouse(); var operation = Guid.NewGuid();
        var first = await Post(product, warehouse, 10m, operation: operation);
        (await Post(product, warehouse, 10m, operation: operation)).Id.Should().Be(first.Id);
        await Error(() => Post(product, warehouse, 11m, operation: operation), "STOCK_OPERATION_CONFLICT");
        await Error(() => Reserve(product, warehouse, 1m, operation), "STOCK_OPERATION_CONFLICT");
        var reserved = await Reserve(product, warehouse, 2m);
        await Error(() => Post(product, warehouse, 2m, operation: reserved.OperationId), "STOCK_OPERATION_RESERVED");
        await Error(() => Post(product, warehouse, 9m, StockMovementKind.Issue), "INSUFFICIENT_AVAILABLE_STOCK");
        (await Balance(product, warehouse))!.OnHand.Should().Be(10m);
        Count("IVT_STOCK_MOVEMENT").Should().Be(1); Count("IVT_STOCK_AUDIT").Should().Be(4);
    }

    [Fact]
    public async Task Independent_transactions_cannot_oversell_available_stock()
    {
        var product = await Product(); var warehouse = await Warehouse(); await Post(product, warehouse, 1m);
        using var start = new Barrier(2);
        async Task<bool> Attempt() => await Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            try
            {
                await NewBridge().PostAsync("stock-user", _tenant, _organization,
                    new(Guid.NewGuid(), product.Id, StockMovementKind.Issue, 1m, warehouse.Id, null, "race"));
                return true;
            }
            catch (BusinessException error) when (error.Code == "INSUFFICIENT_AVAILABLE_STOCK") { return false; }
            catch (DbException) { return false; } // Provider may reject a competing Serializable transaction.
        });
        var results = await Task.WhenAll(Attempt(), Attempt());
        results.Count(success => success).Should().Be(1);
        (await Balance(product, warehouse))!.OnHand.Should().Be(0m); Count("IVT_STOCK_MOVEMENT").Should().Be(2);
    }

    [Fact]
    public async Task Ledger_pages_have_consistent_scoped_total_and_stable_nonoverlapping_entries()
    {
        var product = await Product(); var warehouse = await Warehouse();
        for (var i = 0; i < 4; i++) await Post(product, warehouse, 1m);
        var other = await _bridge.EnrollProductAsync("stock-user", _tenant, _organization, "STOCK-OTHER", "KG"); await Post(other, warehouse, 1m);
        var page1 = await _bridge.ListMovementsAsync("stock-user", _tenant, _organization, product.Id, 0, 2);
        var page2 = await NewBridge().ListMovementsAsync("stock-user", _tenant, _organization, product.Id, 2, 2);
        page1.Total.Should().Be(4); page2.Total.Should().Be(4);
        page1.Items.Concat(page2.Items).Select(x => x.Id).Distinct().Should().HaveCount(4);
        page1.Items.Concat(page2.Items).Should().OnlyContain(x => x.Posting.VariantId == product.Id);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0000001")]
    [InlineData("1000000000001")]
    public async Task Invalid_quantities_leave_no_stock_rows(string text)
    {
        var product = await Product(); var warehouse = await Warehouse();
        await Error(() => Post(product, warehouse, decimal.Parse(text, CultureInfo.InvariantCulture)), "INVALID_STOCK_QUANTITY");
        Count("IVT_STOCK_BALANCE").Should().Be(0); Count("IVT_STOCK_MOVEMENT").Should().Be(0);
    }
}
