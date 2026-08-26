using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.WorkOrders;
using NexaOne.POM.Domain;
using NexaOne.POM.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Pom;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Infrastructure;
using NexaOne.ServiceContracts.Qms;
using NexusCom.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class PomLotPersistenceTests : IClassFixture<PomLotPersistenceTests.LotFactory>
{
    private readonly LotFactory _factory;
    public PomLotPersistenceTests(LotFactory factory) => _factory = factory;

    public sealed class LotFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-lot-p0-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "pom-lot-p0-integration-secret-key-32bytes!!!!");
            builder.UseSetting("Jwt:Issuer", "pom-lot-p0-test");
            builder.UseSetting("Jwt:Audience", "pom-lot-p0-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    private EesDataSource DataSource()
    {
        _ = _factory.CreateClient();
        return new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString
        };
    }

    private static IProductionQualityGateway QualityGateway(EesDataSource dataSource) =>
        new ProductionQualityGateService(new ProductionQualityGateEvidenceRepository(dataSource));

    private LotTrackingService BuildService()
    {
        var ds = DataSource();
        return new LotTrackingService(
            new LotRepository(ds, new ConfigurationBuilder().Build()),
            new LotHistoryRepository(ds, new SqliteEesDbCapability()),
            new LotMixingRelationRepository(ds),
            new PomWorkOrderRepository(ds),
            new TrackingMasterGateway(ds),
            QualityGateway(ds));
    }

    private PomWorkOrderService BuildWorkOrderService()
    {
        var ds = DataSource();
        var config = new ConfigurationBuilder().Build();
        return new PomWorkOrderService(
            new PomWorkOrderRepository(ds),
            new ProductionOrderRepository(ds, config),
            new LotRepository(ds, config),
            QualityGateway(ds));
    }

    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        _ = _factory.CreateClient();
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private (string Lot, string WorkOrder) SeedReleasedWorkOrderLot()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var plan = $"LP_{suffix}";
        var order = $"LO_{suffix}";
        var workOrder = $"LW_{suffix}";
        var lot = $"LOT_{suffix}";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Exec("""
            INSERT INTO POM_PRODUCTION_PLAN
              (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY, PLANNED_START_DATE,
               PLANNED_END_DATE, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@plan, @plan, 'PLANT01', 'ITEM01', 10, @now, @now, 'Released', 'TEST', @now, 'TEST', @now);
            INSERT INTO POM_PRODUCTION_ORDER
              (ORDER_ID, PLAN_ID, EQUIPMENT_ID, PRODUCT_ID, ORDER_QTY, SCHEDULED_START,
               SCHEDULED_END, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@order, @plan, 'EQ01', 'ITEM01', 10, @now, @now, 'Issued', 'TEST', @now, 'TEST', @now);
            INSERT INTO POM_WORK_ORDER
              (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCTION_ORDER_ID, EQUIPMENT_ID,
               PRODUCT_ID, PROCESS_ID, PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY,
               STATUS, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@workOrder, 'PLANT01', @workOrder, @order, 'EQ01', 'ITEM01', 'CUT',
               10, 0, 0, 0, 'Released', 'N', 1, 'TEST', @now, 'TEST', @now);
            INSERT INTO POM_LOT
              (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE,
               PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT)
            VALUES (@lot, 'PLANT01', @workOrder, 'ITEM01', 10, 0, 'Queued', 'Idle', 'CUT', 0, 'N', 1, 'TEST', @now);
            """,
            ("@plan", plan), ("@order", order), ("@workOrder", workOrder), ("@lot", lot), ("@now", now));
        return (lot, workOrder);
    }

    private void SeedDefectClasses(params string[] defectCodes)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var code in defectCodes)
        {
            Exec("""
                INSERT INTO QMS_DEFECT_CLASS
                  (DEFECT_CLASS_ID, DEFECT_CLASS_NAME, SEVERITY, IS_ACTIVE, IS_DELETED,
                   CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@code, @code, 'Minor', 1, 0, 'TEST', @now, 'TEST', @now);
                """, ("@code", code), ("@now", now));
        }
    }

    [Fact]
    public async Task Work_order_repository_round_trips_serial_route_scope()
    {
        var (_, baseWorkOrder) = SeedReleasedWorkOrderLot();
        var productionOrder = Scalar<string>(
            "SELECT PRODUCTION_ORDER_ID FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@workOrder",
            ("@workOrder", baseWorkOrder));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var workOrderId = $"SRO_{suffix}";
        var created = PomWorkOrder.Create(
            workOrderId, productionOrder, "PLANT01", "Serial route work order", "ITEM01", 10m,
            DateTime.UtcNow, DateTime.UtcNow.AddHours(8), processId: null, equipmentId: "EQ01",
            ownerId: "operator", createdBy: "planner", routingId: "RT01",
            routingScope: PomWorkOrderRoutingScope.SerialRoute);
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);

        var repository = new PomWorkOrderRepository(DataSource());
        await repository.AddAsync(created.Value);
        var restored = await repository.GetByIdAsync(workOrderId);

        restored.Should().NotBeNull();
        restored!.RoutingScope.Should().Be(PomWorkOrderRoutingScope.SerialRoute);
        restored.RoutingId.Should().Be("RT01");
        restored.RoutingStepNo.Should().BeNull();
        restored.ProcessId.Should().BeNull();
        Scalar<string>("SELECT ROUTING_SCOPE FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@workOrder",
            ("@workOrder", workOrderId)).Should().Be("SerialRoute");
    }

    [Fact]
    public async Task TrackIn_is_atomic_versioned_and_exact_retry_is_idempotent()
    {
        var (lot, workOrder) = SeedReleasedWorkOrderLot();
        var service = BuildService();
        var command = new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator",
            ExpectedVersion: 1, IdempotencyKey: $"TI:{lot}",
            ClientChannel: "MOBILE", DeviceId: "PDA-01");

        var first = await service.TrackInAsync(command);
        var replay = await service.TrackInAsync(command);
        var stale = await service.TrackInAsync(command with { IdempotencyKey = $"TI-STALE:{lot}" });

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue("an exact client retry must return the persisted result");
        stale.IsFailure.Should().BeTrue("a different request cannot reuse stale version 1");
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(2);
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("Processing");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(1);
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("MOBILE");
        Scalar<string>("SELECT DEVICE_ID FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("PDA-01");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_HISTORY WHERE LOT_ID=@lot AND EXECUTION_ID='TrackIn'", ("@lot", lot)).Should().Be(1);
        Scalar<string>("SELECT STATUS FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be("Started");
        Scalar<long>("SELECT VERSION_NO FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be(1);
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be("MOBILE");
        Scalar<string>("SELECT DEVICE_ID FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be("PDA-01");
        Scalar<long>("SELECT EXPECTED_VERSION FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be(1);
        Scalar<long>("SELECT RESULT_VERSION FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be(2);
    }

    [Fact]
    public async Task TrackOut_rejects_zero_qty_without_a_database_constraint_exception()
    {
        var (lot, _) = SeedReleasedWorkOrderLot();
        var service = BuildService();
        (await service.TrackInAsync(new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator", 1, $"TI-ZERO:{lot}")))
            .IsSuccess.Should().BeTrue();

        var result = await service.TrackOutAsync(new TrackOutCommand(
            "PLANT01", lot, "EQ01", 0m, null, null, "operator", 2, $"TO-ZERO:{lot}"));

        result.IsFailure.Should().BeTrue("zero quantity must be reported as a validation error, not a DB 500");
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("Processing");
        Scalar<decimal>("SELECT QTY FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(10m);
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot AND ACTION='TrackOut'", ("@lot", lot))
            .Should().Be(0);
    }

    [Fact]
    public async Task TrackOut_persists_code_level_defects_with_the_execution_and_exact_retry_is_single_write()
    {
        var (lot, _) = SeedReleasedWorkOrderLot();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var defectA = $"D_A_{suffix}";
        var defectB = $"D_B_{suffix}";
        var process = $"DEF_{suffix}";
        SeedDefectClasses(defectA, defectB);
        Exec("""
            UPDATE POM_LOT SET ROUTE_STEPS=@process WHERE LOT_ID=@lot;
            UPDATE POM_WORK_ORDER SET PROCESS_ID=@process WHERE WORK_ORDER_ID=(
                SELECT WORK_ORDER_ID FROM POM_LOT WHERE LOT_ID=@lot);
            """, ("@process", process), ("@lot", lot));
        var service = BuildService();
        (await service.TrackInAsync(new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator", 1, $"TI-DEF:{lot}")))
            .IsSuccess.Should().BeTrue();
        var command = new TrackOutCommand(
            "PLANT01", lot, "EQ01", 10m,
            [new DefectEntry(defectA, 1m), new DefectEntry(defectB, 2m)],
            null, "operator", 2, $"TO-DEF:{lot}", "POP", "KIOSK-DEF-01");

        var first = await service.TrackOutAsync(command);
        Exec("UPDATE QMS_DEFECT_CLASS SET IS_ACTIVE=0 WHERE DEFECT_CLASS_ID IN (@a, @b);",
            ("@a", defectA), ("@b", defectB));
        var replay = await service.TrackOutAsync(command);

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue(
            "an exact committed retry must resolve before consulting subsequently disabled master data");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_DEFECT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot))
            .Should().Be(2);
        Scalar<decimal>("SELECT SUM(DEFECT_QTY) FROM POM_LOT_DEFECT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot))
            .Should().Be(3m);
        Scalar<long>("""
            SELECT COUNT(*) FROM POM_LOT_DEFECT_EXECUTION d
            JOIN POM_LOT_EXECUTION e ON e.EXECUTION_ID=d.EXECUTION_ID
            WHERE d.LOT_ID=@lot AND e.ACTION='TrackOut'
              AND d.PROCESS_ID=@process AND d.EXECUTION_USER='operator'
              AND d.CLIENT_CHANNEL='POP' AND d.DEVICE_ID='KIOSK-DEF-01'
            """, ("@lot", lot), ("@process", process)).Should().Be(2);
        Scalar<long>("SELECT COUNT(DISTINCT EXECUTION_ID) FROM POM_LOT_DEFECT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot))
            .Should().Be(1);
    }

    [Fact]
    public async Task Later_step_cumulative_defect_violation_is_rejected_before_database_update()
    {
        var (lot, _) = SeedReleasedWorkOrderLot();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var firstDefect = $"D_C1_{suffix}";
        var secondDefect = $"D_C2_{suffix}";
        var process10 = $"C10_{suffix}";
        var process20 = $"C20_{suffix}";
        SeedDefectClasses(firstDefect, secondDefect);
        Exec("""
            UPDATE POM_LOT SET ROUTE_STEPS=@route WHERE LOT_ID=@lot;
            UPDATE POM_WORK_ORDER SET PROCESS_ID=NULL WHERE WORK_ORDER_ID=(
                SELECT WORK_ORDER_ID FROM POM_LOT WHERE LOT_ID=@lot);
            """, ("@route", $"{process10}>{process20}"), ("@lot", lot));
        var service = BuildService();

        (await service.TrackInAsync(new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator", 1, $"TI-C1:{lot}")))
            .IsSuccess.Should().BeTrue();
        (await service.TrackOutAsync(new TrackOutCommand(
            "PLANT01", lot, "EQ01", 6m, [new DefectEntry(firstDefect, 4m)], null,
            "operator", 2, $"TO-C1:{lot}")))
            .IsSuccess.Should().BeTrue();
        (await service.TrackInAsync(new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator", 3, $"TI-C2:{lot}")))
            .IsSuccess.Should().BeTrue();

        var invalid = await service.TrackOutAsync(new TrackOutCommand(
            "PLANT01", lot, "EQ01", 5m, [new DefectEntry(secondDefect, 2m)], null,
            "operator", 4, $"TO-C2:{lot}"));

        invalid.IsFailure.Should().BeTrue("4 accumulated + 2 new defects exceeds the current quantity 5");
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("Processing");
        Scalar<decimal>("SELECT QTY FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(6m);
        Scalar<decimal>("SELECT DEFECT_QTY FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(4m);
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(4);
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_DEFECT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(1);
    }

    [Fact]
    public async Task Defect_detail_insert_failure_rolls_back_lot_execution_and_work_order()
    {
        var (lot, workOrder) = SeedReleasedWorkOrderLot();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var defect = $"D_RB_{suffix}";
        var process = $"DRB_{suffix}";
        SeedDefectClasses(defect);
        Exec("""
            UPDATE POM_LOT SET ROUTE_STEPS=@process WHERE LOT_ID=@lot;
            UPDATE POM_WORK_ORDER SET PROCESS_ID=@process WHERE WORK_ORDER_ID=@workOrder;
            """, ("@process", process), ("@lot", lot), ("@workOrder", workOrder));
        var service = BuildService();
        (await service.TrackInAsync(new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator", 1, $"TI-DRB:{lot}")))
            .IsSuccess.Should().BeTrue();
        Exec("""
            CREATE TRIGGER TR_TEST_REJECT_LOT_DEFECT
            BEFORE INSERT ON POM_LOT_DEFECT_EXECUTION
            BEGIN SELECT RAISE(ABORT, 'forced defect detail failure'); END;
            """);

        try
        {
            var act = () => service.TrackOutAsync(new TrackOutCommand(
                "PLANT01", lot, "EQ01", 10m, [new DefectEntry(defect, 1m)], null,
                "operator", 2, $"TO-DRB:{lot}"));
            await act.Should().ThrowAsync<SqliteException>();
        }
        finally
        {
            Exec("DROP TRIGGER IF EXISTS TR_TEST_REJECT_LOT_DEFECT;");
        }

        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("Processing");
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot AND ACTION='TrackOut'", ("@lot", lot))
            .Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_DEFECT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(0);
        Scalar<string>("SELECT STATUS FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be("Started");
    }

    [Fact]
    public async Task History_failure_rolls_back_lot_work_order_and_execution()
    {
        var (lot, workOrder) = SeedReleasedWorkOrderLot();
        var key = $"ROLLBACK:{lot}";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Exec("""
            INSERT INTO POM_LOT_HISTORY
              (PLANT_ID, LOT_ID, PROCESS_ID, EXECUTION_ID, EXECUTION_USER, QTY, DEFECT_QTY,
               LOT_STATE, PROCESS_STATE, IDEMPOTENCY_KEY, CREATED_AT)
            VALUES ('PLANT01', @lot, 'CUT', 'Hold', 'PRE', 10, 0, 'Queued', 'Idle', @key, @now);
            """, ("@lot", lot), ("@key", key), ("@now", now));

        var act = () => BuildService().TrackInAsync(new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator", ExpectedVersion: 1, IdempotencyKey: key));
        await act.Should().ThrowAsync<SqliteException>();

        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("Queued");
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(1);
        Scalar<string>("SELECT STATUS FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be("Released");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be(0);
    }

    [Fact]
    public async Task Final_track_out_requires_all_active_process_specs_to_be_confirmed_pass()
    {
        var (lot, workOrder) = SeedReleasedWorkOrderLot();
        var service = BuildService();
        var gate = QualityGateway(DataSource());
        var trackIn = await service.TrackInAsync(new TrackInCommand(
            "PLANT01", lot, "EQ01", null, null, "operator", 1, $"TI-QG:{lot}"));
        trackIn.IsSuccess.Should().BeTrue(trackIn.IsFailure ? trackIn.Error.Description : string.Empty);

        var notRequired = await gate.EvaluateAsync(lot, "CUT", workOrder);
        notRequired.Status.Should().Be(ProductionQualityStatus.NotRequired);
        notRequired.AllowsCompletion.Should().BeTrue();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var specA = $"QGA_{suffix}";
        var specB = $"QGB_{suffix}";
        var inspectionA = $"QIA_{suffix}";
        var inspectionB = $"QIB_{suffix}";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Exec("""
            INSERT INTO QMS_INSPECTION_SPEC
              (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, IS_ACTIVE,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@specA, @specA, 'CUT', 'Cut dimension', 'Attribute', 1, 'TEST', @now, 'TEST', @now);
            INSERT INTO QMS_INSPECTION_SPEC
              (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, IS_ACTIVE,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@specB, @specB, 'CUT', 'Cut appearance', 'Attribute', 1, 'TEST', @now, 'TEST', @now);
            """, ("@specA", specA), ("@specB", specB), ("@now", now));

        var pending = await gate.EvaluateAsync(lot, "CUT", workOrder);
        pending.Status.Should().Be(ProductionQualityStatus.Pending);
        pending.RequiredSpecCount.Should().Be(2);

        var command = new TrackOutCommand(
            "PLANT01", lot, "EQ01", 10, null, null, "operator", 2, $"TO-QG:{lot}",
            "POP", "KIOSK-01");
        var blockedWithoutInspection = await service.TrackOutAsync(command);
        blockedWithoutInspection.IsFailure.Should().BeTrue();
        blockedWithoutInspection.Error.Description.Should().Contain("Pending");
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("Processing");
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(2);
        Scalar<string>("SELECT STATUS FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be("Started");

        Exec("""
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, SPEC_ID, INSPECTED_AT,
               INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspectionA, 'Process', @lot, 'EQ01', @specA, @now,
                    'admin', 'Pass', 1, 0, 1, 'TEST', @now, 'TEST', @now);
            INSERT INTO QMS_INSPECTION_RESULT
              (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, ATTRIBUTE_RESULT,
               INSPECTED_AT, INSPECTOR_ID, IS_PASS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspectionA, @inspectionA, @specA, @lot, 'EQ01', 'Pass',
                    @now, 'admin', 1, 'TEST', @now, 'TEST', @now);
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, SPEC_ID, INSPECTED_AT,
               INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspectionB, 'Process', @lot, 'EQ01', @specB, @now,
                    'admin', 'Pass', 1, 0, 0, 'TEST', @now, 'TEST', @now);
            INSERT INTO QMS_INSPECTION_RESULT
              (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, ATTRIBUTE_RESULT,
               INSPECTED_AT, INSPECTOR_ID, IS_PASS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspectionB, @inspectionB, @specB, @lot, 'EQ01', 'Pass',
                    @now, 'admin', 1, 'TEST', @now, 'TEST', @now);
            """, ("@inspectionA", inspectionA), ("@inspectionB", inspectionB),
            ("@specA", specA), ("@specB", specB), ("@lot", lot), ("@now", now));

        (await gate.EvaluateAsync(lot, "CUT", workOrder)).Status.Should().Be(ProductionQualityStatus.Pending,
            "an unconfirmed inspection must not release production");

        Exec("""
            UPDATE QMS_INSPECTION SET IS_CONFIRMED=1, RESULT='Fail', DEFECT_QTY=1
             WHERE INSPECTION_ID=@inspection;
            UPDATE QMS_INSPECTION_RESULT SET IS_PASS=0, ATTRIBUTE_RESULT='Fail'
             WHERE INSPECTION_ID=@inspection;
            """, ("@inspection", inspectionB));
        (await gate.EvaluateAsync(lot, "CUT", workOrder)).Status.Should().Be(ProductionQualityStatus.Failed);
        (await service.TrackOutAsync(command)).IsFailure.Should().BeTrue("a confirmed failure must block completion");

        Exec("""
            UPDATE QMS_INSPECTION SET RESULT='Pass', DEFECT_QTY=0 WHERE INSPECTION_ID=@inspection;
            UPDATE QMS_INSPECTION_RESULT SET IS_PASS=1, ATTRIBUTE_RESULT='Pass'
             WHERE INSPECTION_ID=@inspection;
            """, ("@inspection", inspectionB));
        var approved = await gate.EvaluateAsync(lot, "CUT", workOrder);
        approved.Status.Should().Be(ProductionQualityStatus.Passed);
        approved.PassedSpecCount.Should().Be(2);

        var completed = await service.TrackOutAsync(command);
        completed.IsSuccess.Should().BeTrue(completed.IsFailure ? completed.Error.Description : string.Empty);
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be("Completed");
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(3);
        Scalar<string>("SELECT STATUS FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder)).Should().Be("Completed");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot AND ACTION='TrackOut'", ("@lot", lot))
            .Should().Be("POP");
        Scalar<string>("SELECT DEVICE_ID FROM POM_LOT_EXECUTION WHERE LOT_ID=@lot AND ACTION='TrackOut'", ("@lot", lot))
            .Should().Be("KIOSK-01");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo AND ACTION='Complete'", ("@wo", workOrder))
            .Should().Be("POP");
        Scalar<string>("SELECT DEVICE_ID FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo AND ACTION='Complete'", ("@wo", workOrder))
            .Should().Be("KIOSK-01");
        Scalar<long>("SELECT EXPECTED_VERSION FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo AND ACTION='Complete'", ("@wo", workOrder))
            .Should().Be(2);
        Scalar<long>("SELECT RESULT_VERSION FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo AND ACTION='Complete'", ("@wo", workOrder))
            .Should().Be(3);
    }

    [Fact]
    public async Task Direct_work_order_complete_reuses_lot_quality_gate_and_persists_only_after_pass()
    {
        var (lot, workOrder) = SeedReleasedWorkOrderLot();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var process = $"DIRECT_{suffix}";
        var spec = $"QGD_{suffix}";
        var inspection = $"QID_{suffix}";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Exec("""
            UPDATE POM_WORK_ORDER
               SET STATUS='Started', START_QTY=10, VERSION_NO=2, UPDATED_BY='TEST', UPDATED_AT=@now
             WHERE WORK_ORDER_ID=@workOrder;
            UPDATE POM_LOT
               SET LOT_STATE='Completed', PROCESS_STATE='Idle', ROUTE_STEPS=@process,
                   CURRENT_STEP=0, DEFECT_QTY=2, IS_HOLD='N', VERSION_NO=2,
                   UPDATED_BY='TEST', UPDATED_AT=@now
             WHERE LOT_ID=@lot;
            INSERT INTO QMS_INSPECTION_SPEC
              (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, IS_ACTIVE,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@spec, @spec, @process, 'Direct completion evidence', 'Attribute', 1,
                    'TEST', @now, 'TEST', @now);
            """, ("@workOrder", workOrder), ("@lot", lot), ("@process", process),
            ("@spec", spec), ("@now", now));

        var service = BuildWorkOrderService();
        var context = new PomWorkOrderOperationContext(
            "operator", "MES", $"DIRECT-COMPLETE:{workOrder}", 2, Remark: "manual close");

        var missingInspection = await service.CompleteAsync(workOrder, 8m, 2m, context);

        missingInspection.IsFailure.Should().BeTrue();
        missingInspection.Error.Description.Should().Contain("Pending").And.Contain(lot);
        Scalar<string>("SELECT STATUS FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be("Started");
        Scalar<long>("SELECT COUNT(*) FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo AND ACTION='Complete'",
            ("@wo", workOrder)).Should().Be(0);

        Exec("""
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, SPEC_ID, INSPECTED_AT,
               INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspection, 'Process', @lot, 'EQ01', @spec, @now,
                    'admin', 'Pass', 1, 0, 1, 'TEST', @now, 'TEST', @now);
            INSERT INTO QMS_INSPECTION_RESULT
              (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, ATTRIBUTE_RESULT,
               INSPECTED_AT, INSPECTOR_ID, IS_PASS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspection, @inspection, @spec, @lot, 'EQ01', 'Pass',
                    @now, 'admin', 1, 'TEST', @now, 'TEST', @now);
            """, ("@inspection", inspection), ("@spec", spec), ("@lot", lot), ("@now", now));

        var completed = await service.CompleteAsync(workOrder, 8m, 2m, context);

        completed.IsSuccess.Should().BeTrue(completed.IsFailure ? completed.Error.Description : string.Empty);
        Scalar<string>("SELECT STATUS FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be("Completed");
        Scalar<decimal>("SELECT COMPLETE_QTY FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be(8m);
        Scalar<decimal>("SELECT SCRAP_QTY FROM POM_WORK_ORDER WHERE WORK_ORDER_ID=@wo", ("@wo", workOrder))
            .Should().Be(2m);
        Scalar<long>("SELECT COUNT(*) FROM POM_WORK_ORDER_EXECUTION WHERE WORK_ORDER_ID=@wo AND ACTION='Complete'",
            ("@wo", workOrder)).Should().Be(1);
    }

    [Fact]
    public async Task Flexible_bypass_atomically_consumes_exception_and_persists_route_audit()
    {
        var (lot, _) = SeedReleasedWorkOrderLot();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var process10 = $"R10_{suffix}";
        var process20 = $"R20_{suffix}";
        var process30 = $"R30_{suffix}";
        Exec("""
            UPDATE POM_LOT
               SET ROUTE_STEPS=@route, CURRENT_STEP=0, CONTROL_MODE='Flexible', RETURN_STEP=NULL
             WHERE LOT_ID=@lot;
            """, ("@route", $"{process10}>{process20}>{process30}"), ("@lot", lot));

        var service = BuildService();
        var exceptionId = $"EX_{suffix}";
        var requested = await service.RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            exceptionId, "PLANT01", lot, RouteDeviationType.Bypass, 2,
            "approved maintenance bypass", "operator", 1, DateTime.UtcNow.AddHours(1),
            "POP", "KIOSK-01"));
        requested.IsSuccess.Should().BeTrue(requested.IsFailure ? requested.Error.Description : string.Empty);

        var approved = await service.ApproveRouteExceptionAsync(new ReviewRouteExceptionCommand(
            exceptionId, "supervisor", "verified", "MOBILE", "PDA-SUP-01"));
        approved.IsSuccess.Should().BeTrue(approved.IsFailure ? approved.Error.Description : string.Empty);

        var applied = await service.ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "PLANT01", lot, RouteDeviationType.Bypass, 2,
            "approved maintenance bypass", "operator", 1, $"ROUTE:{suffix}",
            exceptionId, "POP", "KIOSK-01"));

        applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.Description : string.Empty);
        Scalar<long>("SELECT CURRENT_STEP FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(2);
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(2);
        Scalar<string>("SELECT STATUS FROM POM_ROUTE_EXCEPTION WHERE EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be("Applied");
        Scalar<string>("SELECT REVIEW_CLIENT_CHANNEL FROM POM_ROUTE_EXCEPTION WHERE EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be("MOBILE");
        Scalar<string>("SELECT REVIEW_DEVICE_ID FROM POM_ROUTE_EXCEPTION WHERE EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be("PDA-SUP-01");
        Scalar<string>("SELECT ACTION FROM POM_LOT_EXECUTION WHERE ROUTE_EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be("Bypass");
        Scalar<long>("SELECT FROM_STEP FROM POM_LOT_EXECUTION WHERE ROUTE_EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be(0);
        Scalar<long>("SELECT TO_STEP FROM POM_LOT_EXECUTION WHERE ROUTE_EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be(2);
        Scalar<string>("SELECT FROM_PROCESS_ID FROM POM_LOT_EXECUTION WHERE ROUTE_EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be(process10);
        Scalar<string>("SELECT TO_PROCESS_ID FROM POM_LOT_EXECUTION WHERE ROUTE_EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be(process30);
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_LOT_EXECUTION WHERE ROUTE_EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be("POP");
    }

    [Fact]
    public async Task Route_history_insert_failure_rolls_back_lot_execution_and_exception_consumption()
    {
        var (lot, _) = SeedReleasedWorkOrderLot();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var process10 = $"RB10_{suffix}";
        var process20 = $"RB20_{suffix}";
        Exec("""
            UPDATE POM_LOT
               SET ROUTE_STEPS=@route, CURRENT_STEP=0, CONTROL_MODE='Flexible', RETURN_STEP=NULL
             WHERE LOT_ID=@lot;
            """, ("@route", $"{process10}>{process20}"), ("@lot", lot));

        var service = BuildService();
        var exceptionId = $"EX_RB_{suffix}";
        (await service.RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            exceptionId, "PLANT01", lot, RouteDeviationType.Bypass, 1,
            "rollback proof", "operator", 1, DateTime.UtcNow.AddHours(1))))
            .IsSuccess.Should().BeTrue();
        (await service.ApproveRouteExceptionAsync(new ReviewRouteExceptionCommand(
            exceptionId, "supervisor"))).IsSuccess.Should().BeTrue();

        var key = $"ROUTE-ROLLBACK:{suffix}";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Exec("""
            INSERT INTO POM_LOT_HISTORY
              (PLANT_ID, LOT_ID, PROCESS_ID, EXECUTION_ID, EXECUTION_USER, QTY, DEFECT_QTY,
               LOT_STATE, PROCESS_STATE, IDEMPOTENCY_KEY, CREATED_AT)
            VALUES ('PLANT01', @lot, @process, 'Hold', 'PRE', 10, 0,
                    'Queued', 'Idle', @key, @now);
            """, ("@lot", lot), ("@process", process10), ("@key", key), ("@now", now));

        var act = () => service.ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "PLANT01", lot, RouteDeviationType.Bypass, 1,
            "rollback proof", "operator", 1, key, exceptionId));
        await act.Should().ThrowAsync<SqliteException>();

        Scalar<long>("SELECT CURRENT_STEP FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(0);
        Scalar<long>("SELECT VERSION_NO FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lot)).Should().Be(1);
        Scalar<string>("SELECT STATUS FROM POM_ROUTE_EXCEPTION WHERE EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be("Approved");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_EXECUTION WHERE ROUTE_EXCEPTION_ID=@id", ("@id", exceptionId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Hold_records_reason_and_database_rejects_orphans_and_boundary_mismatch()
    {
        var (lot, workOrder) = SeedReleasedWorkOrderLot();
        var service = BuildService();
        var key = $"HOLD:{lot}";
        var held = await service.HoldAsync(
            lot, "supervisor", expectedVersion: 1, idempotencyKey: key,
            reason: "quality containment", clientChannel: "MOBILE", deviceId: "PDA-HOLD-01");
        var exactReplay = await service.HoldAsync(
            lot, "supervisor", expectedVersion: 1, idempotencyKey: key,
            reason: "quality containment", clientChannel: "MOBILE", deviceId: "PDA-HOLD-01");
        var crossUser = await service.HoldAsync(
            lot, "other-supervisor", expectedVersion: 1, idempotencyKey: key,
            reason: "quality containment", clientChannel: "MOBILE", deviceId: "PDA-HOLD-01");
        var crossChannel = await service.HoldAsync(
            lot, "supervisor", expectedVersion: 1, idempotencyKey: key,
            reason: "quality containment", clientChannel: "POP", deviceId: "PDA-HOLD-01");

        held.IsSuccess.Should().BeTrue(held.IsFailure ? held.Error.Description : string.Empty);
        exactReplay.IsSuccess.Should().BeTrue();
        crossUser.IsFailure.Should().BeTrue("the authenticated actor is part of idempotency identity");
        crossChannel.IsFailure.Should().BeTrue("the shop-floor channel is part of idempotency identity");
        Scalar<string>("SELECT REASON FROM POM_LOT_HISTORY WHERE LOT_ID=@lot AND EXECUTION_ID='Hold'", ("@lot", lot))
            .Should().Be("quality containment");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_LOT_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("MOBILE");
        Scalar<string>("SELECT DEVICE_ID FROM POM_LOT_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("PDA-HOLD-01");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);

        var releaseKey = $"RELEASE-HOLD:{lot}";
        var released = await service.ReleaseHoldAsync(
            lot, "supervisor", expectedVersion: 2, idempotencyKey: releaseKey,
            reason: "containment cleared", clientChannel: "POP", deviceId: "KIOSK-REL-01");
        var releaseCrossChannel = await service.ReleaseHoldAsync(
            lot, "supervisor", expectedVersion: 2, idempotencyKey: releaseKey,
            reason: "containment cleared", clientChannel: "MES", deviceId: "KIOSK-REL-01");
        released.IsSuccess.Should().BeTrue(released.IsFailure ? released.Error.Description : string.Empty);
        releaseCrossChannel.IsFailure.Should().BeTrue();
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_LOT_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", releaseKey))
            .Should().Be("POP");
        Scalar<string>("SELECT DEVICE_ID FROM POM_LOT_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", releaseKey))
            .Should().Be("KIOSK-REL-01");

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var badLot = $"BAD_{Guid.NewGuid():N}";
        Action wrongBoundary = () => Exec("""
            INSERT INTO POM_LOT
              (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE,
               PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT)
            VALUES (@bad, 'OTHER', @wo, 'ITEM01', 1, 0, 'Queued', 'Idle', 'CUT', 0, 'N', 1, 'TEST', @now);
            """, ("@bad", badLot), ("@wo", workOrder), ("@now", now));
        wrongBoundary.Should().Throw<SqliteException>();

        Action orphanHistory = () => Exec("""
            INSERT INTO POM_LOT_HISTORY
              (PLANT_ID, LOT_ID, PROCESS_ID, EXECUTION_ID, EXECUTION_USER, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE)
            VALUES ('PLANT01', 'NO_SUCH_LOT', 'CUT', 'TrackIn', 'TEST', 1, 0, 'Queued', 'Idle');
            """);
        orphanHistory.Should().Throw<SqliteException>();

        Action invalidMixing = () => Exec("""
            INSERT INTO POM_LOT_MIXING_RELATION
              (PLANT_ID, OUTPUT_LOT_ID, INPUT_LOT_ID, INPUT_QTY, MIXING_RATE, CONSUMED_AT, CONSUMED_BY)
            VALUES ('PLANT01', @lot, @lot, 0, 0, @now, 'TEST');
            """, ("@lot", lot), ("@now", now));
        invalidMixing.Should().Throw<SqliteException>();
    }

    [Fact]
    public async Task Work_order_idempotency_binds_actor_channel_device_remark_and_version()
    {
        var (_, workOrder) = SeedReleasedWorkOrderLot();
        var service = BuildWorkOrderService();
        var key = $"WO-HOLD:{workOrder}";
        var exact = new PomWorkOrderOperationContext(
            "supervisor", "MOBILE", key, 1, "PDA-WO-01", "quality containment");

        var first = await service.HoldAsync(workOrder, exact);
        var replay = await service.HoldAsync(workOrder, exact);
        var crossUser = await service.HoldAsync(workOrder, exact with { User = "other" });
        var crossChannel = await service.HoldAsync(workOrder, exact with { ClientChannel = "POP" });
        var crossDevice = await service.HoldAsync(workOrder, exact with { DeviceId = "PDA-WO-02" });
        var changedRemark = await service.HoldAsync(workOrder, exact with { Remark = "different" });
        var changedVersion = await service.HoldAsync(workOrder, exact with { ExpectedVersion = 2 });

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue();
        crossUser.IsFailure.Should().BeTrue();
        crossChannel.IsFailure.Should().BeTrue();
        crossDevice.IsFailure.Should().BeTrue();
        changedRemark.IsFailure.Should().BeTrue();
        changedVersion.IsFailure.Should().BeTrue();
        Scalar<long>("SELECT COUNT(*) FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<long>("SELECT EXPECTED_VERSION FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<long>("SELECT RESULT_VERSION FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(2);
        Scalar<string>("SELECT USER_ID FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("supervisor");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("MOBILE");
        Scalar<string>("SELECT DEVICE_ID FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("PDA-WO-01");
        Scalar<string>("SELECT REMARK FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("quality containment");
    }
}
