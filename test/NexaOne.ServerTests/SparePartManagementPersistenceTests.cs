using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.EMS.Application.SpareParts;
using NexaOne.EMS.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ems;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// V110 예비부품 정책 deep module의 실제 SQLite 멱등성, 낙관적 잠금 및 DB 불변식 회귀.
/// </summary>
public sealed class SparePartManagementPersistenceTests :
    IClassFixture<SparePartManagementPersistenceTests.SparePartFactory>
{
    private readonly SparePartFactory _factory;

    public SparePartManagementPersistenceTests(SparePartFactory factory) => _factory = factory;

    public sealed class SparePartFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-spare-part-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False;Default Timeout=10";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "spare-part-test-secret-key-at-least-32-bytes");
            builder.UseSetting("Jwt:Issuer", "spare-part-test");
            builder.UseSetting("Jwt:Audience", "spare-part-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Same_create_replays_changed_payload_conflicts_and_version_update_is_atomic()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partId = $"PART-{suffix}";
        SeedPart(partId, 7m);
        var command = Policy(partId, $"policy:create:{suffix}");
        var createResults = await Task.WhenAll(
            Service().SaveStockPolicyAsync(command),
            Service().SaveStockPolicyAsync(command));
        var first = createResults[0];
        var replay = createResults[1];
        var changedReplay = await Service().SaveStockPolicyAsync(command with { TargetStock = 21m });

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be(first.Value);
        changedReplay.IsFailure.Should().BeTrue();
        changedReplay.Error.Code.Should().Be("EMS.SparePart.IdempotencyConflict");

        var current = first.Value;
        var now = DateTime.UtcNow;
        var candidates = new[]
        {
            current with
            {
                TargetStock = 25m, Version = 2,
                LastIdempotencyKey = $"policy:update:a:{suffix}", LastRequestHash = "hash-a",
                UpdatedBy = "maint-a", UpdatedAt = now,
            },
            current with
            {
                TargetStock = 30m, Version = 2,
                LastIdempotencyKey = $"policy:update:b:{suffix}", LastRequestHash = "hash-b",
                UpdatedBy = "maint-b", UpdatedAt = now,
            },
        };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = candidates.Select(candidate => Task.Run(async () =>
        {
            await start.Task;
            return await Repository().TryUpdateStockPolicyAsync(candidate, expectedVersion: 1);
        })).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(updates);

        outcomes.Count(x => x).Should().Be(1);
        Scalar<long>("SELECT VERSION_NO FROM EMS_SPARE_PART_STOCK_POLICY WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(2);
        var winningTarget = Scalar<decimal>(
            "SELECT TARGET_STOCK FROM EMS_SPARE_PART_STOCK_POLICY WHERE PART_ID=@id", ("@id", partId));
        winningTarget.Should().BeOneOf(25m, 30m);
        Scalar<string>(
            "SELECT UPDATED_BY FROM EMS_SPARE_PART_STOCK_POLICY WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(winningTarget == 25m ? "maint-a" : "maint-b");
    }

    [Fact]
    public async Task Supplier_bom_and_replenishment_use_master_data_and_preserve_database_invariants()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partId = $"PART-{suffix}";
        var primaryVendor = $"V-P-{suffix}";
        var fastVendor = $"V-F-{suffix}";
        var equipmentClassId = $"CLASS-{suffix}";
        var equipmentId = $"EQ-{suffix}";
        SeedPart(partId, 7m);
        SeedVendor(primaryVendor);
        SeedVendor(fastVendor);
        SeedEquipmentClass(equipmentClassId);
        SeedEquipment(equipmentId, equipmentClassId);
        var service = Service();

        (await service.SaveStockPolicyAsync(Policy(partId, $"policy:{suffix}")))
            .IsSuccess.Should().BeTrue();
        (await service.SaveSupplierAsync(new SparePartSupplierCommand(
            $"SUP-FAST-{suffix}", partId, fastVendor, 1, 2m, 10m, "krw",
            false, true, 0, $"supplier:fast:{suffix}", ActorId: "logged-maint")))
            .IsSuccess.Should().BeTrue();
        var primaryId = $"SUP-PRIMARY-{suffix}";
        var primary = await service.SaveSupplierAsync(new SparePartSupplierCommand(
            primaryId, partId, primaryVendor, 4, 10m, 12.5m, "krw",
            true, true, 0, $"supplier:primary:{suffix}", ActorId: "logged-maint"));
        var bomId = $"BOM-{suffix}";
        var bom = await service.SaveEquipmentBomAsync(new EquipmentPartBomCommand(
            bomId, partId, 2m, equipmentId, null, "critical", 90, 1000m,
            "DRIVE-A", true, 0, $"bom:{suffix}", "logged-maint"));
        var classBom = await service.SaveEquipmentBomAsync(new EquipmentPartBomCommand(
            $"BOM-CLASS-{suffix}", partId, 1m, null, equipmentClassId, "High", null, null,
            null, true, 0, $"bom:class:{suffix}", "logged-maint"));
        var recommendation = await service.RecommendReplenishmentAsync(partId);

        primary.IsSuccess.Should().BeTrue(primary.IsFailure ? primary.Error.Description : string.Empty);
        primary.Value.Currency.Should().Be("KRW");
        primary.Value.UpdatedBy.Should().Be("logged-maint");
        bom.IsSuccess.Should().BeTrue(bom.IsFailure ? bom.Error.Description : string.Empty);
        bom.Value.Criticality.Should().Be("Critical");
        classBom.IsSuccess.Should().BeTrue(classBom.IsFailure ? classBom.Error.Description : string.Empty);
        recommendation.IsSuccess.Should().BeTrue(recommendation.IsFailure ? recommendation.Error.Description : string.Empty);
        recommendation.Value.PartSupplierId.Should().Be(primaryId);
        recommendation.Value.AvailableQuantity.Should().Be(4m);
        recommendation.Value.LeadTimeDemand.Should().Be(8m);
        recommendation.Value.EffectiveReorderPoint.Should().Be(13m);
        recommendation.Value.RecommendedOrderQuantity.Should().Be(16m);

        var supplierUpdate = await service.SaveSupplierAsync(new SparePartSupplierCommand(
            primaryId, partId, primaryVendor, 5, 10m, 12m, "KRW",
            true, true, 1, $"supplier:primary:update:{suffix}", ActorId: "planner-2"));
        var bomUpdate = await service.SaveEquipmentBomAsync(new EquipmentPartBomCommand(
            bomId, partId, 3m, equipmentId, null, "High", 120, 1200m,
            "DRIVE-A", true, 1, $"bom:update:{suffix}", "planner-2"));
        supplierUpdate.IsSuccess.Should().BeTrue();
        supplierUpdate.Value.Version.Should().Be(2);
        supplierUpdate.Value.UpdatedBy.Should().Be("planner-2");
        bomUpdate.IsSuccess.Should().BeTrue();
        bomUpdate.Value.Version.Should().Be(2);
        bomUpdate.Value.UpdatedBy.Should().Be("planner-2");

        Action secondPrimary = () => Execute(
            "UPDATE EMS_SPARE_PART_SUPPLIER SET IS_PRIMARY=1 WHERE PART_ID=@part AND VENDOR_ID=@vendor",
            ("@part", partId), ("@vendor", fastVendor));
        secondPrimary.Should().Throw<SqliteException>();

        Action ambiguousScope = () => Execute(
            "UPDATE EMS_EQUIPMENT_PART_BOM SET EQUIPMENT_CLASS_ID=@class WHERE BOM_ITEM_ID=@bom",
            ("@class", equipmentClassId), ("@bom", bomId));
        ambiguousScope.Should().Throw<SqliteException>();

        Action invalidPolicy = () => Execute(
            "UPDATE EMS_SPARE_PART_STOCK_POLICY SET SAFETY_STOCK=-1 WHERE PART_ID=@part",
            ("@part", partId));
        invalidPolicy.Should().Throw<SqliteException>();
    }

    [Fact]
    public void Existing_pre_version_V110_tables_are_upgraded_before_filtered_indexes_are_recreated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-spare-upgrade-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False";
        try
        {
            SqliteSchemaInitializer.Apply(connectionString);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE EMS_EQUIPMENT_PART_BOM;
                    DROP TABLE EMS_SPARE_PART_SUPPLIER;
                    DROP TABLE EMS_SPARE_PART_STOCK_POLICY;
                    CREATE TABLE EMS_SPARE_PART_STOCK_POLICY (
                        PART_ID TEXT NOT NULL PRIMARY KEY,
                        SAFETY_STOCK NUMERIC NOT NULL DEFAULT 0,
                        REORDER_POINT NUMERIC NOT NULL DEFAULT 0,
                        TARGET_STOCK NUMERIC NOT NULL DEFAULT 0,
                        RESERVED_QTY NUMERIC NOT NULL DEFAULT 0,
                        AVG_DAILY_USAGE NUMERIC NOT NULL DEFAULT 0,
                        SERVICE_LEVEL NUMERIC NULL,
                        REVIEW_CYCLE_DAYS INTEGER NULL,
                        IS_ACTIVE INTEGER NOT NULL DEFAULT 1,
                        CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                        CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                        UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE TABLE EMS_SPARE_PART_SUPPLIER (
                        PART_SUPPLIER_ID TEXT NOT NULL PRIMARY KEY,
                        PART_ID TEXT NOT NULL, VENDOR_ID TEXT NOT NULL,
                        VENDOR_PART_NO TEXT NULL, LEAD_TIME_DAYS INTEGER NOT NULL DEFAULT 0,
                        MOQ NUMERIC NULL, UNIT_PRICE NUMERIC NULL, CURRENCY TEXT NULL,
                        IS_PRIMARY INTEGER NOT NULL DEFAULT 0, IS_ACTIVE INTEGER NOT NULL DEFAULT 1,
                        CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                        CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                        UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE TABLE EMS_EQUIPMENT_PART_BOM (
                        BOM_ITEM_ID TEXT NOT NULL PRIMARY KEY,
                        EQUIPMENT_ID TEXT NULL, EQUIPMENT_CLASS_ID TEXT NULL,
                        PART_ID TEXT NOT NULL, QUANTITY_PER NUMERIC NOT NULL DEFAULT 1,
                        CRITICALITY TEXT NULL, REPLACEMENT_CYCLE_DAYS INTEGER NULL,
                        REPLACEMENT_CYCLE_COUNT NUMERIC NULL, POSITION_CODE TEXT NULL,
                        IS_ACTIVE INTEGER NOT NULL DEFAULT 1,
                        CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                        CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                        UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    """;
                command.ExecuteNonQuery();
            }

            var initialize = () => SqliteSchemaInitializer.EnsureSchema(connectionString);
            initialize.Should().NotThrow();

            using var upgraded = new SqliteConnection(connectionString);
            upgraded.Open();
            foreach (var table in new[]
                     {
                         "EMS_SPARE_PART_STOCK_POLICY",
                         "EMS_SPARE_PART_SUPPLIER",
                         "EMS_EQUIPMENT_PART_BOM",
                     })
            {
                using var columns = upgraded.CreateCommand();
                columns.CommandText = $"SELECT GROUP_CONCAT(name) FROM pragma_table_info('{table}')";
                var names = Convert.ToString(columns.ExecuteScalar(), CultureInfo.InvariantCulture)!;
                names.Should().Contain("VERSION_NO");
                names.Should().Contain("LAST_IDEMPOTENCY_KEY");
                names.Should().Contain("LAST_REQUEST_HASH");
            }

            using var index = upgraded.CreateCommand();
            index.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type='index' AND name IN (
                    'UX_EMS_SPARE_STOCK_POLICY_IDEMPOTENCY',
                    'UX_EMS_SPARE_PART_PRIMARY_ACTIVE',
                    'UX_EMS_SPARE_PART_SUPPLIER_IDEMPOTENCY',
                    'UX_EMS_EQUIPMENT_PART_BOM_IDEMPOTENCY')
                """;
            Convert.ToInt64(index.ExecuteScalar(), CultureInfo.InvariantCulture).Should().Be(4);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    private SparePartService Service() => new(Repository());

    private SparePartManagementRepository Repository() => new(DataSource());

    private EesDataSource DataSource()
    {
        _ = _factory.CreateClient();
        return new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
    }

    private static SparePartStockPolicyCommand Policy(string partId, string key) => new(
        partId, 5m, 8m, 20m, 3m, 2m, 0.95m, 7, true, 0, key, "logged-maint");

    private void SeedPart(string partId, decimal stock) => Execute(@"
        INSERT INTO EMS_SPARE_PART
            (PART_ID, PART_NAME, PART_NUMBER, DESCRIPTION, UNIT_OF_MEASURE,
             CURRENT_STOCK, MIN_STOCK, MAX_STOCK, LOCATION,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
            (@id, @id, @id, 'Spare-part test', 'EA', @stock, 2, 50, 'RACK-A',
             'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", partId), ("@stock", stock));

    private void SeedVendor(string vendorId) => Execute(@"
        INSERT INTO MDM_VENDOR
            (VENDOR_ID, VENDOR_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES (@id, @id, 1, 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", vendorId));

    private void SeedEquipmentClass(string equipmentClassId) => Execute(@"
        INSERT INTO MDM_EQUIPMENT_CLASS
            (EQUIPMENT_CLASS_ID, EQUIPMENT_CLASS_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES (@id, @id, 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", equipmentClassId));

    private void SeedEquipment(string equipmentId, string equipmentClassId) => Execute(@"
        INSERT INTO MDM_EQUIPMENT
            (EQUIPMENT_ID, EQUIPMENT_NAME, DESCRIPTION, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
             EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
            (@id, @id, 'Spare-part test equipment', 'PLANT-01', 'AREA-01', 'Cleaner',
             @class, 'Valid', 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", equipmentId), ("@class", equipmentClassId));

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        _ = _factory.CreateClient();
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}
