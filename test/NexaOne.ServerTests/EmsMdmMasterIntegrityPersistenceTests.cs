using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Application.Tools;
using NexaOne.EMS.Domain;
using NexaOne.EMS.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ems;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>V124 MDM evidence and V125 EMS maintenance/spare/tool integrity on the real SQLite adapter.</summary>
public sealed class EmsMdmMasterIntegrityPersistenceTests :
    IClassFixture<EmsMdmMasterIntegrityPersistenceTests.IntegrityFactory>
{
    private readonly IntegrityFactory _factory;

    public EmsMdmMasterIntegrityPersistenceTests(IntegrityFactory factory) => _factory = factory;

    public sealed class IntegrityFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-ems-mdm-integrity-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False;Default Timeout=10";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "ems-mdm-integrity-test-secret-key-at-least-32-bytes");
            builder.UseSetting("Jwt:Issuer", "ems-mdm-integrity-test");
            builder.UseSetting("Jwt:Audience", "ems-mdm-integrity-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Equipment_master_changes_append_authenticated_before_after_snapshots()
    {
        var suffix = Suffix();
        var equipmentId = $"EQ-HIST-{suffix}";
        var repository = new EquipmentRepository(DataSource(), Configuration());
        var service = new EquipmentService(repository);
        var previous = CurrentUserContext.UserId;
        try
        {
            CurrentUserContext.UserId = "logged-maintainer";
            var created = await service.CreateEquipmentAsync(
                equipmentId, "Cleaner A", "PLANT-01", "AREA-01", "Cleaner");
            var updated = await service.UpdateEquipmentAsync(
                equipmentId, "Cleaner B", "bath module", "Cleaner", "Nexa", "NX-1");
            var deactivated = await service.DeactivateEquipmentAsync(equipmentId);

            created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
            updated.IsSuccess.Should().BeTrue(updated.IsFailure ? updated.Error.Description : string.Empty);
            deactivated.IsSuccess.Should().BeTrue(deactivated.IsFailure ? deactivated.Error.Description : string.Empty);
        }
        finally
        {
            CurrentUserContext.UserId = previous;
        }

        Scalar<long>(
                "SELECT COUNT(*) FROM MDM_EQUIPMENT_CHANGE_HISTORY WHERE EQUIPMENT_ID=@id",
                ("@id", equipmentId))
            .Should().Be(3);
        Scalar<long>(
                "SELECT COUNT(*) FROM MDM_EQUIPMENT_CHANGE_HISTORY WHERE EQUIPMENT_ID=@id AND ACTOR_ID='logged-maintainer'",
                ("@id", equipmentId))
            .Should().Be(3);
        Scalar<string>(
                "SELECT CHANGE_TYPE FROM MDM_EQUIPMENT_CHANGE_HISTORY WHERE EQUIPMENT_ID=@id ORDER BY CHANGED_AT, ROWID LIMIT 1",
                ("@id", equipmentId))
            .Should().Be("Create");
        Scalar<string>(
                "SELECT AFTER_STATE_JSON FROM MDM_EQUIPMENT_CHANGE_HISTORY WHERE EQUIPMENT_ID=@id AND CHANGE_TYPE='Update'",
                ("@id", equipmentId))
            .Should().Contain("Cleaner B").And.Contain("bath module");
        Scalar<string>(
                "SELECT BEFORE_STATE_JSON FROM MDM_EQUIPMENT_CHANGE_HISTORY WHERE EQUIPMENT_ID=@id AND CHANGE_TYPE='Deactivate'",
                ("@id", equipmentId))
            .Should().Contain("\"ValidState\":\"Valid\"");
        Scalar<string>(
                "SELECT AFTER_STATE_JSON FROM MDM_EQUIPMENT_CHANGE_HISTORY WHERE EQUIPMENT_ID=@id AND CHANGE_TYPE='Deactivate'",
                ("@id", equipmentId))
            .Should().Contain("\"ValidState\":\"Invalid\"");

        Action mutateHistory = () => Execute(
            "UPDATE MDM_EQUIPMENT_CHANGE_HISTORY SET ACTOR_ID='tampered' WHERE EQUIPMENT_ID=@id",
            ("@id", equipmentId));
        mutateHistory.Should().Throw<SqliteException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task Work_order_plan_must_match_equipment_and_pm_bm_type_in_service_and_repository()
    {
        var suffix = Suffix();
        var equipmentA = $"EQ-WO-A-{suffix}";
        var equipmentB = $"EQ-WO-B-{suffix}";
        var pmPlan = $"PLAN-PM-{suffix}";
        var bmPlan = $"PLAN-BM-{suffix}";
        SeedEquipment(equipmentA);
        SeedEquipment(equipmentB);
        SeedPlan(pmPlan, equipmentA, "PM");
        SeedPlan(bmPlan, equipmentA, "BM");
        var plans = new MaintenancePlanRepository(DataSource(), Configuration());
        var workOrders = new WorkOrderRepository(DataSource(), Configuration());
        var service = new EmsService(workOrders, plans);

        var wrongEquipment = await service.CreateWorkOrderAsync(
            $"WO-SVC-EQ-{suffix}", equipmentB, "PM", "mismatch", "maint", pmPlan,
            Command($"wo-svc-eq:{suffix}"));
        var wrongType = await service.CreateWorkOrderAsync(
            $"WO-SVC-TYPE-{suffix}", equipmentA, "PM", "mismatch", "maint", bmPlan,
            Command($"wo-svc-type:{suffix}"));
        var valid = await service.CreateWorkOrderAsync(
            $"WO-VALID-{suffix}", equipmentA, "PM", "monthly PM", "maint", pmPlan,
            Command($"wo-valid:{suffix}"));

        wrongEquipment.IsFailure.Should().BeTrue();
        wrongEquipment.Error.Code.Should().Be("EMS.WorkOrder.PlanEquipmentMismatch");
        wrongType.IsFailure.Should().BeTrue();
        wrongType.Error.Code.Should().Be("EMS.WorkOrder.PlanTypeMismatch");
        valid.IsSuccess.Should().BeTrue(valid.IsFailure ? valid.Error.Description : string.Empty);

        var bypassId = $"WO-REPO-{suffix}";
        var bypass = WorkOrder.Create(
            bypassId, equipmentB, "PM", "repository bypass", "maint", DateTime.UtcNow, pmPlan).Value;
        var persisted = await workOrders.AddWithActionAsync(
            bypass,
            new MaintenanceAction(
                $"ACT-{suffix}", bypassId, "Create", null, "Issued", "maint",
                $"wo-repo:{suffix}", DateTime.UtcNow));
        persisted.Should().BeFalse();
        Scalar<long>("SELECT COUNT(*) FROM EMS_WORK_ORDER WHERE WO_ID=@id", ("@id", bypassId))
            .Should().Be(0);

        Action directBypass = () => Execute(@"
            INSERT INTO EMS_WORK_ORDER
              (WO_ID, MAINTENANCE_PLAN_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION,
               ASSIGNEE_ID, ISSUED_AT, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
              (@wo, @plan, @equipment, 'PM', 'direct bypass',
               'maint', CURRENT_TIMESTAMP, 'Issued', 'maint', CURRENT_TIMESTAMP, 'maint', CURRENT_TIMESTAMP)",
            ("@wo", $"WO-SQL-{suffix}"), ("@plan", pmPlan), ("@equipment", equipmentB));
        directBypass.Should().Throw<SqliteException>().WithMessage("*scope/type mismatch*");
    }

    [Fact]
    public async Task Spare_part_initial_stock_and_opening_ledger_commit_once_and_replay()
    {
        var suffix = Suffix();
        var partId = $"SP-OPEN-{suffix}";
        var zeroPartId = $"SP-ZERO-{suffix}";
        var key = $"part-open:{suffix}";
        var zeroKey = $"part-zero:{suffix}";
        var service = new MaintenancePlanService(
            new MaintenancePlanRepository(DataSource(), Configuration()),
            new SparePartRepository(DataSource()),
            new EquipmentDirectory(DataSource()));
        var command = Command(key, "MOBILE", "TABLET-01", "corr-opening");

        var first = await service.CreatePartAsync(
            partId, "Drive bearing", $"BR-{suffix}", "Drive bearing", "EA",
            12m, 2m, 30m, "RACK-A", null, command);
        var replay = await service.CreatePartAsync(
            partId, "Drive bearing", $"BR-{suffix}", "Drive bearing", "EA",
            12m, 2m, 30m, "RACK-A", null, command);
        var conflict = await service.CreatePartAsync(
            partId, "Changed bearing", $"BR-{suffix}", "Drive bearing", "EA",
            12m, 2m, 30m, "RACK-A", null, command);
        var zeroOpening = await service.CreatePartAsync(
            zeroPartId, "Reserve bearing", $"BR-ZERO-{suffix}", "Empty initial stock", "EA",
            0m, 0m, 30m, "RACK-Z", null, Command(zeroKey));

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue(replay.IsFailure ? replay.Error.Description : string.Empty);
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.SparePart.IdempotencyConflict");
        zeroOpening.IsSuccess.Should().BeTrue(
            zeroOpening.IsFailure ? zeroOpening.Error.Description : string.Empty);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(1);
        Scalar<string>("SELECT TRANSACTION_TYPE FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("Opening");
        Scalar<decimal>("SELECT BALANCE_BEFORE FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0m);
        Scalar<decimal>("SELECT BALANCE_AFTER FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(12m);
        Scalar<string>("SELECT PROCESSED_BY FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("logged-maintainer");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be("MOBILE");
        Scalar<decimal>("SELECT QUANTITY FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", zeroKey))
            .Should().Be(0m, "an empty spare master still needs an explicit opening-balance ledger row");
    }

    [Fact]
    public async Task Spare_part_master_failure_rolls_back_the_opening_ledger()
    {
        var suffix = Suffix();
        var partId = $"SP-ROLL-{suffix}";
        var key = $"part-roll:{suffix}";
        var trigger = $"TR_PART_MASTER_FAIL_{suffix}";
        Execute($@"
            CREATE TRIGGER {trigger}
            BEFORE INSERT ON EMS_SPARE_PART
            WHEN NEW.PART_ID = '{partId}'
            BEGIN SELECT RAISE(ABORT, 'forced spare-part master failure'); END;");
        var service = new MaintenancePlanService(
            new MaintenancePlanRepository(DataSource(), Configuration()),
            new SparePartRepository(DataSource()),
            new EquipmentDirectory(DataSource()));

        var act = () => service.CreatePartAsync(
            partId, "Rollback bearing", $"BR-{suffix}", "rollback", "EA",
            5m, 1m, 10m, "RACK-B", null, Command(key));

        await act.Should().ThrowAsync<SqliteException>().WithMessage("*forced spare-part master failure*");
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART WHERE PART_ID=@id", ("@id", partId))
            .Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM EMS_SPARE_PART_INOUT WHERE IDEMPOTENCY_KEY=@key", ("@key", key))
            .Should().Be(0, "the opening ledger and master are one transaction");
    }

    [Fact]
    public async Task Tool_mount_class_usage_time_and_inspection_summary_are_guarded()
    {
        var suffix = Suffix();
        var equipmentClass = $"EQC-{suffix}";
        var otherClass = $"EQC-X-{suffix}";
        var equipmentId = $"EQ-TOOL-{suffix}";
        var toolId = $"TOOL-{suffix}";
        SeedEquipmentClass(equipmentClass);
        SeedEquipmentClass(otherClass);
        SeedEquipment(equipmentId, equipmentClass);
        var repository = new ToolRepository(DataSource());
        var service = new ToolService(repository, new EquipmentDirectory(DataSource()));
        var mountedAt = new DateTime(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);
        (await service.SaveAsync(new ToolCommand(
            toolId, "Carrier fixture", "Fixture", EquipmentClassId: equipmentClass,
            InspectionCycleDays: 30, CalibrationCycleDays: 180, ActorId: "maint")))
            .IsSuccess.Should().BeTrue();
        var mount = await service.MountAsync(new ToolMountCommand(
            $"mount:{suffix}", toolId, equipmentId, mountedAt, "PORT-A", "maint"));
        mount.IsSuccess.Should().BeTrue(mount.IsFailure ? mount.Error.Description : string.Empty);

        var serviceClassChange = await service.SaveAsync(new ToolCommand(
            toolId, "Carrier fixture", "Fixture", EquipmentClassId: otherClass,
            InspectionCycleDays: 30, CalibrationCycleDays: 180,
            Status: "Mounted", ActorId: "maint"));
        var repositoryClassChange = await repository.TrySaveToolAsync(
            (await repository.GetToolAsync(toolId))! with { EquipmentClassId = otherClass },
            "Mounted", "maint");
        var earlyUsage = await service.RecordUsageAsync(new ToolUsageCommand(
            $"usage-early:{suffix}", toolId, equipmentId, 1m, 0m,
            mountedAt.AddSeconds(-1), MountId: mount.Value.MountId, ActorId: "operator"));

        serviceClassChange.IsFailure.Should().BeTrue();
        repositoryClassChange.Should().BeFalse();
        earlyUsage.IsFailure.Should().BeTrue();
        Scalar<string>("SELECT EQUIPMENT_CLASS_ID FROM EMS_TOOL WHERE TOOL_ID=@id", ("@id", toolId))
            .Should().Be(equipmentClass);
        Scalar<long>("SELECT COUNT(*) FROM EMS_TOOL_USAGE_HISTORY WHERE TOOL_ID=@id", ("@id", toolId))
            .Should().Be(0);

        Action directClassChange = () => Execute(
            "UPDATE EMS_TOOL SET EQUIPMENT_CLASS_ID=@class WHERE TOOL_ID=@tool",
            ("@class", otherClass), ("@tool", toolId));
        directClassChange.Should().Throw<SqliteException>().WithMessage("*equipment class is immutable*");
        Action directEarlyUsage = () => Execute(@"
            INSERT INTO EMS_TOOL_USAGE_HISTORY
              (USAGE_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, MOUNT_ID, EQUIPMENT_ID,
               USE_COUNT, USE_MINUTES, USED_AT, USED_BY, CREATED_BY, CREATED_AT)
            VALUES
              (@usage, @key, 'hash', @tool, @mount, @equipment,
               1, 0, @usedAt, 'operator', 'operator', CURRENT_TIMESTAMP)",
            ("@usage", $"TUS-{suffix}"), ("@key", $"direct-early:{suffix}"),
            ("@tool", toolId), ("@mount", mount.Value.MountId), ("@equipment", equipmentId),
            ("@usedAt", mountedAt.AddMinutes(-1)));
        directEarlyUsage.Should().Throw<SqliteException>().WithMessage("*cannot precede its mount*");

        var newerAt = mountedAt.AddDays(10);
        var olderAt = mountedAt.AddDays(5);
        var newer = await service.RecordInspectionAsync(new ToolInspectionCommand(
            $"inspection-new:{suffix}", toolId, "Inspection", "Pass", newerAt,
            NextDueAt: newerAt.AddDays(30), ActorId: "inspector-new"));
        var older = await service.RecordInspectionAsync(new ToolInspectionCommand(
            $"inspection-old:{suffix}", toolId, "Inspection", "Pass", olderAt,
            NextDueAt: olderAt.AddDays(30), ActorId: "inspector-old"));
        var newerCalibration = await service.RecordInspectionAsync(new ToolInspectionCommand(
            $"calibration-new:{suffix}", toolId, "Calibration", "Pass", newerAt,
            NextDueAt: newerAt.AddDays(180), ActorId: "calibrator-new"));
        var olderCalibration = await service.RecordInspectionAsync(new ToolInspectionCommand(
            $"calibration-old:{suffix}", toolId, "Calibration", "Pass", olderAt,
            NextDueAt: olderAt.AddDays(180), ActorId: "calibrator-old"));

        newer.IsSuccess.Should().BeTrue(newer.IsFailure ? newer.Error.Description : string.Empty);
        older.IsSuccess.Should().BeTrue(older.IsFailure ? older.Error.Description : string.Empty);
        newerCalibration.IsSuccess.Should().BeTrue(
            newerCalibration.IsFailure ? newerCalibration.Error.Description : string.Empty);
        olderCalibration.IsSuccess.Should().BeTrue(
            olderCalibration.IsFailure ? olderCalibration.Error.Description : string.Empty);
        var summary = await repository.GetToolAsync(toolId);
        summary!.LastInspectedAt.Should().Be(newerAt);
        summary.NextInspectionDueAt.Should().Be(newerAt.AddDays(30));
        summary.LastCalibratedAt.Should().Be(newerAt);
        summary.NextCalibrationDueAt.Should().Be(newerAt.AddDays(180));
        Scalar<long>("SELECT COUNT(*) FROM EMS_TOOL_INSPECTION_HISTORY WHERE TOOL_ID=@id", ("@id", toolId))
            .Should().Be(4, "backdated evidence remains append-only even when it cannot rewind either summary");
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

    private static IConfiguration Configuration() => new ConfigurationBuilder().Build();

    private static MaintenanceCommandContext Command(
        string key,
        string channel = "MES",
        string? device = null,
        string? correlation = null) => MaintenanceCommandContext.Create(
        "logged-maintainer", key, channel, device, correlation).Value;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SeedEquipment(string equipmentId, string equipmentClassId = "") => Execute(@"
        INSERT INTO MDM_EQUIPMENT
          (EQUIPMENT_ID, EQUIPMENT_NAME, DESCRIPTION, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
           EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
          (@id, @id, 'integrity test', 'PLANT-01', 'AREA-01', 'Cleaner',
           @class, 'Valid', 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", equipmentId), ("@class", equipmentClassId));

    private void SeedEquipmentClass(string equipmentClassId) => Execute(@"
        INSERT INTO MDM_EQUIPMENT_CLASS
          (EQUIPMENT_CLASS_ID, EQUIPMENT_CLASS_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
          (@id, @id, 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", equipmentClassId));

    private void SeedPlan(string planId, string equipmentId, string planType) => Execute(@"
        INSERT INTO EMS_MAINTENANCE_PLAN
          (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE, SCHEDULED_DATE,
           ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
           CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
          (@id, @id, @equipment, @type, 'Monthly', CURRENT_TIMESTAMP,
           1, 'maint', 'Planned', 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", planId), ("@equipment", equipmentId), ("@type", planType));

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
