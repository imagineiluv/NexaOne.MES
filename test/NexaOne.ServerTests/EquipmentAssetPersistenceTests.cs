using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.EMS.Application.Tools;
using NexaOne.EMS.Infrastructure;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Est;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>Tool/Utility/non-LOT output의 실제 SQLite 원장과 멱등 SQL을 검증한다.</summary>
public sealed class EquipmentAssetPersistenceTests : IClassFixture<EquipmentAssetPersistenceTests.AssetFactory>
{
    private readonly AssetFactory _factory;
    public EquipmentAssetPersistenceTests(AssetFactory factory) => _factory = factory;

    public sealed class AssetFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-assets-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "asset-persistence-test-key-at-least-32-bytes!!!");
            builder.UseSetting("Jwt:Issuer", "asset-test");
            builder.UseSetting("Jwt:Audience", "asset-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    private (EesDataSource DataSource, INexaOneEESDbCapability Dialect) Context()
    {
        _ = _factory.CreateClient();
        return (new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        }, _factory.Services.GetRequiredService<INexaOneEESDbCapability>());
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Carrier_output_is_persisted_once_for_an_idempotency_key()
    {
        var (ds, _) = Context();
        var service = new EquipmentOutputService(
            new EquipmentOutputRepository(ds), new EquipmentOutputMasterDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var command = new EquipmentOutputCommand(
            $"carrier:{suffix}", "PLANT01", "EQ01", "CarrierCleaned", 1m, 1m, 0m, "EA",
            DateTime.UtcNow, "EquipmentPlugin", SourceEventId: $"plc:{suffix}", CarrierId: "CR01", ActorId: "operator");

        var first = await service.RecordAsync(command);
        var replay = await service.RecordAsync(command);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.OutputEventId.Should().Be(first.Value.OutputEventId);
    }

    [Fact]
    public async Task Parallel_carrier_output_retries_converge_on_one_replay()
    {
        var (ds, _) = Context();
        var service = new EquipmentOutputService(
            new EquipmentOutputRepository(ds), new EquipmentOutputMasterDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var command = new EquipmentOutputCommand(
            $"carrier-race:{suffix}", "PLANT01", "EQ01", "CarrierCleaned", 1m, 1m, 0m, "EA",
            DateTime.UtcNow, "EquipmentPlugin", SourceEventId: $"plc-race:{suffix}",
            CarrierId: "CR01", ActorId: "operator");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RecordAsync(command)));

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.OutputEventId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Carrier_output_rejects_unknown_carrier_and_equipment_plant_mismatch()
    {
        var (ds, _) = Context();
        var service = new EquipmentOutputService(
            new EquipmentOutputRepository(ds), new EquipmentOutputMasterDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var command = new EquipmentOutputCommand(
            $"carrier-invalid:{suffix}", "PLANT02", "EQ01", "CarrierCleaned",
            1m, 1m, 0m, "EA", DateTime.UtcNow, "EquipmentPlugin",
            SourceEventId: $"plc-invalid:{suffix}", CarrierId: $"UNKNOWN-{suffix}", ActorId: "operator");

        var result = await service.RecordAsync(command);

        result.IsFailure.Should().BeTrue();
        Scalar<long>("SELECT COUNT(*) FROM EST_EQUIPMENT_OUTPUT_EVENT WHERE IDEMPOTENCY_KEY = @key",
            ("@key", command.IdempotencyKey)).Should().Be(0);
    }

    [Fact]
    public async Task Sqlite_output_repository_guard_rejects_master_scope_bypass()
    {
        var (ds, _) = Context();
        var repository = new EquipmentOutputRepository(ds);
        var now = DateTime.UtcNow;
        var wrongPlant = new EquipmentOutputRecord(
            $"OUT_{Guid.NewGuid():N}", $"guard:{Guid.NewGuid():N}", "hash",
            "PLANT02", "EQ01", "CarrierCleaned", "CR01", null, null, null, null, null,
            1m, 1m, 0m, "EA", "TEST", null, "operator", null, null, now, now);
        var unknownCarrier = wrongPlant with
        {
            OutputEventId = $"OUT_{Guid.NewGuid():N}",
            IdempotencyKey = $"guard:{Guid.NewGuid():N}",
            PlantId = "PLANT01",
            CarrierId = $"UNKNOWN-{Guid.NewGuid():N}",
        };

        Func<Task> wrongPlantWrite = () => repository.TryAddAsync(wrongPlant);
        Func<Task> unknownCarrierWrite = () => repository.TryAddAsync(unknownCarrier);

        await wrongPlantWrite.Should().ThrowAsync<SqliteException>();
        await unknownCarrierWrite.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Sqlite_output_repository_guard_rejects_carrier_cleaned_with_lot_semantics()
    {
        var (ds, _) = Context();
        var repository = new EquipmentOutputRepository(ds);
        var now = DateTime.UtcNow;
        var invalid = new EquipmentOutputRecord(
            $"OUT_{Guid.NewGuid():N}", $"carrier-lot:{Guid.NewGuid():N}", "hash",
            "PLANT01", "EQ01", "CarrierCleaned", "CR01", "LOT01", null, null, null, null,
            1m, 1m, 0m, "EA", "TEST", null, "operator", null, null, now, now,
            IsLotOutput: true);

        Func<Task> write = () => repository.TryAddAsync(invalid);

        await write.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Utility_readings_create_a_reproducible_period_summary()
    {
        var (ds, dialect) = Context();
        var service = new UtilityService(new UtilityRepository(ds, dialect));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"POWER-{suffix}";
        var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Power", "PLANT01", "Electricity", "kWh", "Cumulative", 0.1m,
            EquipmentId: "EQ01", CostPerUnit: 100m, CarbonPerUnit: 0.5m, ActorId: "maint",
            IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 1000m, "FDC", $"{suffix}:1", start, ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 1150m, "FDC", $"{suffix}:2", start.AddHours(1), ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();

        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            meter, "Hourly", start, start.AddHours(2), "maint"));

        summary.IsSuccess.Should().BeTrue();
        summary.Value.Consumption.Should().Be(15m);
        summary.Value.CostAmount.Should().Be(1500m);
        summary.Value.CarbonAmount.Should().Be(7.5m);
    }

    [Fact]
    public async Task Parallel_utility_retries_converge_on_one_reading()
    {
        var (ds, dialect) = Context();
        var service = new UtilityService(new UtilityRepository(ds, dialect));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"POWER-RACE-{suffix}";
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Power", "PLANT01", "Electricity", "kWh", "Delta",
            EquipmentId: "EQ01", ActorId: "maint", IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();
        var command = new UtilityReadingCommand(
            meter, 5m, "FDC", $"race:{suffix}", DateTime.UtcNow, ActorId: "SYSTEM");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RecordReadingAsync(command)));

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.ReadingId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Cumulative_utility_uses_the_last_good_reading_before_the_period_as_baseline()
    {
        var (ds, dialect) = Context();
        var service = new UtilityService(new UtilityRepository(ds, dialect));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"WATER-{suffix}";
        var start = new DateTime(2026, 8, 26, 6, 0, 0, DateTimeKind.Utc);
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Water", "PLANT01", "Water", "m3", "Cumulative",
            EquipmentId: "EQ01", ActorId: "maint", IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 100m, "FDC", $"{suffix}:baseline-good", start.AddHours(-2),
            Quality: "good", ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 900m, "FDC", $"{suffix}:baseline-bad", start.AddHours(-1),
            Quality: "Bad", ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 120m, "FDC", $"{suffix}:period-1", start.AddMinutes(30),
            Quality: "Good", ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 130m, "FDC", $"{suffix}:period-2", start.AddHours(1),
            Quality: "Good", ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();

        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            meter, "Hourly", start, start.AddHours(2), "maint"));

        summary.IsSuccess.Should().BeTrue();
        summary.Value.StartReading.Should().Be(100m);
        summary.Value.EndReading.Should().Be(130m);
        summary.Value.Consumption.Should().Be(30m);
    }

    [Fact]
    public async Task Cumulative_utility_prefers_a_good_reading_exactly_at_the_period_start()
    {
        var (ds, dialect) = Context();
        var service = new UtilityService(new UtilityRepository(ds, dialect));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"GAS-{suffix}";
        var start = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc);
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Gas", "PLANT01", "Gas", "Nm3", "Cumulative",
            EquipmentId: "EQ01", ActorId: "maint", IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 80m, "FDC", $"{suffix}:before", start.AddHours(-1),
            Quality: "Good", ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 100m, "FDC", $"{suffix}:boundary", start,
            Quality: "Good", ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 130m, "FDC", $"{suffix}:end", start.AddHours(1),
            Quality: "Good", ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();

        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            meter, "Hourly", start, start.AddHours(2), "maint"));

        summary.IsSuccess.Should().BeTrue();
        summary.Value.StartReading.Should().Be(100m);
        summary.Value.EndReading.Should().Be(130m);
        summary.Value.Consumption.Should().Be(30m);
    }

    [Fact]
    public async Task Utility_meter_event_history_preserves_audit_and_excludes_a_reset_jump()
    {
        var (ds, dialect) = Context();
        var repository = new UtilityRepository(ds, dialect);
        var service = new UtilityService(repository);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"RESET-{suffix}";
        var idempotencyKey = $"meter-event:{suffix}";
        var start = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Reset meter", "PLANT01", "Water", "m3", "Cumulative",
            EquipmentId: "EQ01", ActorId: "maint", IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 100m, "FDC", $"{suffix}:before", start, ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(new UtilityReadingCommand(
            meter, 30m, "FDC", $"{suffix}:after", start.AddHours(2), ActorId: "SYSTEM"))).IsSuccess.Should().BeTrue();
        var command = new UtilityMeterEventCommand(
            idempotencyKey, meter, "Reset", start.AddHours(1), "manual counter reset",
            PreviousValue: 150m, AfterValue: 0m, ActorId: "logged-in-maintainer");

        var first = await service.RecordMeterEventAsync(command);
        var replay = await service.RecordMeterEventAsync(command);
        var history = await service.GetMeterEventHistoryAsync(meter, start, start.AddHours(3));
        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            meter, "Shift", start, start.AddHours(3), "maint"));

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.EventId.Should().Be(first.Value.EventId);
        history.IsSuccess.Should().BeTrue();
        history.Value.Should().ContainSingle().Which.Should().Match<UtilityMeterEventRecord>(e =>
            e.ActorUserId == "logged-in-maintainer"
            && e.Reason == "manual counter reset"
            && e.PreviousValue == 150m
            && e.AfterValue == 0m
            && e.BaselineValue == null);
        summary.IsSuccess.Should().BeTrue();
        summary.Value.Consumption.Should().Be(80m);
        Scalar<long>("SELECT COUNT(*) FROM EST_UTILITY_METER_EVENT WHERE IDEMPOTENCY_KEY=@key",
            ("@key", idempotencyKey)).Should().Be(1);
        Scalar<string>("SELECT ACTOR_USER_ID FROM EST_UTILITY_METER_EVENT WHERE IDEMPOTENCY_KEY=@key",
            ("@key", idempotencyKey)).Should().Be("logged-in-maintainer");
        Scalar<string>("SELECT REASON FROM EST_UTILITY_METER_EVENT WHERE IDEMPOTENCY_KEY=@key",
            ("@key", idempotencyKey)).Should().Be("manual counter reset");
        Scalar<long>(@"SELECT COUNT(*) FROM sqlite_master
                       WHERE type='index' AND name='UX_EST_UTILITY_METER_EVENT_IDEMPOTENCY'")
            .Should().Be(1, "V122 must retain the SQLite concurrency guard");
    }

    [Fact]
    public async Task Parallel_utility_meter_event_retries_persist_one_history_row()
    {
        var (ds, dialect) = Context();
        var service = new UtilityService(new UtilityRepository(ds, dialect));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"EVENT-RACE-{suffix}";
        var idempotencyKey = $"event-race:{suffix}";
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Race meter", "PLANT01", "Electricity", "kWh", "Cumulative",
            EquipmentId: "EQ01", ActorId: "maint", IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();
        var command = new UtilityMeterEventCommand(
            idempotencyKey, meter, "Calibration", DateTime.UtcNow, "offset calibration",
            PreviousValue: 100m, AfterValue: 105m, ActorId: "calibrator");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RecordMeterEventAsync(command)));

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.EventId).Distinct().Should().ContainSingle();
        Scalar<long>("SELECT COUNT(*) FROM EST_UTILITY_METER_EVENT WHERE IDEMPOTENCY_KEY=@key",
            ("@key", idempotencyKey)).Should().Be(1);
    }

    [Fact]
    public async Task Utility_repository_event_guard_cannot_be_bypassed_for_a_delta_meter()
    {
        var (ds, dialect) = Context();
        var repository = new UtilityRepository(ds, dialect);
        var service = new UtilityService(repository);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"DELTA-EVENT-{suffix}";
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Delta meter", "PLANT01", "Electricity", "kWh", "Delta",
            EquipmentId: "EQ01", ActorId: "maint", IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();
        var now = DateTime.UtcNow;
        var record = new UtilityMeterEventRecord(
            $"UEV_{suffix}", $"delta-event:{suffix}", $"hash:{suffix}", meter,
            "PLANT01", "EQ01", "Reset", now, "repository bypass attempt",
            10m, 0m, null, "kWh", "maint", now);

        (await repository.TryAddMeterEventAsync(record)).Should().BeFalse();
        Scalar<long>("SELECT COUNT(*) FROM EST_UTILITY_METER_EVENT WHERE EVENT_ID=@eventId",
            ("@eventId", record.EventId)).Should().Be(0);
    }

    [Fact]
    public async Task Utility_v122_schema_rejects_invalid_event_shape_type_and_actor()
    {
        var (ds, dialect) = Context();
        var service = new UtilityService(new UtilityRepository(ds, dialect));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var meter = $"EVENT-SCHEMA-{suffix}";
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            meter, "Schema meter", "PLANT01", "Electricity", "kWh", "Cumulative",
            EquipmentId: "EQ01", ActorId: "maint", IdempotencyKey: $"meter-save:{suffix}"))).IsSuccess.Should().BeTrue();

        void InsertEvent(string eventId, string eventType, decimal? previous, decimal? after,
            decimal? baseline, string actor)
        {
            using var connection = new SqliteConnection(_factory.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO EST_UTILITY_METER_EVENT
                  (EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, METER_ID, PLANT_ID, EQUIPMENT_ID,
                   EVENT_TYPE, OCCURRED_AT, REASON, PREVIOUS_VALUE, AFTER_VALUE, BASELINE_VALUE,
                   UNIT, ACTOR_USER_ID, CREATED_AT)
                VALUES
                  (@eventId, @eventId, 'request-hash', @meter, 'PLANT01', 'EQ01',
                   @eventType, @at, 'schema guard', @previous, @after, @baseline,
                   'kWh', @actor, @at)";
            command.Parameters.AddWithValue("@eventId", eventId);
            command.Parameters.AddWithValue("@meter", meter);
            command.Parameters.AddWithValue("@eventType", eventType);
            command.Parameters.AddWithValue("@at", DateTime.UtcNow);
            command.Parameters.AddWithValue("@previous", (object?)previous ?? DBNull.Value);
            command.Parameters.AddWithValue("@after", (object?)after ?? DBNull.Value);
            command.Parameters.AddWithValue("@baseline", (object?)baseline ?? DBNull.Value);
            command.Parameters.AddWithValue("@actor", actor);
            command.ExecuteNonQuery();
        }

        Action mixedShape = () => InsertEvent($"UEV_MIX_{suffix}", "Reset", 10m, 0m, 0m, "maint");
        Action unknownType = () => InsertEvent($"UEV_TYPE_{suffix}", "Unknown", 10m, 0m, null, "maint");
        Action blankActor = () => InsertEvent($"UEV_ACTOR_{suffix}", "Reset", 10m, 0m, null, "   ");

        mixedShape.Should().Throw<SqliteException>();
        unknownType.Should().Throw<SqliteException>();
        blankActor.Should().Throw<SqliteException>();
    }

    [Fact]
    public async Task Tool_master_CAS_persists_exact_replay_across_later_versions()
    {
        var (ds, _) = Context();
        var service = new ToolService(new ToolRepository(ds), new EquipmentDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var toolId = $"TOOL-CAS-{suffix}";
        var create = new ToolCommand(
            toolId, "Wash fixture", "Fixture", ActorId: "maint-1",
            ExpectedVersion: 0, IdempotencyKey: $"tool:create:{suffix}");

        var first = await service.SaveAsync(create);
        var second = await service.SaveAsync(create with
        {
            ToolName = "Wash fixture v2", ActorId = "maint-2", ExpectedVersion = 1,
            IdempotencyKey = $"tool:update:{suffix}",
        });
        var replay = await service.SaveAsync(create);
        var stale = await service.SaveAsync(create with
        {
            ToolName = "stale", ExpectedVersion = 1,
            IdempotencyKey = $"tool:stale:{suffix}",
        });

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        second.IsSuccess.Should().BeTrue(second.IsFailure ? second.Error.Description : string.Empty);
        first.Value.Version.Should().Be(1);
        second.Value.Version.Should().Be(2);
        replay.Value.Should().Be(first.Value);
        stale.IsFailure.Should().BeTrue();
        stale.Error.Code.Should().Be("EMS.Tool.VersionConflict");
        Scalar<long>("SELECT VERSION_NO FROM EMS_TOOL WHERE TOOL_ID=@toolId", ("@toolId", toolId))
            .Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM EMS_TOOL_SAVE_COMMAND WHERE TOOL_ID=@toolId", ("@toolId", toolId))
            .Should().Be(2);
    }

    [Fact]
    public async Task Tool_lifecycle_persists_mount_usage_calibration_and_unmount_atomically()
    {
        var (ds, _) = Context();
        var service = new ToolService(new ToolRepository(ds), new EquipmentDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var toolId = $"TOOL-{suffix}";
        var at = DateTime.UtcNow;
        (await service.SaveAsync(new ToolCommand(
            toolId, "Wash fixture", "Fixture", MaxUseCount: 10m,
            CalibrationCycleDays: 90, ActorId: "maint"))).IsSuccess.Should().BeTrue();
        var mount = await service.MountAsync(new ToolMountCommand(
            $"mount:{suffix}", toolId, "EQ01", at, "PORT-A", "maint"));
        mount.IsSuccess.Should().BeTrue();
        var usage = await service.RecordUsageAsync(new ToolUsageCommand(
            $"usage:{suffix}", toolId, "EQ01", 1m, 3m, at.AddMinutes(1),
            MountId: mount.Value.MountId, ConditionSnapshotJson: "{\"pressure\":2.1}", ActorId: "operator"));
        usage.IsSuccess.Should().BeTrue();
        var calibration = await service.RecordInspectionAsync(new ToolInspectionCommand(
            $"cal:{suffix}", toolId, "Calibration", "Pass", at.AddMinutes(2), ActorId: "maint"));
        calibration.IsSuccess.Should().BeTrue();
        calibration.Value.NextDueAt.Should().Be(at.AddMinutes(2).AddDays(90));
        (await new ToolRepository(ds).GetToolAsync(toolId))!.Status.Should().Be("Mounted");
        var unmount = await service.UnmountAsync(new ToolUnmountCommand(
            $"unmount:{suffix}", mount.Value.MountId, at.AddMinutes(3), "cleaning", "maint"));
        unmount.IsSuccess.Should().BeTrue();
        unmount.Value.UnmountedBy.Should().Be("maint");
    }

    [Fact]
    public async Task Tool_unmount_cannot_precede_recorded_usage_at_service_repository_or_sqlite_boundary()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var service = new ToolService(repository, new EquipmentDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var toolId = $"TOOL-TIME-{suffix}";
        var mountedAt = DateTime.UtcNow.AddHours(-1);
        (await service.SaveAsync(new ToolCommand(
            toolId, "Chronology fixture", "Fixture", ActorId: "maint"))).IsSuccess.Should().BeTrue();
        var mount = await service.MountAsync(new ToolMountCommand(
            $"mount-time:{suffix}", toolId, "EQ01", mountedAt, ActorId: "maint"));
        var usedAt = mountedAt.AddMinutes(30);
        (await service.RecordUsageAsync(new ToolUsageCommand(
            $"usage-time:{suffix}", toolId, "EQ01", 1m, 0m, usedAt,
            MountId: mount.Value.MountId, ActorId: "operator"))).IsSuccess.Should().BeTrue();

        var serviceResult = await service.UnmountAsync(new ToolUnmountCommand(
            $"unmount-service-time:{suffix}", mount.Value.MountId, usedAt.AddSeconds(-1),
            ActorId: "maint"));
        var repositoryResult = await repository.TryUnmountAsync(
            mount.Value, $"unmount-repo-time:{suffix}", $"hash-{suffix}",
            usedAt.AddSeconds(-1), "maint", null);

        serviceResult.IsFailure.Should().BeTrue();
        serviceResult.Error.Code.Should().Be(nameof(ToolUnmountCommand.UnmountedAt));
        repositoryResult.Should().BeFalse();

        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var directUnmount = connection.CreateCommand();
        directUnmount.CommandText = "UPDATE EMS_TOOL_MOUNT_HISTORY SET UNMOUNTED_AT=@at WHERE MOUNT_ID=@id";
        directUnmount.Parameters.AddWithValue("@at", usedAt.AddSeconds(-1));
        directUnmount.Parameters.AddWithValue("@id", mount.Value.MountId);
        Action bypass = () => directUnmount.ExecuteNonQuery();
        bypass.Should().Throw<SqliteException>();
    }

    [Fact]
    public async Task Tool_mount_rejects_equipment_class_mismatch_through_equipment_directory()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var service = new ToolService(repository, new EquipmentDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var toolId = $"TOOL-CLASS-{suffix}";
        (await service.SaveAsync(new ToolCommand(
            toolId, "Precision fixture", "Fixture", EquipmentClassId: "EQC_PRECISION",
            ActorId: "maint"))).IsSuccess.Should().BeTrue();
        var at = DateTime.UtcNow;

        var serviceResult = await service.MountAsync(new ToolMountCommand(
            $"mount-class-service:{suffix}", toolId, "EQ01", at, "PORT-CLASS", "maint"));

        serviceResult.IsFailure.Should().BeTrue();
        serviceResult.Error.Code.Should().Be("EMS.Tool.EquipmentClassMismatch");
        (await repository.GetToolAsync(toolId))!.Status.Should().Be("Available");
        Scalar<long>("SELECT COUNT(*) FROM EMS_TOOL_MOUNT_HISTORY WHERE TOOL_ID=@toolId", ("@toolId", toolId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Parallel_tool_mounts_to_one_equipment_position_persist_exactly_one_winner()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var service = new ToolService(repository, new EquipmentDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var firstToolId = $"TOOL-POS-A-{suffix}";
        var secondToolId = $"TOOL-POS-B-{suffix}";
        (await service.SaveAsync(new ToolCommand(
            firstToolId, "Position fixture A", "Fixture", ActorId: "maint"))).IsSuccess.Should().BeTrue();
        (await service.SaveAsync(new ToolCommand(
            secondToolId, "Position fixture B", "Fixture", ActorId: "maint"))).IsSuccess.Should().BeTrue();
        var at = DateTime.UtcNow;
        var position = $"PORT-RACE-{suffix}";

        var results = await Task.WhenAll(
            service.MountAsync(new ToolMountCommand(
                $"mount-position-a:{suffix}", firstToolId, "EQ01", at, position, "maint")),
            service.MountAsync(new ToolMountCommand(
                $"mount-position-b:{suffix}", secondToolId, "EQ01", at, position, "maint")));

        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Count(result => result.IsFailure).Should().Be(1);
        results.Single(result => result.IsFailure).Error.Code
            .Should().BeOneOf("EMS.Tool.PositionOccupied", "EMS.Tool.AlreadyMounted");
        Scalar<long>(@"SELECT COUNT(*) FROM EMS_TOOL_MOUNT_HISTORY
                       WHERE EQUIPMENT_ID='EQ01' AND POSITION_CODE=@position AND UNMOUNTED_AT IS NULL",
                ("@position", position))
            .Should().Be(1);
        Scalar<long>(@"SELECT COUNT(*) FROM sqlite_master
                       WHERE type='index' AND name='UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION'")
            .Should().Be(1, "SQLite must retain the schema-level concurrency guard");
        new[]
            {
                (await repository.GetToolAsync(firstToolId))!.Status,
                (await repository.GetToolAsync(secondToolId))!.Status,
            }
            .Should().BeEquivalentTo(new[] { "Mounted", "Available" });
    }

    [Fact]
    public async Task Parallel_tool_usage_retries_increment_life_once()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var service = new ToolService(repository, new EquipmentDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var toolId = $"TOOL-RACE-{suffix}";
        (await service.SaveAsync(new ToolCommand(
            toolId, "Race fixture", "Fixture", MaxUseCount: 10m,
            ActorId: "maint"))).IsSuccess.Should().BeTrue();
        var command = new ToolUsageCommand(
            $"usage-race:{suffix}", toolId, "EQ01", 1m, 0m, DateTime.UtcNow,
            ActorId: "operator");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RecordUsageAsync(command)));

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.UsageId).Distinct().Should().ContainSingle();
        (await repository.GetToolAsync(toolId))!.CurrentUseCount.Should().Be(1m);
    }

    [Fact]
    public async Task Tool_service_rejects_non_operational_usage_but_allows_serviceable_inspection()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var service = new ToolService(repository, new EquipmentDirectory(ds));
        var cases = new[]
        {
            (Name: "due", Status: "Due", IsActive: true, CurrentUseCount: 0m, MaxUseCount: (decimal?)10m, InspectionAllowed: true),
            (Name: "retired", Status: "Retired", IsActive: true, CurrentUseCount: 0m, MaxUseCount: (decimal?)10m, InspectionAllowed: false),
            (Name: "inactive", Status: "Available", IsActive: false, CurrentUseCount: 0m, MaxUseCount: (decimal?)10m, InspectionAllowed: false),
            (Name: "exhausted", Status: "Available", IsActive: true, CurrentUseCount: 10m, MaxUseCount: (decimal?)10m, InspectionAllowed: true),
        };

        foreach (var item in cases)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var toolId = $"TOOL-{item.Name}-{suffix}";
            (await repository.TrySaveToolAsync(new ToolRecord(
                ToolId: toolId, ToolName: item.Name, ToolType: "Fixture",
                ToolNumber: null, SerialNumber: null, EquipmentClassId: null,
                MaxUseCount: item.MaxUseCount, MaxUseMinutes: null,
                CurrentUseCount: item.CurrentUseCount, CurrentUseMinutes: 0m,
                InspectionCycleDays: 30, CalibrationCycleDays: 180,
                LastInspectedAt: null, LastCalibratedAt: null,
                NextInspectionDueAt: null, NextCalibrationDueAt: null,
                Status: item.Status, Location: null, IsActive: item.IsActive), null, "maint")).Should().BeTrue();

            var usage = await service.RecordUsageAsync(new ToolUsageCommand(
                $"usage:{suffix}", toolId, "EQ01", 1m, 0m, DateTime.UtcNow,
                ActorId: "operator"));
            var inspection = await service.RecordInspectionAsync(new ToolInspectionCommand(
                $"inspection:{suffix}", toolId, "Inspection", "Pass", DateTime.UtcNow,
                ActorId: "maint"));

            usage.IsFailure.Should().BeTrue(item.Name);
            inspection.IsSuccess.Should().Be(item.InspectionAllowed, item.Name);
        }
    }

    [Fact]
    public async Task Tool_repository_guard_cannot_be_bypassed_for_non_operational_tools()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var cases = new[]
        {
            (Name: "due", Status: "Due", IsActive: true, CurrentUseCount: 0m, MaxUseCount: (decimal?)10m, InspectionAllowed: true),
            (Name: "retired", Status: "Retired", IsActive: true, CurrentUseCount: 0m, MaxUseCount: (decimal?)10m, InspectionAllowed: false),
            (Name: "inactive", Status: "Available", IsActive: false, CurrentUseCount: 0m, MaxUseCount: (decimal?)10m, InspectionAllowed: false),
            (Name: "exhausted", Status: "Available", IsActive: true, CurrentUseCount: 10m, MaxUseCount: (decimal?)10m, InspectionAllowed: true),
        };

        foreach (var item in cases)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var toolId = $"TOOL-guard-{item.Name}-{suffix}";
            (await repository.TrySaveToolAsync(new ToolRecord(
                ToolId: toolId, ToolName: item.Name, ToolType: "Fixture",
                ToolNumber: null, SerialNumber: null, EquipmentClassId: null,
                MaxUseCount: item.MaxUseCount, MaxUseMinutes: null,
                CurrentUseCount: item.CurrentUseCount, CurrentUseMinutes: 0m,
                InspectionCycleDays: 30, CalibrationCycleDays: 180,
                LastInspectedAt: null, LastCalibratedAt: null,
                NextInspectionDueAt: null, NextCalibrationDueAt: null,
                Status: item.Status, Location: null, IsActive: item.IsActive), null, "maint")).Should().BeTrue();
            var now = DateTime.UtcNow;
            var usageKey = $"usage-guard:{suffix}";
            var inspectionKey = $"inspection-guard:{suffix}";

            var usageRecorded = await repository.TryRecordUsageAsync(new ToolUsageRecord(
                UsageId: $"TUS_{suffix}", IdempotencyKey: usageKey, RequestHash: $"hash-{suffix}",
                ToolId: toolId, MountId: null, EquipmentId: "EQ01", ProcessLotId: null,
                WorkOrderId: null, ProcessId: null, RecipeId: null, RecipeVersion: null,
                UseCount: 1m, UseMinutes: 0m, UsedAt: now, UsedBy: "operator",
                TraceId: null, ConditionSnapshotJson: null, CreatedAt: now), null);
            var inspectionRecorded = await repository.TryRecordInspectionAsync(new ToolInspectionRecord(
                InspectionId: $"TIN_{suffix}", IdempotencyKey: inspectionKey,
                RequestHash: $"hash-inspection-{suffix}", ToolId: toolId,
                InspectionType: "Inspection", Result: "Pass", MeasuredValue: null,
                StandardValue: null, CertificateNumber: null, InspectedAt: now,
                InspectedBy: "maint", NextDueAt: now.AddDays(30), Remark: null, CreatedAt: now));

            usageRecorded.Should().BeFalse(item.Name);
            inspectionRecorded.Should().Be(item.InspectionAllowed, item.Name);
            (await repository.GetUsageByIdempotencyKeyAsync(usageKey)).Should().BeNull(item.Name);
            if (item.InspectionAllowed)
                (await repository.GetInspectionByIdempotencyKeyAsync(inspectionKey)).Should().NotBeNull(item.Name);
            else
                (await repository.GetInspectionByIdempotencyKeyAsync(inspectionKey)).Should().BeNull(item.Name);
            var persisted = await repository.GetToolAsync(toolId);
            persisted.Should().NotBeNull(item.Name);
            persisted!.Status.Should().Be(item.Status, item.Name);
            persisted.IsActive.Should().Be(item.IsActive, item.Name);
        }
    }

    [Fact]
    public async Task Passing_inspection_preserves_available_tool_status()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var service = new ToolService(repository, new EquipmentDirectory(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var toolId = $"TOOL-available-{suffix}";
        (await service.SaveAsync(new ToolCommand(
            toolId, "Available fixture", "Fixture", MaxUseCount: 10m,
            InspectionCycleDays: 30, ActorId: "maint"))).IsSuccess.Should().BeTrue();

        var inspection = await service.RecordInspectionAsync(new ToolInspectionCommand(
            $"inspection:{suffix}", toolId, "Inspection", "Pass", DateTime.UtcNow,
            ActorId: "maint"));

        inspection.IsSuccess.Should().BeTrue();
        (await repository.GetToolAsync(toolId))!.Status.Should().Be("Available");
    }
}
