using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Application.MaintenanceSchedules;
using NexaOne.EMS.Application.MaintenanceExecution;
using NexaOne.EMS.Domain;
using NexaOne.EMS.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ems;
using NexaOne.SYS.Infrastructure;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// EMS 정비 실행 감사와 예비부품 원자 원장의 실제 SQLite 회귀 테스트.
/// 서비스 mock만 확인하지 않고 V115 스키마 및 실 리포지토리를 함께 통과시킨다.
/// </summary>
public sealed class EmsMaintenancePersistenceTests :
    IClassFixture<EmsMaintenancePersistenceTests.EmsPersistenceFactory>
{
    private readonly EmsPersistenceFactory _factory;

    public EmsMaintenancePersistenceTests(EmsPersistenceFactory factory) => _factory = factory;

    public sealed class EmsPersistenceFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-ems-persistence-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "ems-persistence-test-secret-key-at-least-32bytes");
            builder.UseSetting("Jwt:Issuer", "nexaone-ems-persistence-test");
            builder.UseSetting("Jwt:Audience", "nexaone-ems-persistence-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    private EesDataSource DataSource()
    {
        _ = _factory.CreateClient();
        return new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
    }

    private MaintenancePlanService PartsService()
    {
        var plans = new Mock<IMaintenancePlanRepository>(MockBehavior.Strict);
        var dataSource = DataSource();
        return new MaintenancePlanService(
            plans.Object,
            new SparePartRepository(dataSource),
            new EquipmentDirectory(dataSource));
    }

    private EmsService WorkOrders() => new(
        new WorkOrderRepository(DataSource(), new ConfigurationBuilder().Build()),
        new MaintenancePlanRepository(DataSource(), new ConfigurationBuilder().Build()));

    private MaintenanceScheduleService MaintenanceSchedules()
        => new(new MaintenanceScheduleRepository(DataSource()));

    private MaintenanceExecutionService MaintenanceExecution()
    {
        var dataSource = DataSource();
        return new MaintenanceExecutionService(
            new MaintenanceExecutionRepository(dataSource),
            new MaintenanceIdentityDirectory(dataSource));
    }

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

    private static string Id(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(48, prefix.Length + 33)];

    private void SeedPart(string partId, decimal stock)
    {
        Execute(@"INSERT INTO EMS_SPARE_PART
            (PART_ID, PART_NAME, PART_NUMBER, DESCRIPTION, UNIT_OF_MEASURE,
             CURRENT_STOCK, MIN_STOCK, MAX_STOCK, LOCATION,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@id, 'Bearing', @id, 'Drive bearing', 'EA', @stock, 2, 50, 'RACK-A',
             'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@id", partId), ("@stock", stock));
    }

    private void SeedEquipment(string equipmentId, string equipmentClassId = "CLASS-A")
    {
        Execute(@"INSERT INTO MDM_EQUIPMENT
            (EQUIPMENT_ID, EQUIPMENT_NAME, DESCRIPTION, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
             EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@id, @id, 'Maintenance test equipment', 'PLANT-01', 'AREA-01', 'Cleaner',
             @classId, 'Valid', 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@id", equipmentId), ("@classId", equipmentClassId));
    }

    private void SeedWorkOrder(string workOrderId, string equipmentId)
    {
        Execute(@"INSERT INTO EMS_WORK_ORDER
            (WO_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@wo, @equipment, 'BM', 'Bearing replacement', 'maintenance-login',
             CURRENT_TIMESTAMP, 'InProgress', 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@wo", workOrderId), ("@equipment", equipmentId));
    }

    private void SeedUser(string userId)
    {
        Execute(@"INSERT INTO SYS_USER
            (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE,
             IS_ACTIVE, IS_DELETED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@user, @user, 'hash', @email, 'MAINTENANCE', 'KoKr',
                    1, 0, 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@user", userId), ("@email", $"{userId}@test.local"));
    }

    private void SeedWorkerMap(string workerId, string userId)
    {
        Execute(@"INSERT INTO MDM_WORKER
            (WORKER_ID, WORKER_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@worker, @worker, 1, 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@worker", workerId));
        Execute(@"INSERT INTO MDM_WORKER_USER_MAP
            (WORKER_USER_MAP_ID, WORKER_ID, USER_ID, IS_ACTIVE, EFFECTIVE_FROM,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@map, @worker, @user, 1, @effective,
                    'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@map", Id("WORKER-MAP")), ("@worker", workerId), ("@user", userId),
            ("@effective", DateTime.UtcNow.AddDays(-1)));
    }

    private void SeedBom(string bomItemId, string partId, string equipmentId)
    {
        Execute(@"INSERT INTO EMS_EQUIPMENT_PART_BOM
            (BOM_ITEM_ID, EQUIPMENT_ID, PART_ID, QUANTITY_PER, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@bom, @equipment, @part, 1, 1,
             'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@bom", bomItemId), ("@equipment", equipmentId), ("@part", partId));
    }

    private void SeedMaintenancePlan(string planId, string planType = "PM")
    {
        Execute(@"INSERT INTO EMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE, SCHEDULED_DATE,
             ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@plan, @plan, 'EQ-PM', @planType, 'Manual', CURRENT_TIMESTAMP,
             1, 'maintainer', 'Planned', 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@plan", planId), ("@planType", planType));
    }

    [Fact]
    public async Task AdjustStock_commits_balance_and_authenticated_ledger_once()
    {
        var partId = Id("SP");
        var key = Id("PART-IDEM");
        var equipmentId = Id("EQ");
        var workOrderId = Id("EMS-WO");
        var bomItemId = Id("BOM");
        SeedPart(partId, 10m);
        SeedEquipment(equipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedBom(bomItemId, partId, equipmentId);
        var command = MaintenanceCommandContext.Create(
            "login-maintainer", key, "POP", "PANEL-01", "corr-parts").Value;
        var context = new SparePartAdjustmentContext(
            command, "Usage", workOrderId, equipmentId, Remark: "bearing replacement",
            BomItemId: bomItemId);

        var first = await PartsService().AdjustStockAsync(partId, -3m, context);
        var replay = await PartsService().AdjustStockAsync(partId, -3m, context);

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue("the same idempotency key and payload is a successful replay");
        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(7m);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<string>("SELECT PROCESSED_BY FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("login-maintainer");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("POP");
        Scalar<string>("SELECT CORRELATION_ID FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("corr-parts");
        Scalar<decimal>("SELECT BALANCE_BEFORE FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(10m);
        Scalar<decimal>("SELECT BALANCE_AFTER FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(7m);
        Scalar<long>(@"SELECT COUNT(*) FROM EMS_SPARE_PART_USAGE u
                       JOIN EMS_SPARE_PART_INOUT i ON i.INOUT_ID=u.INOUT_ID
                       WHERE i.IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1, "an equipment Usage retry must not duplicate the usage ledger");
        Scalar<string>(@"SELECT u.USED_BY FROM EMS_SPARE_PART_USAGE u
                         JOIN EMS_SPARE_PART_INOUT i ON i.INOUT_ID=u.INOUT_ID
                         WHERE i.IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("login-maintainer");
        Scalar<string>(@"SELECT u.EQUIPMENT_ID FROM EMS_SPARE_PART_USAGE u
                         JOIN EMS_SPARE_PART_INOUT i ON i.INOUT_ID=u.INOUT_ID
                         WHERE i.IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(equipmentId);
        Scalar<string>(@"SELECT u.WO_ID FROM EMS_SPARE_PART_USAGE u
                         JOIN EMS_SPARE_PART_INOUT i ON i.INOUT_ID=u.INOUT_ID
                         WHERE i.IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(workOrderId);
        Scalar<string>(@"SELECT u.BOM_ITEM_ID FROM EMS_SPARE_PART_USAGE u
                         JOIN EMS_SPARE_PART_INOUT i ON i.INOUT_ID=u.INOUT_ID
                         WHERE i.IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(bomItemId);
    }

    [Fact]
    public async Task Parallel_spare_part_usage_retries_converge_on_one_atomic_ledger()
    {
        var partId = Id("SP-RACE");
        var key = Id("PART-IDEM-RACE");
        var equipmentId = Id("EQ-RACE");
        var workOrderId = Id("EMS-WO-RACE");
        var bomItemId = Id("BOM-RACE");
        SeedPart(partId, 10m);
        SeedEquipment(equipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedBom(bomItemId, partId, equipmentId);
        var context = new SparePartAdjustmentContext(
            MaintenanceCommandContext.Create(
                "login-maintainer", key, "POP", "PANEL-01", "corr-parts-race").Value,
            "Usage", workOrderId, equipmentId, Remark: "bearing replacement",
            BomItemId: bomItemId);
        var service = PartsService();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.AdjustStockAsync(partId, -2m, context)));

        results.Should().OnlyContain(result => result.IsSuccess);
        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(8m);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<long>(@"SELECT COUNT(*) FROM EMS_SPARE_PART_USAGE u
                       JOIN EMS_SPARE_PART_INOUT i ON i.INOUT_ID=u.INOUT_ID
                       WHERE i.IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
    }

    [Fact]
    public async Task AdjustStock_ledger_failure_rolls_back_balance_update()
    {
        var partId = Id("SP-ROLLBACK");
        var key = Id("PART-ROLLBACK");
        SeedPart(partId, 10m);
        var trigger = $"TR_EMS_PART_FAIL_{Guid.NewGuid():N}";
        Execute($@"CREATE TRIGGER {trigger}
            BEFORE INSERT ON EMS_SPARE_PART_INOUT
            WHEN NEW.PART_ID = '{partId}'
            BEGIN SELECT RAISE(ABORT, 'forced EMS ledger failure'); END;");
        var context = new SparePartAdjustmentContext(
            MaintenanceCommandContext.Create("login-maintainer", key, "MES").Value,
            "Scrap");

        var act = () => PartsService().AdjustStockAsync(partId, -4m, context);
        await act.Should().ThrowAsync<Exception>();

        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(10m, "a failed ledger insert must roll the guarded stock update back");
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(0);
    }

    [Fact]
    public async Task AdjustStock_usage_without_equipment_fails_without_any_stock_or_ledger_write()
    {
        var partId = Id("SP-USAGE-NO-EQ");
        var key = Id("PART-USAGE-NO-EQ");
        SeedPart(partId, 10m);
        var context = new SparePartAdjustmentContext(
            MaintenanceCommandContext.Create("login-maintainer", key, "MES").Value,
            "Usage", EquipmentId: null);

        var result = await PartsService().AdjustStockAsync(partId, -2m, context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(nameof(SparePartAdjustmentContext.EquipmentId));
        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(10m);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_USAGE WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("EQ-BYPASS")]
    public async Task SparePart_repository_rejects_usage_that_bypasses_the_usage_ledger(
        string? equipmentId)
    {
        var partId = Id("SP-USAGE-BYPASS");
        var key = Id("PART-USAGE-BYPASS");
        SeedPart(partId, 10m);
        var now = DateTime.UtcNow;
        var transaction = new SparePartStockTransaction(
            Id("INOUT"), partId, "Usage", 2m, 10m, 8m, "login-maintainer", now,
            key, "MES", EquipmentId: equipmentId, Usage: null);

        var persisted = await new SparePartRepository(DataSource())
            .PersistAdjustmentAsync(transaction, null);

        persisted.Should().BeFalse();
        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(10m);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_USAGE WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(0);
    }

    [Fact]
    public async Task SparePart_repository_atomic_guard_rejects_a_usage_with_wrong_bom_scope()
    {
        var partId = Id("SP-REPO-SCOPE");
        var otherPartId = Id("SP-REPO-OTHER");
        var equipmentId = Id("EQ-REPO-SCOPE");
        var bomItemId = Id("BOM-REPO-SCOPE");
        var key = Id("PART-REPO-SCOPE");
        SeedPart(partId, 10m);
        SeedPart(otherPartId, 10m);
        SeedEquipment(equipmentId);
        SeedBom(bomItemId, otherPartId, equipmentId);
        var now = DateTime.UtcNow;
        var inoutId = Id("INOUT-REPO-SCOPE");
        var transaction = new SparePartStockTransaction(
            inoutId, partId, "Usage", 2m, 10m, 8m, "login-maintainer", now,
            key, "MES", EquipmentId: equipmentId,
            Usage: new SparePartUsage(
                Id("USAGE-REPO-SCOPE"), inoutId, partId, bomItemId, equipmentId,
                null, 2m, "login-maintainer", now));

        var persisted = await new SparePartRepository(DataSource())
            .PersistAdjustmentAsync(transaction, "CLASS-A");

        persisted.Should().BeFalse();
        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(10m);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_USAGE WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AdjustStock_usage_rejects_bom_with_wrong_part_or_equipment_scope(
        bool matchingPart,
        bool matchingEquipment)
    {
        var partId = Id("SP-SCOPE");
        var otherPartId = Id("SP-OTHER");
        var equipmentId = Id("EQ-SCOPE");
        var otherEquipmentId = Id("EQ-OTHER");
        var workOrderId = Id("EMS-WO-SCOPE");
        var bomItemId = Id("BOM-SCOPE");
        var key = Id("PART-SCOPE");
        SeedPart(partId, 10m);
        SeedPart(otherPartId, 10m);
        SeedEquipment(equipmentId);
        SeedEquipment(otherEquipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedBom(
            bomItemId,
            matchingPart ? partId : otherPartId,
            matchingEquipment ? equipmentId : otherEquipmentId);
        var context = new SparePartAdjustmentContext(
            MaintenanceCommandContext.Create("login-maintainer", key, "MES").Value,
            "Usage", workOrderId, equipmentId, BomItemId: bomItemId);

        var result = await PartsService().AdjustStockAsync(partId, -2m, context);

        result.IsFailure.Should().BeTrue();
        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(10m);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_USAGE WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(0);
    }

    [Fact]
    public async Task AdjustStock_usage_ledger_failure_rolls_back_stock_and_inout_ledger()
    {
        var partId = Id("SP-USAGE-ROLLBACK");
        var equipmentId = Id("EQ-USAGE-ROLLBACK");
        var workOrderId = Id("EMS-WO-USAGE-ROLLBACK");
        var bomItemId = Id("BOM-USAGE-ROLLBACK");
        var key = Id("PART-USAGE-ROLLBACK");
        SeedPart(partId, 10m);
        SeedEquipment(equipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedBom(bomItemId, partId, equipmentId);
        var trigger = $"TR_EMS_PART_USAGE_FAIL_{Guid.NewGuid():N}";
        Execute($@"CREATE TRIGGER {trigger}
            BEFORE INSERT ON EMS_SPARE_PART_USAGE
            WHEN NEW.PART_ID = '{partId}'
            BEGIN SELECT RAISE(ABORT, 'forced usage ledger failure'); END;");
        var context = new SparePartAdjustmentContext(
            MaintenanceCommandContext.Create("login-maintainer", key, "MES").Value,
            "Usage", workOrderId, equipmentId, BomItemId: bomItemId);

        var act = () => PartsService().AdjustStockAsync(partId, -3m, context);
        await act.Should().ThrowAsync<Exception>();

        Scalar<decimal>("SELECT CURRENT_STOCK FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(10m, "a usage ledger failure must roll the stock update back");
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0, "the in/out ledger belongs to the same transaction");
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_USAGE WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(0);
    }

    [Fact]
    public async Task WorkOrder_persists_maintenance_plan_and_authenticated_transition_history()
    {
        var planId = Id("MP");
        var workOrderId = Id("EMS-WO");
        Execute(@"INSERT INTO EMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE, SCHEDULED_DATE,
             ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@plan, 'Monthly PM', 'EQ01', 'PM', 'Monthly', CURRENT_TIMESTAMP,
             2, 'tech01', 'Planned', 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
            ("@plan", planId));
        var create = MaintenanceCommandContext.Create(
            "planner-login", Id("WO-CREATE"), "MES", correlationId: "corr-wo").Value;
        var startKey = Id("WO-START");
        var start = MaintenanceCommandContext.Create(
            "actual-maintainer", startKey, "MOBILE", "TABLET-01", "corr-wo").Value;

        var created = await WorkOrders().CreateWorkOrderAsync(
            workOrderId, "EQ01", "PM", "Monthly inspection", "tech01", planId, create);
        var started = await WorkOrders().StartWorkOrderAsync(workOrderId, start);
        var replay = await WorkOrders().StartWorkOrderAsync(workOrderId, start);
        var createReplay = await WorkOrders().CreateWorkOrderAsync(
            workOrderId, "EQ01", "PM", "Monthly inspection", "tech01", planId, create);

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue("a repeated start command must not attempt the transition twice");
        createReplay.IsSuccess.Should().BeTrue();
        createReplay.Value.Status.Should().Be(
            WorkOrderStatus.Issued,
            "creation replay returns the immutable creation result even after the live W/O starts");
        Scalar<string>("SELECT MAINTENANCE_PLAN_ID FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", workOrderId))
            .Should().Be(planId);
        Scalar<string>("SELECT STATUS FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", workOrderId))
            .Should().Be("InProgress");
        Scalar<long>("SELECT COUNT(*) FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", startKey))
            .Should().Be(1);
        Scalar<string>("SELECT ACTOR_ID FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", startKey))
            .Should().Be("actual-maintainer");
        Scalar<string>("SELECT FROM_STATUS FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", startKey))
            .Should().Be("Issued");
        Scalar<string>("SELECT TO_STATUS FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", startKey))
            .Should().Be("InProgress");
        Scalar<string>("SELECT CORRELATION_ID FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", startKey))
            .Should().Be("corr-wo");
        Scalar<string>("SELECT ACTOR_ID FROM EMS_WORK_ORDER_CREATE_COMMAND WHERE IDEMPOTENCY_KEY=@key", ("@key", create.IdempotencyKey))
            .Should().Be("planner-login");
    }

    [Fact]
    public async Task WorkOrder_create_replay_requires_the_same_full_business_payload()
    {
        var workOrderId = Id("EMS-WO-CREATE-REPLAY");
        var key = Id("WO-CREATE-REPLAY");
        var command = MaintenanceCommandContext.Create(
            "planner-login", key, "MES", correlationId: "corr-create-replay").Value;
        var service = WorkOrders();

        var first = await service.CreateWorkOrderAsync(
            workOrderId, "EQ-CREATE", "PM", "Original description", "tech01", null, command);
        var replay = await service.CreateWorkOrderAsync(
            workOrderId, "EQ-CREATE", "PM", "Original description", "tech01", null, command);
        var conflict = await service.CreateWorkOrderAsync(
            workOrderId, "EQ-CREATE", "PM", "Different description", "tech01", null, command);

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue("an exact create retry returns the committed work order");
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.WorkOrder.IdempotencyConflict");
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", workOrderId))
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER_CREATE_COMMAND WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<string>("SELECT ACTOR_ID FROM EMS_WORK_ORDER_CREATE_COMMAND WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("planner-login");
    }

    [Fact]
    public async Task WorkOrder_repository_idempotency_guard_preserves_the_committed_winner()
    {
        var key = Id("WO-REPO-GUARD");
        var winnerId = Id("EMS-WO-WINNER");
        var loserId = Id("EMS-WO-LOSER");
        var repository = new WorkOrderRepository(DataSource(), new ConfigurationBuilder().Build());
        var winner = WorkOrder.Create(
            winnerId, "EQ-GUARD", "PM", "Winner", "tech01", DateTime.UtcNow).Value;
        var loser = WorkOrder.Create(
            loserId, "EQ-GUARD", "PM", "Loser", "tech01", DateTime.UtcNow).Value;
        var winnerAction = new MaintenanceAction(
            Id("ACT-WINNER"), winnerId, "Create", null, "Issued", "login-tech",
            key, DateTime.UtcNow, CorrelationId: "corr-repo-guard");
        var loserAction = winnerAction with { ActionId = Id("ACT-LOSER"), WorkOrderId = loserId };

        var first = await repository.AddWithActionAsync(winner, winnerAction);
        var second = await repository.AddWithActionAsync(loser, loserAction);

        first.Should().BeTrue();
        second.Should().BeFalse("the repository leaves replay/conflict classification to the service");
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", winnerId))
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", loserId))
            .Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
    }

    [Fact]
    public async Task WorkOrder_repository_collapses_only_the_known_idempotency_unique_race()
    {
        var key = Id("WO-REPO-UNIQUE");
        var workOrderId = Id("EMS-WO-UNIQUE");
        var trigger = $"TR_EMS_WO_UNIQUE_{Guid.NewGuid():N}";
        Execute($@"
            CREATE TRIGGER {trigger}
            AFTER INSERT ON EMS_WORK_ORDER
            WHEN NEW.WO_ID = '{workOrderId}'
            BEGIN
              INSERT INTO EMS_MAINTENANCE_ACTION_HISTORY
                (ACTION_ID, WO_ID, EQUIPMENT_ID, MAINTENANCE_TYPE, ACTION_TYPE,
                 RESULT_STATUS, ACTOR_ID, SOURCE, CLIENT_CHANNEL, ACTION_AT,
                 IDEMPOTENCY_KEY, FROM_STATUS, TO_STATUS, CREATED_BY, CREATED_AT)
              VALUES
                ('{Id("ACT-RACE-WINNER")}', NEW.WO_ID, NEW.EQUIPMENT_ID, NEW.WO_TYPE, 'Create',
                 'Issued', 'login-tech', 'Manual', 'MES', CURRENT_TIMESTAMP,
                 '{key}', NULL, 'Issued', 'login-tech', CURRENT_TIMESTAMP);
            END;");
        var repository = new WorkOrderRepository(DataSource(), new ConfigurationBuilder().Build());
        var workOrder = WorkOrder.Create(
            workOrderId, "EQ-UNIQUE", "PM", "Unique race", "tech01", DateTime.UtcNow).Value;
        var action = new MaintenanceAction(
            Id("ACT-RACE-LOSER"), workOrderId, "Create", null, "Issued", "login-tech",
            key, DateTime.UtcNow);

        var persisted = await repository.AddWithActionAsync(workOrder, action);

        persisted.Should().BeFalse();
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", workOrderId))
            .Should().Be(0, "the losing transaction, including its trigger-created row, is rolled back");
        Scalar<long>("SELECT COUNT(*) FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0);
    }

    [Fact]
    public async Task WorkOrder_repository_does_not_swallow_non_unique_database_failures()
    {
        var key = Id("WO-REPO-FAIL");
        var workOrderId = Id("EMS-WO-FAIL");
        var trigger = $"TR_EMS_WO_FAIL_{Guid.NewGuid():N}";
        Execute($@"
            CREATE TRIGGER {trigger}
            BEFORE INSERT ON EMS_MAINTENANCE_ACTION_HISTORY
            WHEN NEW.IDEMPOTENCY_KEY = '{key}'
            BEGIN
              SELECT RAISE(ABORT, 'forced non-unique EMS work-order failure');
            END;");
        var repository = new WorkOrderRepository(DataSource(), new ConfigurationBuilder().Build());
        var workOrder = WorkOrder.Create(
            workOrderId, "EQ-FAIL", "PM", "Expected rollback", "tech01", DateTime.UtcNow).Value;
        var action = new MaintenanceAction(
            Id("ACT-FAIL"), workOrderId, "Create", null, "Issued", "login-tech",
            key, DateTime.UtcNow);

        var act = () => repository.AddWithActionAsync(workOrder, action);

        await act.Should().ThrowAsync<SqliteException>().WithMessage("*forced non-unique*");
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", workOrderId))
            .Should().Be(0, "an unrelated database fault must roll back and remain observable");
    }

    [Fact]
    public async Task Maintenance_schedule_acknowledgement_advances_state_and_appends_one_authenticated_row()
    {
        var planId = Id("PM-PLAN");
        var scheduleId = Id("PM-SCHEDULE");
        var key = Id("PM-ACK");
        var due = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        SeedMaintenancePlan(planId);
        var service = MaintenanceSchedules();
        var created = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            scheduleId, planId, "Calendar", 1m, "Day", NextDueAt: due,
            ActorId: "planner-login"));
        var command = new MaintenanceScheduleAcknowledgeCommand(
            scheduleId, 1, key, due.AddHours(2), ClientChannel: "MOBILE",
            DeviceId: "TABLET-01", CorrelationId: "corr-pm", Remark: "manual PM done",
            ActorId: "maintainer-login");

        var first = await service.AcknowledgeAsync(command);
        var replay = await service.AcknowledgeAsync(command);
        var conflict = await service.AcknowledgeAsync(command with { Remark = "changed" });

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue("the exact operator retry must return the immutable history row");
        replay.Value.AcknowledgementId.Should().Be(first.Value.AcknowledgementId);
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.MaintenanceSchedule.IdempotencyConflict");
        Scalar<long>("SELECT VERSION_NO FROM EMS_MAINTENANCE_SCHEDULE WHERE SCHEDULE_ID=@id", ("@id", scheduleId))
            .Should().Be(2);
        Scalar<string>("SELECT UPDATED_BY FROM EMS_MAINTENANCE_SCHEDULE WHERE SCHEDULE_ID=@id", ("@id", scheduleId))
            .Should().Be("maintainer-login");
        Scalar<long>("SELECT COUNT(*) FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<string>("SELECT ACKNOWLEDGED_BY FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("maintainer-login");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("MOBILE");
        Scalar<string>("SELECT CORRELATION_ID FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("corr-pm");
        Scalar<long>("SELECT LENGTH(REQUEST_HASH) FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(64);
    }

    [Fact]
    public async Task Maintenance_schedule_rejects_bm_plan_in_service_and_atomic_repository_guard()
    {
        var planId = Id("BM-PLAN");
        var pmPlanId = Id("PM-PLAN-GUARD");
        var serviceScheduleId = Id("BM-SCHEDULE-SERVICE");
        var repositoryScheduleId = Id("BM-SCHEDULE-REPO");
        var updateScheduleId = Id("PM-SCHEDULE-UPDATE-GUARD");
        var due = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        SeedMaintenancePlan(planId, "BM");
        SeedMaintenancePlan(pmPlanId);
        var repository = new MaintenanceScheduleRepository(DataSource());

        var serviceResult = await new MaintenanceScheduleService(repository).CreateAsync(
            new MaintenanceScheduleCreateCommand(
                serviceScheduleId, planId, "Calendar", 1m, "Day", NextDueAt: due,
                ActorId: "planner-login"));
        var repositoryResult = await repository.TryCreateAsync(new MaintenanceScheduleRecord(
            repositoryScheduleId, planId, "Calendar", 1m, "Day", "UTC",
            null, due, null, null, null, null, null, false, true, 1,
            "planner-login", DateTime.UtcNow, "planner-login", DateTime.UtcNow));
        var pmSchedule = new MaintenanceScheduleRecord(
            updateScheduleId, pmPlanId, "Calendar", 1m, "Day", "UTC",
            null, due, null, null, null, null, null, false, true, 1,
            "planner-login", DateTime.UtcNow, "planner-login", DateTime.UtcNow);
        (await repository.TryCreateAsync(pmSchedule)).Should().BeTrue();
        var updateResult = await repository.TryUpdateAsync(
            pmSchedule with
            {
                MaintenancePlanId = planId,
                Version = 2,
                UpdatedAt = DateTime.UtcNow,
            },
            1);

        serviceResult.IsFailure.Should().BeTrue();
        serviceResult.Error.Code.Should().Be("EMS.MaintenanceSchedule.PreventivePlanRequired");
        repositoryResult.Should().BeFalse();
        updateResult.Should().BeFalse();
        Scalar<long>(
                "SELECT COUNT(*) FROM EMS_MAINTENANCE_SCHEDULE WHERE MAINTENANCE_PLAN_ID=@plan",
                ("@plan", planId))
            .Should().Be(0);
        Scalar<string>(
                "SELECT MAINTENANCE_PLAN_ID FROM EMS_MAINTENANCE_SCHEDULE WHERE SCHEDULE_ID=@id",
                ("@id", updateScheduleId))
            .Should().Be(pmPlanId);
    }

    [Fact]
    public async Task Maintenance_schedule_history_failure_rolls_back_version_and_next_due()
    {
        var planId = Id("PM-PLAN-ROLLBACK");
        var scheduleId = Id("PM-SCHEDULE-ROLLBACK");
        var key = Id("PM-ACK-ROLLBACK");
        var due = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        SeedMaintenancePlan(planId);
        var service = MaintenanceSchedules();
        var created = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            scheduleId, planId, "Calendar", 1m, "Day", NextDueAt: due,
            ActorId: "planner-login"));
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        var trigger = $"TR_EMS_PM_ACK_FAIL_{Guid.NewGuid():N}";
        Execute($@"CREATE TRIGGER {trigger}
            BEFORE INSERT ON EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY
            WHEN NEW.SCHEDULE_ID = '{scheduleId}'
            BEGIN SELECT RAISE(ABORT, 'forced maintenance acknowledgement failure'); END;");

        var act = () => service.AcknowledgeAsync(new MaintenanceScheduleAcknowledgeCommand(
            scheduleId, 1, key, due.AddHours(1), ActorId: "maintainer-login"));

        await act.Should().ThrowAsync<SqliteException>().WithMessage("*forced maintenance acknowledgement failure*");
        Scalar<long>("SELECT VERSION_NO FROM EMS_MAINTENANCE_SCHEDULE WHERE SCHEDULE_ID=@id", ("@id", scheduleId))
            .Should().Be(1, "the schedule update and history insert belong to one transaction");
        Scalar<string>("SELECT NEXT_DUE_AT FROM EMS_MAINTENANCE_SCHEDULE WHERE SCHEDULE_ID=@id", ("@id", scheduleId))
            .Should().Contain("2026-08-26");
        Scalar<long>("SELECT COUNT(*) FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY WHERE SCHEDULE_ID=@id", ("@id", scheduleId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Maintenance_execution_persists_authenticated_check_and_labor_once()
    {
        var equipmentId = Id("EQ-EXEC");
        var workOrderId = Id("WO-EXEC");
        var workerId = Id("WORKER");
        var userId = Id("USER");
        var checkKey = Id("CHECK-KEY");
        var startKey = Id("LABOR-START");
        var endKey = Id("LABOR-END");
        var startedAt = DateTime.UtcNow.AddHours(-2);
        SeedEquipment(equipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedUser(userId);
        SeedWorkerMap(workerId, userId);
        var service = MaintenanceExecution();
        var checkCommand = new MaintenanceCheckCommand(
            Id("CHECK"), workOrderId, 1, "Bath temperature", DateTime.UtcNow,
            new EmsCommandContextDto(userId, checkKey, "MOBILE", "TABLET-01", "corr-exec"),
            MeasuredValue: 42.25m, Unit: "C", IsPass: true);
        var startCommand = new MaintenanceLaborStartCommand(
            Id("LABOR"), workOrderId, "Inspection", startedAt,
            new EmsCommandContextDto(userId, startKey, "POP", "PANEL-01", "corr-exec"),
            WorkerId: workerId, Remark: "manual inspection");

        var check = await service.RecordCheckAsync(checkCommand);
        var checkReplay = await service.RecordCheckAsync(checkCommand);
        var labor = await service.StartLaborAsync(startCommand);
        var laborReplay = await service.StartLaborAsync(startCommand);
        var completed = await service.CompleteLaborAsync(new MaintenanceLaborCompleteCommand(
            labor.Value.LaborId, labor.Value.Version, startedAt.AddMinutes(90),
            new EmsCommandContextDto(userId, endKey, "MOBILE", "TABLET-01", "corr-exec"),
            "inspection complete"));

        check.IsSuccess.Should().BeTrue(check.IsFailure ? check.Error.Description : string.Empty);
        checkReplay.IsSuccess.Should().BeTrue();
        checkReplay.Value.CheckResultId.Should().Be(check.Value.CheckResultId);
        labor.IsSuccess.Should().BeTrue(labor.IsFailure ? labor.Error.Description : string.Empty);
        laborReplay.IsSuccess.Should().BeTrue();
        laborReplay.Value.LaborId.Should().Be(labor.Value.LaborId);
        completed.IsSuccess.Should().BeTrue(completed.IsFailure ? completed.Error.Description : string.Empty);
        completed.Value.WorkerId.Should().Be(workerId);
        completed.Value.EndedBy.Should().Be(userId);
        completed.Value.LaborHours.Should().Be(1.5m);
        completed.Value.Version.Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER_CHECK_RESULT WHERE IDEMPOTENCY_KEY=@key", ("@key", checkKey))
            .Should().Be(1);
        Scalar<string>("SELECT RECORDED_BY FROM EMS_WORK_ORDER_CHECK_RESULT WHERE IDEMPOTENCY_KEY=@key", ("@key", checkKey))
            .Should().Be(userId);
        Scalar<string>("SELECT CLIENT_CHANNEL FROM EMS_WORK_ORDER_CHECK_RESULT WHERE IDEMPOTENCY_KEY=@key", ("@key", checkKey))
            .Should().Be("MOBILE");
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER_LABOR WHERE START_IDEMPOTENCY_KEY=@key", ("@key", startKey))
            .Should().Be(1);
        Scalar<string>("SELECT END_IDEMPOTENCY_KEY FROM EMS_WORK_ORDER_LABOR WHERE LABOR_ID=@id", ("@id", labor.Value.LaborId))
            .Should().Be(endKey);
    }

    [Fact]
    public async Task Maintenance_execution_rejects_worker_spoof_and_stale_completion()
    {
        var equipmentId = Id("EQ-EXEC-GUARD");
        var workOrderId = Id("WO-EXEC-GUARD");
        var workerId = Id("WORKER-GUARD");
        var userId = Id("USER-GUARD");
        var startedAt = DateTime.UtcNow.AddMinutes(-30);
        SeedEquipment(equipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedUser(userId);
        SeedWorkerMap(workerId, userId);
        var service = MaintenanceExecution();

        var spoofed = await service.StartLaborAsync(new MaintenanceLaborStartCommand(
            Id("LABOR-SPOOF"), workOrderId, "Work", startedAt,
            new EmsCommandContextDto(userId, Id("START-SPOOF")), WorkerId: "OTHER-WORKER"));
        var started = await service.StartLaborAsync(new MaintenanceLaborStartCommand(
            Id("LABOR-GUARD"), workOrderId, "Work", startedAt,
            new EmsCommandContextDto(userId, Id("START-GUARD")), WorkerId: workerId));
        var stale = await service.CompleteLaborAsync(new MaintenanceLaborCompleteCommand(
            started.Value.LaborId, 2, DateTime.UtcNow,
            new EmsCommandContextDto(userId, Id("END-STALE"))));

        spoofed.IsFailure.Should().BeTrue();
        spoofed.Error.Code.Should().Be("EMS.MaintenanceExecution.WorkerMappingMismatch");
        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error.Description : string.Empty);
        stale.IsFailure.Should().BeTrue();
        stale.Error.Code.Should().Be("EMS.MaintenanceExecution.LaborVersionConflict");
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER_LABOR WHERE WO_ID=@wo", ("@wo", workOrderId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Work_order_completion_waits_for_open_labor_to_close()
    {
        var equipmentId = Id("EQ-LABOR-GATE");
        var workOrderId = Id("WO-LABOR-GATE");
        var workerId = Id("WORKER-LABOR-GATE");
        var userId = Id("USER-LABOR-GATE");
        var startedAt = DateTime.UtcNow.AddMinutes(-15);
        SeedEquipment(equipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedUser(userId);
        SeedWorkerMap(workerId, userId);
        var execution = MaintenanceExecution();
        var labor = await execution.StartLaborAsync(new MaintenanceLaborStartCommand(
            Id("LABOR-GATE"), workOrderId, "Work", startedAt,
            new EmsCommandContextDto(userId, Id("LABOR-GATE-START")), WorkerId: workerId));

        var blocked = await WorkOrders().CompleteWorkOrderAsync(
            workOrderId, "done",
            MaintenanceCommandContext.Create(userId, Id("WO-GATE-BLOCKED"), "MES").Value);
        var laborCompleted = await execution.CompleteLaborAsync(new MaintenanceLaborCompleteCommand(
            labor.Value.LaborId, 1, DateTime.UtcNow,
            new EmsCommandContextDto(userId, Id("LABOR-GATE-END"))));
        var completed = await WorkOrders().CompleteWorkOrderAsync(
            workOrderId, "done",
            MaintenanceCommandContext.Create(userId, Id("WO-GATE-COMPLETE"), "MES").Value);

        labor.IsSuccess.Should().BeTrue(labor.IsFailure ? labor.Error.Description : string.Empty);
        blocked.IsFailure.Should().BeTrue();
        blocked.Error.Code.Should().Be("EMS.WorkOrder.OpenLabor");
        laborCompleted.IsSuccess.Should().BeTrue(laborCompleted.IsFailure ? laborCompleted.Error.Description : string.Empty);
        completed.IsSuccess.Should().BeTrue(completed.IsFailure ? completed.Error.Description : string.Empty);
        Scalar<string>("SELECT STATUS FROM EMS_WORK_ORDER WHERE WO_ID=@wo", ("@wo", workOrderId))
            .Should().Be("Completed");
    }

    [Fact]
    public async Task Work_order_repository_completion_guard_is_atomic_with_open_labor()
    {
        var equipmentId = Id("EQ-LABOR-RACE");
        var workOrderId = Id("WO-LABOR-RACE");
        var workerId = Id("WORKER-LABOR-RACE");
        var userId = Id("USER-LABOR-RACE");
        SeedEquipment(equipmentId);
        SeedWorkOrder(workOrderId, equipmentId);
        SeedUser(userId);
        SeedWorkerMap(workerId, userId);
        var labor = await MaintenanceExecution().StartLaborAsync(new MaintenanceLaborStartCommand(
            Id("LABOR-RACE"), workOrderId, "Work", DateTime.UtcNow.AddMinutes(-5),
            new EmsCommandContextDto(userId, Id("LABOR-RACE-START")), WorkerId: workerId));
        labor.IsSuccess.Should().BeTrue(labor.IsFailure ? labor.Error.Description : string.Empty);
        var repository = new WorkOrderRepository(DataSource(), new ConfigurationBuilder().Build());
        var workOrder = await repository.GetByIdAsync(workOrderId);
        workOrder.Should().NotBeNull();
        var from = workOrder!.Status;
        workOrder.Complete("should be guarded").IsSuccess.Should().BeTrue();
        var actionKey = Id("WO-LABOR-RACE-COMPLETE");
        var action = new MaintenanceAction(
            Id("ACTION-LABOR-RACE"), workOrderId, "Complete", from.ToString(),
            workOrder.Status.ToString(), userId, actionKey, DateTime.UtcNow);

        var persisted = await repository.UpdateWithActionAsync(workOrder, action);

        persisted.Should().BeFalse();
        Scalar<string>("SELECT STATUS FROM EMS_WORK_ORDER WHERE WO_ID=@wo", ("@wo", workOrderId))
            .Should().Be("InProgress");
        Scalar<long>("SELECT COUNT(*) FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE IDEMPOTENCY_KEY=@key", ("@key", actionKey))
            .Should().Be(0);
    }

    [Fact]
    public void V115_schema_contains_maintenance_execution_and_worker_mapping()
    {
        _ = DataSource();
        Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='MDM_WORKER_USER_MAP'")
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EMS_MAINTENANCE_SCHEDULE'")
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY'")
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EMS_WORK_ORDER_CHECK_RESULT'")
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EMS_WORK_ORDER_LABOR'")
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('EMS_WORK_ORDER') WHERE name='MAINTENANCE_PLAN_ID'")
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('EMS_MAINTENANCE_ACTION_HISTORY') WHERE name IN ('IDEMPOTENCY_KEY','FROM_STATUS','TO_STATUS','CORRELATION_ID')")
            .Should().Be(4);
        Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('EMS_SPARE_PART_INOUT') WHERE name IN ('IDEMPOTENCY_KEY','BALANCE_BEFORE','BALANCE_AFTER','CORRELATION_ID')")
            .Should().Be(4);
        Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('EMS_WORK_ORDER_CHECK_RESULT') WHERE name IN ('IDEMPOTENCY_KEY','REQUEST_HASH','CLIENT_CHANNEL','DEVICE_ID')")
            .Should().Be(4);
        Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('EMS_WORK_ORDER_LABOR') WHERE name IN ('START_IDEMPOTENCY_KEY','START_REQUEST_HASH','ENDED_BY','END_IDEMPOTENCY_KEY','END_REQUEST_HASH','VERSION_NO')")
            .Should().Be(6);
        Scalar<long>("SELECT [notnull] FROM pragma_table_info('EMS_SPARE_PART_USAGE') WHERE name='INOUT_ID'")
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM pragma_index_list('EMS_SPARE_PART_USAGE') WHERE name='UX_EMS_SPARE_USAGE_INOUT' AND [unique]=1")
            .Should().Be(1);
    }
}
