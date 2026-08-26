using FluentAssertions;
using NexaOne.EMS.Application.Tools;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.UnitTests.Services;

public sealed class ToolServiceTests
{
    [Fact]
    public async Task Mount_usage_and_calibration_keep_separate_actor_and_context_history()
    {
        var repo = new MemoryRepository();
        var service = new ToolService(repo);
        (await service.SaveAsync(new ToolCommand(
            "TOOL-01", "Nozzle jig", "Fixture", MaxUseCount: 100m,
            InspectionCycleDays: 30, CalibrationCycleDays: 180, ActorId: "admin"))).IsSuccess.Should().BeTrue();

        var at = new DateTime(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc);
        var mount = await service.MountAsync(new ToolMountCommand("mount-1", "TOOL-01", "EQ01", at, "PORT-A", "operator-1"));
        var usage = await service.RecordUsageAsync(new ToolUsageCommand(
            "usage-1", "TOOL-01", "EQ01", 1m, 2.5m, at.AddMinutes(10),
            MountId: mount.Value.MountId, ProcessLotId: "LOT-1", RecipeId: "RCP-1",
            RecipeVersion: 2, ConditionSnapshotJson: "{\"temp\":45}", ActorId: "operator-1"));
        var calibration = await service.RecordInspectionAsync(new ToolInspectionCommand(
            "cal-1", "TOOL-01", "Calibration", "Pass", at.AddHours(1), ActorId: "maint-1"));

        mount.IsSuccess.Should().BeTrue();
        usage.IsSuccess.Should().BeTrue();
        usage.Value.ProcessLotId.Should().Be("LOT-1");
        usage.Value.UsedBy.Should().Be("operator-1");
        calibration.IsSuccess.Should().BeTrue();
        calibration.Value.InspectedBy.Should().Be("maint-1");
        calibration.Value.NextDueAt.Should().Be(at.AddHours(1).AddDays(180));
    }

    [Fact]
    public async Task Replays_identical_usage_and_rejects_changed_payload()
    {
        var repo = new MemoryRepository { Tool = AvailableTool() };
        var service = new ToolService(repo);
        var command = new ToolUsageCommand(
            "usage-1", "TOOL-01", "EQ01", 1m, 0m, DateTime.UtcNow, ActorId: "operator");

        var first = await service.RecordUsageAsync(command);
        var replay = await service.RecordUsageAsync(command);
        var conflict = await service.RecordUsageAsync(command with { UseCount = 2m });

        first.IsSuccess.Should().BeTrue();
        replay.Value.UsageId.Should().Be(first.Value.UsageId);
        conflict.IsFailure.Should().BeTrue();
        repo.Usages.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Due", true, true)]
    [InlineData("Blocked", true, true)]
    [InlineData("Expired", true, false)]
    [InlineData("Retired", true, false)]
    [InlineData("Available", false, false)]
    public async Task Non_operational_tools_reject_usage_but_serviceable_states_allow_inspection(
        string status,
        bool isActive,
        bool inspectionAllowed)
    {
        var repo = new MemoryRepository
        {
            Tool = AvailableTool() with { Status = status, IsActive = isActive },
        };
        var service = new ToolService(repo);
        var at = new DateTime(2026, 8, 26, 2, 0, 0, DateTimeKind.Utc);

        var usage = await service.RecordUsageAsync(new ToolUsageCommand(
            $"usage-{status}-{isActive}", "TOOL-01", "EQ01", 1m, 0m, at,
            ActorId: "operator"));
        var inspection = await service.RecordInspectionAsync(new ToolInspectionCommand(
            $"inspection-{status}-{isActive}", "TOOL-01", "Inspection", "Pass", at,
            ActorId: "maint"));

        usage.IsFailure.Should().BeTrue();
        inspection.IsSuccess.Should().Be(inspectionAllowed);
        repo.Usages.Should().BeEmpty();
        repo.Inspections.Should().HaveCount(inspectionAllowed ? 1 : 0);
    }

    [Fact]
    public async Task Tool_at_a_life_limit_rejects_usage_but_still_allows_inspection()
    {
        var repo = new MemoryRepository
        {
            Tool = AvailableTool() with { CurrentUseCount = 100m },
        };
        var service = new ToolService(repo);
        var at = new DateTime(2026, 8, 26, 3, 0, 0, DateTimeKind.Utc);

        var usage = await service.RecordUsageAsync(new ToolUsageCommand(
            "usage-exhausted", "TOOL-01", "EQ01", 1m, 0m, at, ActorId: "operator"));
        var inspection = await service.RecordInspectionAsync(new ToolInspectionCommand(
            "inspection-exhausted", "TOOL-01", "Calibration", "Pass", at,
            ActorId: "maint"));

        usage.IsFailure.Should().BeTrue();
        inspection.IsSuccess.Should().BeTrue();
        repo.Usages.Should().BeEmpty();
        repo.Inspections.Should().ContainSingle();
    }

    [Fact]
    public async Task Expired_inspection_or_calibration_due_date_blocks_use_but_not_inspection()
    {
        var at = new DateTime(2026, 8, 26, 3, 0, 0, DateTimeKind.Utc);
        var repo = new MemoryRepository
        {
            Tool = AvailableTool() with { NextCalibrationDueAt = at.AddSeconds(-1) },
        };
        var service = new ToolService(repo);

        var mount = await service.MountAsync(new ToolMountCommand(
            "mount-expired", "TOOL-01", "EQ01", at, ActorId: "operator"));
        var usage = await service.RecordUsageAsync(new ToolUsageCommand(
            "usage-expired", "TOOL-01", "EQ01", 1m, 0m, at, ActorId: "operator"));
        var inspection = await service.RecordInspectionAsync(new ToolInspectionCommand(
            "cal-expired", "TOOL-01", "Calibration", "Pass", at, ActorId: "maint"));

        mount.IsFailure.Should().BeTrue();
        usage.IsFailure.Should().BeTrue();
        inspection.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Master_save_cannot_overwrite_lifecycle_status_while_tool_is_mounted()
    {
        var repo = new MemoryRepository { Tool = AvailableTool() };
        var service = new ToolService(repo);
        var at = new DateTime(2026, 8, 26, 3, 0, 0, DateTimeKind.Utc);
        (await service.MountAsync(new ToolMountCommand(
            "mount-active", "TOOL-01", "EQ01", at, ActorId: "operator"))).IsSuccess.Should().BeTrue();

        var save = await service.SaveAsync(new ToolCommand(
            "TOOL-01", "Nozzle jig", "Fixture", Status: "Available", ActorId: "admin"));

        save.IsFailure.Should().BeTrue();
        repo.Tool!.Status.Should().Be("Mounted");
    }

    [Fact]
    public async Task Write_commands_fail_closed_without_an_actor()
    {
        var previous = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        try
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
            var result = await new ToolService(new MemoryRepository()).SaveAsync(
                new ToolCommand("TOOL-01", "Nozzle jig", "Fixture"));

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previous;
        }
    }

    private static ToolRecord AvailableTool() => new(
        "TOOL-01", "Nozzle jig", "Fixture", null, null, null, 100m, null,
        0m, 0m, 30, 180, null, null, null, null, "Available", null, true);

    private sealed class MemoryRepository : IToolRepository
    {
        public ToolRecord? Tool { get; set; }
        public List<ToolMountRecord> Mounts { get; } = new();
        public List<ToolUsageRecord> Usages { get; } = new();
        public List<ToolInspectionRecord> Inspections { get; } = new();

        public Task<ToolRecord?> GetToolAsync(string toolId, CancellationToken ct = default)
            => Task.FromResult(Tool?.ToolId == toolId ? Tool : null);
        public Task<bool> TrySaveToolAsync(
            ToolRecord tool, string? expectedStatus, string actorId, CancellationToken ct = default)
        {
            var active = Mounts.SingleOrDefault(x => x.ToolId == tool.ToolId && x.UnmountedAt is null);
            if (Tool is not null && !string.Equals(Tool.Status, expectedStatus, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
            if (active is not null && (Tool is null || !tool.IsActive
                                      || !string.Equals(tool.Status, Tool.Status, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(false);
            if (active is null && tool.Status.Equals("Mounted", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
            Tool = tool;
            return Task.FromResult(true);
        }
        public Task<bool> EquipmentExistsAsync(string equipmentId, CancellationToken ct = default)
            => Task.FromResult(equipmentId == "EQ01");
        public Task<bool> EquipmentClassExistsAsync(string equipmentClassId, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<ToolMountRecord?> GetMountAsync(string mountId, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.MountId == mountId));
        public Task<ToolMountRecord?> GetActiveMountAsync(string toolId, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.ToolId == toolId && x.UnmountedAt is null));
        public Task<ToolMountRecord?> GetMountByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.IdempotencyKey == key));
        public Task<ToolMountRecord?> GetUnmountByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.UnmountIdempotencyKey == key));
        public Task<bool> TryMountAsync(ToolMountRecord mount, CancellationToken ct = default)
        {
            if (Mounts.Any(x => x.ToolId == mount.ToolId && x.UnmountedAt is null)) return Task.FromResult(false);
            Mounts.Add(mount);
            Tool = Tool! with { Status = "Mounted" };
            return Task.FromResult(true);
        }
        public Task<bool> TryUnmountAsync(ToolMountRecord mount, string key, string hash, DateTime at,
            string actorId, string? reason, CancellationToken ct = default)
        {
            var index = Mounts.FindIndex(x => x.MountId == mount.MountId && x.UnmountedAt is null);
            if (index < 0) return Task.FromResult(false);
            Mounts[index] = mount with { UnmountedAt = at, UnmountedBy = actorId, UnmountIdempotencyKey = key, UnmountRequestHash = hash, UnmountReason = reason };
            Tool = Tool! with { Status = "Available" };
            return Task.FromResult(true);
        }
        public Task<ToolUsageRecord?> GetUsageByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Usages.SingleOrDefault(x => x.IdempotencyKey == key));
        public Task<bool> TryRecordUsageAsync(ToolUsageRecord usage, CancellationToken ct = default)
        {
            if (Usages.Any(x => x.IdempotencyKey == usage.IdempotencyKey)) return Task.FromResult(false);
            Usages.Add(usage);
            Tool = Tool! with { CurrentUseCount = Tool.CurrentUseCount + usage.UseCount, CurrentUseMinutes = Tool.CurrentUseMinutes + usage.UseMinutes };
            return Task.FromResult(true);
        }
        public Task<ToolInspectionRecord?> GetInspectionByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Inspections.SingleOrDefault(x => x.IdempotencyKey == key));
        public Task<bool> TryRecordInspectionAsync(ToolInspectionRecord inspection, CancellationToken ct = default)
        {
            if (Inspections.Any(x => x.IdempotencyKey == inspection.IdempotencyKey)) return Task.FromResult(false);
            Inspections.Add(inspection);
            return Task.FromResult(true);
        }
    }
}
