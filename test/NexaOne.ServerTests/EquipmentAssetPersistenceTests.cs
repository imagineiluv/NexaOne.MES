using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.EMS.Application.Tools;
using NexaOne.EMS.Infrastructure;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Infrastructure;
using NexaOne.Infrastructure.Persistence;
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

    [Fact]
    public async Task Carrier_output_is_persisted_once_for_an_idempotency_key()
    {
        var (ds, _) = Context();
        var service = new EquipmentOutputService(new EquipmentOutputRepository(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var command = new EquipmentOutputCommand(
            $"carrier:{suffix}", "PLANT01", "EQ01", "CarrierCleaned", 1m, 1m, 0m, "EA",
            DateTime.UtcNow, "EquipmentPlugin", SourceEventId: $"plc:{suffix}", CarrierId: $"CAR-{suffix}", ActorId: "operator");

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
        var service = new EquipmentOutputService(new EquipmentOutputRepository(ds));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var command = new EquipmentOutputCommand(
            $"carrier-race:{suffix}", "PLANT01", "EQ01", "CarrierCleaned", 1m, 1m, 0m, "EA",
            DateTime.UtcNow, "EquipmentPlugin", SourceEventId: $"plc-race:{suffix}",
            CarrierId: $"CAR-{suffix}", ActorId: "operator");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RecordAsync(command)));

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.OutputEventId).Distinct().Should().ContainSingle();
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
            EquipmentId: "EQ01", CostPerUnit: 100m, CarbonPerUnit: 0.5m, ActorId: "maint"))).IsSuccess.Should().BeTrue();
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
            EquipmentId: "EQ01", ActorId: "maint"))).IsSuccess.Should().BeTrue();
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
            EquipmentId: "EQ01", ActorId: "maint"))).IsSuccess.Should().BeTrue();
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
            EquipmentId: "EQ01", ActorId: "maint"))).IsSuccess.Should().BeTrue();
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
    public async Task Tool_lifecycle_persists_mount_usage_calibration_and_unmount_atomically()
    {
        var (ds, _) = Context();
        var service = new ToolService(new ToolRepository(ds));
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
    public async Task Parallel_tool_usage_retries_increment_life_once()
    {
        var (ds, _) = Context();
        var repository = new ToolRepository(ds);
        var service = new ToolService(repository);
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
        var service = new ToolService(repository);
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
                TraceId: null, ConditionSnapshotJson: null, CreatedAt: now));
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
        var service = new ToolService(repository);
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
