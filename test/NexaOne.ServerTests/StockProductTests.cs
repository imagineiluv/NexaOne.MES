using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.IVT.Infrastructure;
using NexaOne.MDM.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Collection(ChildProcessSmokeCollection.Name)]
[Trait("Category", "HostSmoke")]
public sealed class StockHostTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions HttpJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Real_host_resolves_stock_contract_and_preserves_explicit_enrollment_identity_decimal_and_replay()
    {
        using var host = await HostProcess.StartAsync(output, springConfig: null, expectListening: true);
        host.Listening.Should().BeTrue(host.Log);
        var seed = new StockProductSeed();
        await using var database = new SqliteConnection(
            $"Data Source={host.DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=10");
        await database.OpenAsync();
        await database.ExecuteAsync(StockProductSeed.Sql, seed);
        using var admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var otherOrganization = Guid.NewGuid();
        var route = $"/api/v1/ivt/stock/{tenant}/{organization}";
        var productRoute = route + "/products/" + seed.Product;
        await Status(await admin.GetAsync(productRoute), HttpStatusCode.Unauthorized);
        await Login(admin, "admin", "admin", seed.OtherPlant);
        var diagnostics = await Body<JsonElement>(await admin.GetAsync("/diag"));
        var descriptor = diagnostics.GetProperty("bridges").EnumerateArray().Single(item =>
            item.GetProperty("contract").GetString() == typeof(IStockBridge).FullName);
        descriptor.GetProperty("module").GetString().Should().Be("Ivt");
        descriptor.GetProperty("beanName").GetString().Should().Be("stockBridge");
        descriptor.GetProperty("implementation").GetString().Should().Be(typeof(StockBridge).FullName);
        // This is the existing shared IVT binding; stock introduces no second binding authority.
        var binding = await Body<InventoryScopeBinding>(await admin.PutAsJsonAsync(
            $"/api/v1/ivt/shared-equipment/{tenant}/{organization}/binding", new { plantId = seed.Plant, active = true }));
        await Body<InventoryScopeBinding>(await admin.PutAsJsonAsync(
            $"/api/v1/ivt/shared-equipment/{tenant}/{otherOrganization}/binding", new { plantId = seed.OtherPlant, active = true }));
        await Status(await admin.PutAsJsonAsync(productRoute, new StockController.ProductEnrollment("kg")), HttpStatusCode.Forbidden);
        var membershipRoute = $"/api/v1/sys/business-memberships/{tenant}/{organization}/users/{seed.User}";
        var membership = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute,
            new BusinessMembershipChange(0, true, StockProductSeed.Grants.Where(value => value != "stock.product.write").ToArray())));
        await Body<BusinessMembership>(await admin.PutAsJsonAsync(
            $"/api/v1/sys/business-memberships/{tenant}/{otherOrganization}/users/{seed.User}",
            new BusinessMembershipChange(0, true, ["stock.read", "stock.warehouse.read"])));
        using var member = new HttpClient { BaseAddress = admin.BaseAddress };
        await Login(member, seed.User, StockProductSeed.Password, seed.OtherPlant);
        await Status(await member.PutAsJsonAsync(productRoute, new StockController.ProductEnrollment("kg")), HttpStatusCode.Forbidden);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM IVT_STOCK_PRODUCT")).Should().Be(0);
        membership = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute,
            new BusinessMembershipChange(membership.Version, true, StockProductSeed.Grants)));
        var variant = await Body<ProductVariant>(await member.PutAsJsonAsync(productRoute, new StockController.ProductEnrollment("kg")));
        variant.Scope.Should().Be(new BusinessScope("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")));
        variant.Unit.Should().Be("kg");
        variant.ProductId.Should().NotBeEmpty();
        (await Body<ProductVariant>(await member.PutAsJsonAsync(productRoute, new StockController.ProductEnrollment("kg"))))
            .Should().BeEquivalentTo(variant);
        (await Body<ProductVariant>(await member.GetAsync(productRoute))).Should().BeEquivalentTo(variant);
        var create = new StockController.WarehouseCreate(Guid.NewGuid(), "HTTP-STOCK", "Original warehouse");
        var warehouse = await Body<Warehouse>(await member.PostAsJsonAsync(route + "/warehouses", create));
        warehouse = await Body<Warehouse>(await member.PutAsJsonAsync(route + "/warehouses/" + warehouse.Id,
            new StockController.WarehouseChange(warehouse.Version, "RENAMED", "Renamed warehouse")));
        (await Body<Warehouse>(await member.PostAsJsonAsync(route + "/warehouses", create))).Should().Be(warehouse);
        await Error(await member.GetAsync($"/api/v1/ivt/stock/{tenant}/{otherOrganization}/warehouses/{warehouse.Id}"),
            HttpStatusCode.NotFound, "WAREHOUSE_NOT_FOUND");
        var posting = new StockPosting(Guid.NewGuid(), variant.Id, StockMovementKind.Receipt,
            StockProductSeed.PreciseQuantity, null, warehouse.Id, "HTTP receipt");
        var payload = JsonSerializer.SerializeToNode(posting, HttpJson)!.AsObject();
        payload["createdBy"] = "forged-user";
        payload["tenantId"] = Guid.NewGuid().ToString("D");
        payload["organizationId"] = otherOrganization.ToString("D");
        var receipt = await Body<StockMovement>(await member.PostAsJsonAsync(route + "/movements", payload));
        receipt.Posting.Should().Be(posting);
        receipt.CreatedBy.Should().Be(membership.BusinessUserId.ToString("D"));
        receipt.Scope.Should().Be(variant.Scope);
        (await Body<StockMovement>(await member.PostAsJsonAsync(route + "/movements", posting))).Should().BeEquivalentTo(receipt);
        await Error(await member.PutAsJsonAsync(route + $"/warehouses/{warehouse.Id}/active",
            new StockController.ActiveChange(warehouse.Version, false)), HttpStatusCode.Conflict, "WAREHOUSE_IS_REFERENCED");
        await Error(await admin.PutAsJsonAsync($"/api/v1/ivt/shared-equipment/{tenant}/{organization}/binding",
            new { plantId = seed.Plant, expectedVersion = binding.Version, active = false }),
            HttpStatusCode.Conflict, "SCOPE_HAS_STOCK_OR_RESERVATIONS");
        await Error(await member.PostAsJsonAsync(route + "/movements", posting with { Quantity = 1m }),
            HttpStatusCode.Conflict, "STOCK_OPERATION_CONFLICT");
        var reserve = new StockController.ReservationCommand(Guid.NewGuid(), variant.Id, warehouse.Id, 0.123456m, "HTTP reserve");
        var reservation = await Body<StockReservation>(await member.PostAsJsonAsync(route + "/reservations", reserve));
        reservation.CreatedBy.Should().Be(membership.BusinessUserId.ToString("D"));
        var consume = new StockController.VersionedCommand(reservation.Version);
        var issue = await Body<StockMovement>(await member.PostAsJsonAsync(route + $"/reservations/{reservation.Id}/consume", consume));
        (await Body<StockMovement>(await member.PostAsJsonAsync(route + $"/reservations/{reservation.Id}/consume", consume)))
            .Should().BeEquivalentTo(issue);
        (await Body<StockReservation>(await member.GetAsync(route + "/reservations/" + reservation.Id)))
            .State.Should().Be(StockReservationState.Consumed);
        var balance = await Body<StockBalance>(await member.GetAsync(route + $"/balances?variantId={variant.Id}&warehouseId={warehouse.Id}"));
        balance.OnHand.Should().Be(999999999999m);
        balance.Reserved.Should().Be(0m);
        var movements = await Body<BusinessPage<StockMovement>>(await member.GetAsync(route + $"/movements?variantId={variant.Id}&offset=0&limit=1"));
        movements.Total.Should().Be(2);
        movements.Items.Should().ContainSingle();
        (await Body<StockMovement>(await member.GetAsync(route + "/movements/" + receipt.Id))).Should().BeEquivalentTo(receipt);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM IVT_STOCK_MOVEMENT")).Should().Be(2);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM IVT_STOCK_PRODUCT")).Should().Be(1);
        (await database.ExecuteScalarAsync<string>("SELECT typeof(ON_HAND) FROM IVT_STOCK_BALANCE")).Should().Be("text");
        (await database.ExecuteScalarAsync<string>("SELECT ON_HAND FROM IVT_STOCK_BALANCE")).Should().Be("999999999999");
        await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute, new BusinessMembershipChange(membership.Version, false, [])));
        await Status(await member.PostAsJsonAsync(route + "/movements", posting), HttpStatusCode.Forbidden);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM IVT_STOCK_MOVEMENT")).Should().Be(2);
    }

    private static async Task Login(HttpClient client, string userId, string password, string plantId)
    {
        var login = await Body<JsonElement>(await client.PostAsJsonAsync("/api/v1/auth/login", new { userId, password, plantId }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
    }

    private static async Task<T> Body<T>(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>(HttpJson))!;
        }
    }

    private static async Task Status(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response) response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        using (response)
        {
            response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(code);
        }
    }
}

// Match the shared SQL gate, but report unavailable local SQL as an actual skip rather than a passing early return.
public sealed class StockMssqlFactAttribute : FactAttribute
{
    public StockMssqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MssqlContractDatabase.ConnectionEnvironmentVariable))
            && !string.Equals(Environment.GetEnvironmentVariable(MssqlContractDatabase.RequiredEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase))
            Skip = $"Requires {MssqlContractDatabase.ConnectionEnvironmentVariable}; no SQL Server acceptance was executed.";
    }
}

[Trait("Category", "MssqlContract")]
public sealed class StockMssqlTests(ITestOutputHelper output)
{
    [StockMssqlFact]
    public async Task Actual_SQL_Server_migrations_and_warehouse_creation_receipts_preserve_identity_after_rename()
    {
        var h = await Harness.CreateAsync(output);
        string[] tables = ["IVT_STOCK_PRODUCT", "IVT_STOCK_WAREHOUSE", "IVT_STOCK_WAREHOUSE_CREATION",
            "IVT_STOCK_BALANCE", "IVT_STOCK_MOVEMENT", "IVT_STOCK_RESERVATION", "IVT_STOCK_AUDIT"];
        (await h.Database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('dbo') AND name IN @tables", new { tables }))
            .Should().Be(7);
        (await h.Database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON c.object_id=t.object_id
             WHERE t.schema_id=SCHEMA_ID('dbo') AND TYPE_NAME(c.user_type_id) IN ('varchar','nvarchar')
               AND ((t.name='IVT_STOCK_BALANCE' AND c.name IN ('ON_HAND','RESERVED'))
                 OR (t.name IN ('IVT_STOCK_MOVEMENT','IVT_STOCK_RESERVATION') AND c.name='QUANTITY'))
            """)).Should().Be(4, "durable quantities must use decimal text, including on SQL Server");
        var operation = Guid.NewGuid();
        var warehouse = await h.Bridge.CreateWarehouseAsync(h.Seed.User, h.Tenant, h.Organization, operation, "ORIGINAL", "Original");
        var renamed = await h.Bridge.UpdateWarehouseAsync(h.Seed.User, h.Tenant, h.Organization, warehouse.Id, warehouse.Version, "RENAMED", "Renamed");
        (await h.Bridge.CreateWarehouseAsync(h.Seed.User, h.Tenant, h.Organization, operation, "ORIGINAL", "Original")).Should().Be(renamed);
        await Error(() => h.Bridge.CreateWarehouseAsync(h.Seed.User, h.Tenant, h.Organization, operation, "ORIGINAL", "Changed"), "WAREHOUSE_CREATE_OPERATION_CONFLICT");
        await Error(() => h.Bridge.CreateWarehouseAsync(h.Seed.OtherUser, h.Tenant, h.Organization, operation, "ORIGINAL", "Original"), "WAREHOUSE_CREATE_OPERATION_CONFLICT");
        (await h.Count("IVT_STOCK_WAREHOUSE")).Should().Be(1);
        (await h.Count("IVT_STOCK_WAREHOUSE_CREATION")).Should().Be(1);
        (await h.Count("IVT_STOCK_AUDIT")).Should().Be(3, "product enrollment and the two successful warehouse revisions are audited once");
    }

    [StockMssqlFact]
    public async Task Actual_SQL_Server_decimal_ledger_reservation_consumption_reversal_and_transfer_survive_fresh_bridges()
    {
        var h = await Harness.CreateAsync(output);
        var source = await h.Warehouse("SOURCE");
        var target = await h.Warehouse("TARGET");
        var posting = new StockPosting(Guid.NewGuid(), h.Variant.Id, StockMovementKind.Receipt, StockProductSeed.PreciseQuantity, null, source.Id, "Receipt");
        var receipt = await h.Post(posting);
        receipt.CreatedBy.Should().Be(h.Member.BusinessUserId.ToString("D"));
        (await h.Post(posting)).Should().BeEquivalentTo(receipt);
        // Stock operation replay is scope-wide, while the persisted original actor remains unchanged.
        (await h.Bridge.PostAsync(h.Seed.OtherUser, h.Tenant, h.Organization, posting)).Should().BeEquivalentTo(receipt);
        await Error(() => h.Post(posting with { Quantity = 1m }), "STOCK_OPERATION_CONFLICT");
        (await h.Database.ScalarAsync<string>("SELECT ON_HAND FROM IVT_STOCK_BALANCE WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization", h.Scope))
            .Should().Be("999999999999.123456");
        var auditBefore = await h.Count("IVT_STOCK_AUDIT");
        var reserveOperation = Guid.NewGuid();
        var reservation = await h.Reserve(source, reserveOperation, 1.234567m);
        (await h.Reserve(source, reserveOperation, 1.234567m)).Should().Be(reservation);
        await Error(() => h.Post(new(Guid.NewGuid(), h.Variant.Id, StockMovementKind.Issue,
            StockProductSeed.PreciseQuantity, source.Id, null, "Cannot consume reserved stock")), "INSUFFICIENT_AVAILABLE_STOCK");
        var issue = await h.Bridge.ConsumeReservationAsync(h.Seed.User, h.Tenant, h.Organization, reservation.Id, reservation.Version);
        (await h.Bridge.ConsumeReservationAsync(h.Seed.User, h.Tenant, h.Organization, reservation.Id, reservation.Version)).Should().BeEquivalentTo(issue);
        var reverseOperation = Guid.NewGuid();
        var reversal = await h.Bridge.ReverseAsync(h.Seed.User, h.Tenant, h.Organization, issue.Id, issue.Version, reverseOperation, "Undo consumption");
        (await h.Bridge.ReverseAsync(h.Seed.User, h.Tenant, h.Organization, issue.Id, issue.Version, reverseOperation, "Undo consumption"))
            .Should().BeEquivalentTo(reversal);
        var replay = await h.Bridge.ConsumeReservationAsync(h.Seed.User, h.Tenant, h.Organization, reservation.Id, reservation.Version);
        replay.Id.Should().Be(issue.Id);
        replay.ReversedById.Should().Be(reversal.Id);
        (await h.Bridge.GetReservationAsync(h.Seed.User, h.Tenant, h.Organization, reservation.Id)).State.Should().Be(StockReservationState.Consumed);
        var transfer = await h.Post(new(Guid.NewGuid(), h.Variant.Id, StockMovementKind.Transfer, 0.000001m, source.Id, target.Id, "Exact transfer"));
        transfer.Deltas.Should().BeEquivalentTo(new[] { new StockDelta(source.Id, -0.000001m), new StockDelta(target.Id, 0.000001m) });
        (await h.Balance(source)).OnHand.Should().Be(999999999999.123455m);
        (await h.Balance(source)).Reserved.Should().Be(0m);
        (await h.Balance(target)).OnHand.Should().Be(0.000001m);
        (await h.Count("IVT_STOCK_MOVEMENT")).Should().Be(4);
        (await h.Count("IVT_STOCK_RESERVATION")).Should().Be(1);
        (await h.Count("IVT_STOCK_AUDIT")).Should().Be(auditBefore + 5);
        (await h.Database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM IVT_STOCK_AUDIT WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization
              AND RESOURCE_ID=@id AND OPERATION='consumed' AND PREVIOUS_VERSION=@previous
            """, new { tenant = h.Tenant.ToString("D"), organization = h.Organization.ToString("D"),
                id = reservation.Id.ToString("D"), previous = reservation.Version.ToString("D") })).Should().Be(1);
        var page = await h.Bridge.ListMovementsAsync(h.Seed.User, h.Tenant, h.Organization, h.Variant.Id, 1, 2);
        page.Total.Should().Be(4);
        page.Items.Should().HaveCount(2).And.OnlyContain(value => value.Posting.VariantId == h.Variant.Id);
    }

    [StockMssqlFact]
    public async Task Actual_SQL_Server_master_unit_change_blocks_new_operations_but_preserves_history_and_release()
    {
        var h = await Harness.CreateAsync(output);
        var warehouse = await h.Warehouse("FROZEN-UNIT");
        var posting = new StockPosting(Guid.NewGuid(), h.Variant.Id, StockMovementKind.Receipt, 5.123456m, null, warehouse.Id, "Original kg");
        var receipt = await h.Post(posting);
        var reservation = await h.Reserve(warehouse, Guid.NewGuid(), 0.123456m);
        var before = await h.Balance(warehouse);
        var auditBefore = await h.Count("IVT_STOCK_AUDIT");
        await h.Database.ExecuteAsync("UPDATE MDM_PRODUCT SET UNIT='g' WHERE PRODUCT_ID=@Product", h.Seed);
        await Error(() => h.Bridge.EnrollProductAsync(h.Seed.User, h.Tenant, h.Organization, h.Seed.Product, "g"), "PRODUCT_UNIT_CHANGED");
        await Error(() => h.Post(posting with { OperationId = Guid.NewGuid() }), "VARIANT_INACTIVE");
        await Error(() => h.Reserve(warehouse, Guid.NewGuid(), 1m), "VARIANT_INACTIVE");
        await Error(() => h.Bridge.ConsumeReservationAsync(h.Seed.User, h.Tenant, h.Organization, reservation.Id, reservation.Version), "VARIANT_INACTIVE");
        (await h.Balance(warehouse)).Should().Be(before);
        (await h.Count("IVT_STOCK_AUDIT")).Should().Be(auditBefore);
        var frozen = await h.Bridge.GetProductAsync(h.Seed.User, h.Tenant, h.Organization, h.Seed.Product);
        frozen.Id.Should().Be(h.Variant.Id);
        frozen.ProductId.Should().Be(h.Variant.ProductId);
        frozen.Unit.Should().Be("kg");
        frozen.Active.Should().BeFalse();
        (await h.Bridge.GetMovementAsync(h.Seed.User, h.Tenant, h.Organization, receipt.Id)).Should().BeEquivalentTo(receipt);
        (await h.Post(posting)).Should().BeEquivalentTo(receipt);
        var released = await h.Bridge.ReleaseReservationAsync(h.Seed.User, h.Tenant, h.Organization, reservation.Id, reservation.Version);
        released.State.Should().Be(StockReservationState.Released);
        (await h.Bridge.ReleaseReservationAsync(h.Seed.User, h.Tenant, h.Organization, reservation.Id, reservation.Version)).Should().Be(released);
        (await h.Balance(warehouse)).OnHand.Should().Be(5.123456m);
        (await h.Balance(warehouse)).Reserved.Should().Be(0m);
        (await h.Count("IVT_STOCK_PRODUCT")).Should().Be(1);
        (await h.Count("IVT_STOCK_MOVEMENT")).Should().Be(1);
        (await h.Count("IVT_STOCK_AUDIT")).Should().Be(auditBefore + 1);
        var scopes = new EquipmentSharingBridge(h.Database.DataSource,
            new BusinessMembershipBridge(h.Database.DataSource), new BusinessMasterDirectory(h.Database.DataSource));
        var binding = await scopes.GetScopeBindingAsync("admin", h.Tenant, h.Organization);
        await Error(() => scopes.BindScopeAsync("admin", h.Tenant, h.Organization, h.Seed.Plant, binding.Version, false),
            "SCOPE_HAS_STOCK_OR_RESERVATIONS");
        (await scopes.GetScopeBindingAsync("admin", h.Tenant, h.Organization)).Should().Be(binding);
    }

    [StockMssqlFact]
    public async Task Actual_SQL_Server_competing_reservations_cannot_overdraw_available_stock()
    {
        var h = await Harness.CreateAsync(output);
        var warehouse = await h.Warehouse("RACE");
        await h.Post(new(Guid.NewGuid(), h.Variant.Id, StockMovementKind.Receipt, 1.000001m, null, warehouse.Id, "Race opening"));
        var auditBefore = await h.Count("IVT_STOCK_AUDIT");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var operations = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var attempts = operations.Select(Attempt).ToArray();
        start.SetResult();
        var successes = await Task.WhenAll(attempts);
        successes.Count(value => value).Should().Be(1);
        await Error(() => h.Reserve(warehouse, operations[Array.IndexOf(successes, false)], 0.750001m), "INSUFFICIENT_AVAILABLE_STOCK");
        var balance = await h.Balance(warehouse);
        balance.OnHand.Should().Be(1.000001m);
        balance.Reserved.Should().Be(0.750001m);
        balance.Available.Should().Be(0.25m);
        (await h.Count("IVT_STOCK_RESERVATION")).Should().Be(1);
        (await h.Count("IVT_STOCK_AUDIT")).Should().Be(auditBefore + 1);

        async Task<bool> Attempt(Guid operation)
        {
            await start.Task;
            try
            {
                await h.Bridge.ReserveAsync(h.Seed.User, h.Tenant, h.Organization, operation, h.Variant.Id,
                    warehouse.Id, 0.750001m, "Reservation", timeout.Token);
                return true;
            }
            catch (BusinessException error) when (error.Code == "INSUFFICIENT_AVAILABLE_STOCK") { return false; }
            // A serializable deadlock victim rolls back; the fresh retry above must observe the winner's reservation.
            catch (SqlException error) when (error.Number == 1205) { return false; }
        }
    }

    [StockMssqlFact]
    public async Task Actual_SQL_Server_audit_failure_rolls_back_balance_and_movement_and_allows_same_operation_retry()
    {
        var h = await Harness.CreateAsync(output);
        var warehouse = await h.Warehouse("AUDIT-ROLLBACK");
        var posting = new StockPosting(Guid.NewGuid(), h.Variant.Id, StockMovementKind.Receipt, 0.123456m, null, warehouse.Id, "Atomic receipt");
        var auditBefore = await h.Count("IVT_STOCK_AUDIT");
        var trigger = "stock_audit_test_" + Guid.NewGuid().ToString("N");
        await h.Database.ExecuteAsync($"""
            CREATE TRIGGER dbo.[{trigger}] ON dbo.IVT_STOCK_AUDIT AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE TENANT_ID='{h.Tenant:D}')
                    THROW 51098, 'Stock acceptance audit failure', 1;
            END;
            """);
        try
        {
            (await Assert.ThrowsAsync<SqlException>(() => h.Post(posting))).Number.Should().Be(51098);
            (await h.Count("IVT_STOCK_BALANCE")).Should().Be(0);
            (await h.Count("IVT_STOCK_MOVEMENT")).Should().Be(0);
            (await h.Count("IVT_STOCK_AUDIT")).Should().Be(auditBefore);
        }
        finally { await h.Database.ExecuteAsync($"DROP TRIGGER dbo.[{trigger}]"); }
        var receipt = await h.Post(posting);
        (await h.Post(posting)).Should().BeEquivalentTo(receipt);
        (await h.Balance(warehouse)).OnHand.Should().Be(0.123456m);
        (await h.Count("IVT_STOCK_MOVEMENT")).Should().Be(1);
        (await h.Count("IVT_STOCK_AUDIT")).Should().Be(auditBefore + 1);
    }

    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);

    private sealed class Harness(MssqlContractDatabase database)
    {
        public MssqlContractDatabase Database { get; } = database;
        public StockProductSeed Seed { get; } = new();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Organization { get; } = Guid.NewGuid();
        public BusinessMembership Member { get; private set; } = null!;
        public ProductVariant Variant { get; private set; } = null!;
        public StockBridge Bridge => new(Database.DataSource, new BusinessMembershipBridge(Database.DataSource), new BusinessMasterDirectory(Database.DataSource));
        public object Scope => new { tenant = Tenant.ToString("D"), organization = Organization.ToString("D") };

        public static async Task<Harness> CreateAsync(ITestOutputHelper output)
        {
            var database = await MssqlContractDatabase.TryCreateAsync(output)
                ?? throw new InvalidOperationException("SQL Server acceptance cannot pass without a configured database.");
            var h = new Harness(database);
            await database.ExecuteAsync(StockProductSeed.Sql, h.Seed);
            var memberships = new BusinessMembershipBridge(database.DataSource);
            await new EquipmentSharingBridge(database.DataSource, memberships, new BusinessMasterDirectory(database.DataSource))
                .BindScopeAsync("admin", h.Tenant, h.Organization, h.Seed.Plant, null, true);
            foreach (var user in new[] { h.Seed.User, h.Seed.OtherUser })
            {
                var saved = await memberships.SaveMembershipAsync("admin", h.Tenant, h.Organization, user, new(0, true, StockProductSeed.Grants));
                saved.IsSuccess.Should().BeTrue();
                if (user == h.Seed.User) h.Member = saved.Value;
            }
            h.Variant = await h.Bridge.EnrollProductAsync(h.Seed.User, h.Tenant, h.Organization, h.Seed.Product, "kg");
            return h;
        }

        public Task<Warehouse> Warehouse(string code)
            => Bridge.CreateWarehouseAsync(Seed.User, Tenant, Organization, Guid.NewGuid(), code, code);
        public Task<StockMovement> Post(StockPosting posting) => Bridge.PostAsync(Seed.User, Tenant, Organization, posting);
        public Task<StockReservation> Reserve(Warehouse warehouse, Guid operation, decimal quantity)
            => Bridge.ReserveAsync(Seed.User, Tenant, Organization, operation, Variant.Id, warehouse.Id, quantity, "Reservation");
        public async Task<StockBalance> Balance(Warehouse warehouse)
            => (await Bridge.GetBalanceAsync(Seed.User, Tenant, Organization, Variant.Id, warehouse.Id))!;
        public Task<int> Count(string table)
            => Database.ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization", Scope);
    }
}

internal sealed class StockProductSeed
{
    public const string Password = "stock-product-test";
    // Legal six-place decimal near the Framework limit, with digits a binary double cannot preserve.
    public const decimal PreciseQuantity = 999999999999.123456m;
    public static readonly string[] Grants = ["stock.product.write", "stock.warehouse.read", "stock.warehouse.write",
        "stock.read", "stock.post", "stock.reverse", "stock.reserve", "stock.consume", "stock.release"];
    public string Plant { get; } = "STP-" + Guid.NewGuid().ToString("N");
    public string OtherPlant { get; } = "STP-" + Guid.NewGuid().ToString("N");
    public string Product { get; } = "STK-" + Guid.NewGuid().ToString("N");
    public string Role { get; } = "STR-" + Guid.NewGuid().ToString("N");
    public string User { get; } = "STU-" + Guid.NewGuid().ToString("N");
    public string OtherUser { get; } = "STU-" + Guid.NewGuid().ToString("N");
    public DateTime Now { get; } = DateTime.UtcNow;
    public string PasswordHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password))).ToLowerInvariant();
    public const string Sql = """
        INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Plant, 'Stock plant', 'admin', @Now, 'admin', @Now),
                   (@OtherPlant, 'Other stock plant', 'admin', @Now, 'admin', @Now);
        INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Product, 'Stock material', 'Material', 'kg', 'Valid', 'admin', @Now, 'admin', @Now);
        INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Role, 'Stock member', '', 'admin', @Now, 'admin', @Now);
        INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, IS_ACTIVE, IS_DELETED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@User, 'Stock operator', @PasswordHash, '', @Role, 1, 0, 'admin', @Now, 'admin', @Now),
                   (@OtherUser, 'Other stock operator', @PasswordHash, '', @Role, 1, 0, 'admin', @Now, 'admin', @Now);
        """;
}
