using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaFramework.Service.Inventory;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Infrastructure;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Erp;
using NexaOne.SYS.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

public sealed class BillingProductSourcePersistenceTests :
    IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 30, 0, TimeSpan.Zero);
    private static readonly string[] Grants =
    [
        "billing.read", "billing.write", "stock.product.write", "stock.warehouse.read",
        "stock.warehouse.write", "stock.read", "stock.post", "stock.reverse"
    ];
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly EesDataSource _dataSource;
    private readonly BusinessMembershipBridge _memberships;
    private readonly BusinessMasterDirectory _masters;
    private readonly StockBridge _stock;
    private readonly BillingBridge _billing;
    private readonly ITestOutputHelper _output;

    public BillingProductSourcePersistenceTests(BusinessMembershipDatabaseTemplate template,
        ITestOutputHelper output)
    {
        _output = output;
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _dataSource = new() { Provider = new SqliteProvider(), ConnectionString = _connectionString };
        _memberships = new(_dataSource);
        _masters = new(_dataSource);
        var clock = new FixedBillingClock(Now);
        _stock = new(_dataSource, _memberships, _masters, clock);
        _billing = new(_dataSource, _memberships, _masters, clock,
            stockBilling: new StockBillingDirectory());
        Execute("""
            INSERT INTO MDM_PLANT (PLANT_ID,PLANT_NAME) VALUES ('BILLING-STOCK-PLANT','Billing stock plant');
            INSERT INTO MDM_PRODUCT (PRODUCT_ID,PRODUCT_NAME,PRODUCT_TYPE,UNIT)
                VALUES ('BILLING-STOCK-PRODUCT','Billable product','Finished','EA');
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID,CUSTOMER_NAME,IS_ACTIVE)
                VALUES ('BILLING-STOCK-CUSTOMER','Product customer',1);
            INSERT INTO SYS_ROLE (ROLE_ID,ROLE_NAME,PERMISSIONS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('BILLING-STOCK-ROLE','Billing stock role','','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID,USER_NAME,PASSWORD_HASH,EMAIL,ROLE_ID,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
                VALUES ('billing-stock-user','Billing stock user','','','BILLING-STOCK-ROLE','admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            """);
    }

    [StockMssqlFact]
    public async Task Actual_SQL_Server_registers_and_claims_inventory_product_sources()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null) return;
        var seed = new BillingProductSeed();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var productCode = "BPS-" + Guid.NewGuid().ToString("N");
        await database.ExecuteAsync(BillingProductSeed.Sql, seed);
        await database.ExecuteAsync("""
            INSERT INTO MDM_PRODUCT
                (PRODUCT_ID,PRODUCT_NAME,PRODUCT_TYPE,UNIT,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT)
            VALUES (@Product,'SQL billable product','Finished','EA','admin',@Now,'admin',@Now)
            """, new { Product = productCode, seed.Now });
        var memberships = new BusinessMembershipBridge(database.DataSource);
        var masters = new BusinessMasterDirectory(database.DataSource);
        (await memberships.SaveMembershipAsync("admin", tenant, organization, seed.User,
            new(0, true, Grants))).IsSuccess.Should().BeTrue();
        var equipment = new EquipmentSharingBridge(database.DataSource, memberships, masters);
        await equipment.BindScopeAsync("admin", tenant, organization, seed.Plant, null, true);
        var stock = new StockBridge(database.DataSource, memberships, masters,
            new FixedBillingClock(Now));
        var billing = new BillingBridge(database.DataSource, memberships, masters,
            new FixedBillingClock(Now), stockBilling: new StockBillingDirectory());
        var variant = await stock.EnrollProductAsync(seed.User, tenant, organization, productCode, "EA");
        var warehouse = await stock.CreateWarehouseAsync(seed.User, tenant, organization,
            Guid.NewGuid(), "SQL-MAIN", "SQL main");
        await stock.PostAsync(seed.User, tenant, organization,
            new(Guid.NewGuid(), variant.Id, StockMovementKind.Receipt, 3m, null, warehouse.Id, "receipt"));
        var issue = await stock.PostAsync(seed.User, tenant, organization,
            new(Guid.NewGuid(), variant.Id, StockMovementKind.Issue, 1.25m, warehouse.Id, null, "shipment"));
        var contact = await billing.EnrollContactAsync(seed.User, tenant, organization, seed.Customer);
        var source = await billing.RegisterProductSourceAsync(seed.User, tenant, organization, issue.Id,
            new(contact.Id, "KRW", "SQL product shipment", 20m));
        source.ProductId.Should().Be(variant.ProductId);
        var generated = await billing.GenerateAutomaticInvoiceAsync(seed.User, tenant, organization,
            Guid.NewGuid(), new(contact.Id, BillingInvoiceType.ByProducts,
                new(2026, 9, 1), new(2026, 9, 30), new(2026, 9, 26), new(2026, 10, 26), "KRW"));
        generated.Document.Totals.Total.Should().Be(25m);
        (await billing.GetProductSourceAsync(seed.User, tenant, organization, issue.Id)).InvoiceId
            .Should().Be(generated.Document.Id);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM ERP_BILLING_PRODUCT_SOURCE "
            + "WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization",
            new { tenant = tenant.ToString("D"), organization = organization.ToString("D") }))
            .Should().Be(1);
    }

    public async Task InitializeAsync()
    {
        var equipment = new EquipmentSharingBridge(_dataSource, _memberships, _masters);
        await equipment.BindScopeAsync("admin", _tenant, _organization, "BILLING-STOCK-PLANT", null, true);
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization,
            "billing-stock-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        File.Delete(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Inventory_issue_registers_replays_generates_and_is_claimed_once()
    {
        var variant = await _stock.EnrollProductAsync("billing-stock-user", _tenant, _organization,
            "BILLING-STOCK-PRODUCT", "EA");
        var warehouse = await _stock.CreateWarehouseAsync("billing-stock-user", _tenant, _organization,
            Guid.NewGuid(), "MAIN", "Main");
        await _stock.PostAsync("billing-stock-user", _tenant, _organization,
            new(Guid.NewGuid(), variant.Id, StockMovementKind.Receipt, 10m, null, warehouse.Id, "receipt"));
        var issue = await _stock.PostAsync("billing-stock-user", _tenant, _organization,
            new(Guid.NewGuid(), variant.Id, StockMovementKind.Issue, 2.5m, warehouse.Id, null, "shipment"));
        var contact = await _billing.EnrollContactAsync("billing-stock-user", _tenant, _organization,
            "BILLING-STOCK-CUSTOMER");
        var input = new BillingProductSourceInput(contact.Id, "KRW", "Billable product / shipment",
            12.345678m, ApplyTax: false);

        var registered = await _billing.RegisterProductSourceAsync("billing-stock-user", _tenant,
            _organization, issue.Id, input);
        registered.Should().Match<BillingProductSource>(value =>
            value.MovementId == issue.Id && value.ProductId == variant.ProductId
            && value.VariantId == variant.Id && value.Quantity == 2.5m
            && value.UnitPrice == 12.345678m && value.State == BillingProductSourceState.Eligible);
        (await NewBilling().RegisterProductSourceAsync("billing-stock-user", _tenant, _organization,
            issue.Id, input)).Should().Be(registered);
        await Error(() => NewBilling().RegisterProductSourceAsync("billing-stock-user", _tenant,
            _organization, issue.Id, input with { UnitPrice = 13m }), "BILLING_PRODUCT_SOURCE_CONFLICT");

        var request = new AutomaticBillingRequest(contact.Id, BillingInvoiceType.ByProducts,
            new(2026, 9, 1), new(2026, 9, 30), new(2026, 9, 26), new(2026, 10, 26), "KRW");
        var generated = await NewBilling().GenerateAutomaticInvoiceAsync("billing-stock-user", _tenant,
            _organization, Guid.NewGuid(), request);
        generated.Document.Input.Lines.Should().Equal(
            new BillingLine(input.Description, input.UnitPrice, issue.Posting.Quantity, false, true));
        generated.Generation.Sources.Should().ContainSingle().Which.Should().Match<AutomaticBillingSourceLink>(
            value => value.SourceId == issue.Id && value.Kind == BillingSourceKind.Product
                && value.ProductId == variant.ProductId);
        var invoiced = await NewBilling().GetProductSourceAsync("billing-stock-user", _tenant,
            _organization, issue.Id);
        invoiced.State.Should().Be(BillingProductSourceState.Invoiced);
        invoiced.InvoiceId.Should().Be(generated.Document.Id);
        await Error(() => NewBilling().GenerateAutomaticInvoiceAsync("billing-stock-user", _tenant,
            _organization, Guid.NewGuid(), request), "AUTOMATIC_BILLING_SOURCE_NOT_FOUND");
        Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_PRODUCT_SOURCE").Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM ERP_AUTOMATIC_BILLING_SOURCE WHERE SOURCE_KIND=1").Should().Be(1);
    }

    [Fact]
    public async Task Only_unreversed_issues_can_become_product_sources()
    {
        var variant = await _stock.EnrollProductAsync("billing-stock-user", _tenant, _organization,
            "BILLING-STOCK-PRODUCT", "EA");
        var warehouse = await _stock.CreateWarehouseAsync("billing-stock-user", _tenant, _organization,
            Guid.NewGuid(), "REV", "Reversal");
        var receipt = await _stock.PostAsync("billing-stock-user", _tenant, _organization,
            new(Guid.NewGuid(), variant.Id, StockMovementKind.Receipt, 5m, null, warehouse.Id, "receipt"));
        var contact = await _billing.EnrollContactAsync("billing-stock-user", _tenant, _organization,
            "BILLING-STOCK-CUSTOMER");
        var input = new BillingProductSourceInput(contact.Id, "KRW", "Product", 1m);
        await Error(() => _billing.RegisterProductSourceAsync("billing-stock-user", _tenant,
            _organization, receipt.Id, input), "STOCK_MOVEMENT_NOT_BILLABLE");

        var issue = await _stock.PostAsync("billing-stock-user", _tenant, _organization,
            new(Guid.NewGuid(), variant.Id, StockMovementKind.Issue, 1m, warehouse.Id, null, "issue"));
        await _billing.RegisterProductSourceAsync("billing-stock-user", _tenant, _organization, issue.Id, input);
        await _stock.ReverseAsync("billing-stock-user", _tenant, _organization, issue.Id,
            issue.Version, Guid.NewGuid(), "cancelled shipment");
        (await _billing.GetProductSourceAsync("billing-stock-user", _tenant, _organization, issue.Id))
            .State.Should().Be(BillingProductSourceState.Reversed);
        await Error(() => NewBilling().GenerateAutomaticInvoiceAsync("billing-stock-user", _tenant,
            _organization, Guid.NewGuid(), new(contact.Id, BillingInvoiceType.ByProducts,
                new(2026, 9, 1), new(2026, 9, 30), new(2026, 9, 26), new(2026, 10, 26), "KRW")),
            "AUTOMATIC_BILLING_SOURCE_NOT_FOUND");
    }

    private BillingBridge NewBilling() => new(_dataSource,
        new BusinessMembershipBridge(_dataSource), new BusinessMasterDirectory(_dataSource),
        new FixedBillingClock(Now), stockBilling: new StockBillingDirectory());
    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute(sql);
    }
    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection.ExecuteScalar<T>(sql)!;
    }
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);
}

file sealed class FixedBillingClock(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}
