using FluentAssertions;
using NexaOne.EMS.Application.Tools;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.UnitTests.Services;

public sealed class ToolServiceTests
{
    [Fact]
    public async Task Master_save_uses_version_CAS_and_replays_the_persisted_command_result()
    {
        var repository = new MemoryRepository();
        var service = Service(repository);
        var create = new ToolCommand(
            "TOOL-CAS", "Nozzle jig", "Fixture", ActorId: "admin",
            ExpectedVersion: 0, IdempotencyKey: "tool:create");

        var first = await service.SaveAsync(create);
        var update = await service.SaveAsync(create with
        {
            ToolName = "Nozzle jig v2", ExpectedVersion = 1, IdempotencyKey = "tool:update",
        });
        var originalReplay = await service.SaveAsync(create);
        var stale = await service.SaveAsync(create with
        {
            ToolName = "stale", ExpectedVersion = 1, IdempotencyKey = "tool:stale",
        });
        var conflict = await service.SaveAsync(create with { ToolName = "changed" });

        first.IsSuccess.Should().BeTrue();
        first.Value.Version.Should().Be(1);
        update.IsSuccess.Should().BeTrue();
        update.Value.Version.Should().Be(2);
        originalReplay.IsSuccess.Should().BeTrue();
        originalReplay.Value.Should().Be(first.Value, "replay returns the immutable command result, not current master state");
        stale.IsFailure.Should().BeTrue();
        stale.Error.Code.Should().Be("EMS.Tool.VersionConflict");
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.Tool.IdempotencyConflict");
    }

    [Fact]
    public void Constructor_requires_equipment_directory()
    {
        Action create = () => new ToolService(new MemoryRepository(), null!);

        create.Should().Throw<ArgumentNullException>()
            .WithParameterName("equipmentDirectory");
    }

    [Fact]
    public async Task Master_save_validates_equipment_class_through_directory()
    {
        var repository = new MemoryRepository();
        var service = new ToolService(
            repository,
            new EquipmentDirectoryStub(
                "EQ01",
                "EQC-GENERAL",
                equipmentClassExists: false));

        var result = await service.SaveAsync(new ToolCommand(
            "TOOL-01", "Nozzle jig", "Fixture",
            EquipmentClassId: "EQC-MISSING", ActorId: "admin"));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(NexaOne.Common.ErrorType.NotFound);
        repository.Tool.Should().BeNull();
    }

    [Theory]
    [InlineData(false, "EQC-GENERAL", "EMS.Tool.EquipmentInactive")]
    [InlineData(true, "EQC-OTHER", "EMS.Tool.EquipmentClassMismatch")]
    public async Task Usage_validates_equipment_state_and_class_through_directory(
        bool equipmentIsValid,
        string equipmentClassId,
        string expectedCode)
    {
        var repository = new MemoryRepository
        {
            Tool = AvailableTool() with { EquipmentClassId = "EQC-GENERAL" },
        };
        var service = new ToolService(
            repository,
            new EquipmentDirectoryStub("EQ01", equipmentClassId, equipmentIsValid));

        var result = await service.RecordUsageAsync(new ToolUsageCommand(
            "usage-directory", "TOOL-01", "EQ01", 1m, 0m,
            DateTime.UtcNow, ActorId: "operator"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
        repository.Usages.Should().BeEmpty();
    }

    [Fact]
    public async Task Mount_usage_and_calibration_keep_separate_actor_and_context_history()
    {
        var repo = new MemoryRepository();
        var service = Service(repo);
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
        var service = Service(repo);
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
        var service = Service(repo);
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
        var service = Service(repo);
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
        var service = Service(repo);

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
        var service = Service(repo);
        var at = new DateTime(2026, 8, 26, 3, 0, 0, DateTimeKind.Utc);
        (await service.MountAsync(new ToolMountCommand(
            "mount-active", "TOOL-01", "EQ01", at, ActorId: "operator"))).IsSuccess.Should().BeTrue();

        var save = await service.SaveAsync(new ToolCommand(
            "TOOL-01", "Nozzle jig", "Fixture", Status: "Available", ActorId: "admin",
            ExpectedVersion: 1, IdempotencyKey: "tool-mounted-save"));

        save.IsFailure.Should().BeTrue();
        repo.Tool!.Status.Should().Be("Mounted");
    }

    [Fact]
    public async Task Master_save_cannot_change_equipment_class_while_tool_is_mounted()
    {
        var repo = new MemoryRepository
        {
            Tool = AvailableTool() with { EquipmentClassId = "EQC-GENERAL" },
        };
        var service = Service(repo);
        var at = new DateTime(2026, 8, 26, 3, 30, 0, DateTimeKind.Utc);
        (await service.MountAsync(new ToolMountCommand(
            "mount-class-active", "TOOL-01", "EQ01", at, ActorId: "operator")))
            .IsSuccess.Should().BeTrue();

        var save = await service.SaveAsync(new ToolCommand(
            "TOOL-01", "Nozzle jig", "Fixture", EquipmentClassId: "EQC-PRECISION",
            Status: "Mounted", ActorId: "admin", ExpectedVersion: 1,
            IdempotencyKey: "tool-mounted-class-save"));

        save.IsFailure.Should().BeTrue();
        save.Error.Code.Should().Be("EMS.Tool.ActiveMountState");
        repo.Tool!.EquipmentClassId.Should().Be("EQC-GENERAL");
    }

    [Fact]
    public async Task Usage_cannot_precede_its_matching_mount()
    {
        var repo = new MemoryRepository { Tool = AvailableTool() };
        var service = Service(repo);
        var mountedAt = new DateTime(2026, 8, 26, 4, 0, 0, DateTimeKind.Utc);
        var mount = await service.MountAsync(new ToolMountCommand(
            "mount-chronology", "TOOL-01", "EQ01", mountedAt, ActorId: "operator"));

        var usage = await service.RecordUsageAsync(new ToolUsageCommand(
            "usage-before-mount", "TOOL-01", "EQ01", 1m, 0m,
            mountedAt.AddSeconds(-1), MountId: mount.Value.MountId, ActorId: "operator"));

        usage.IsFailure.Should().BeTrue();
        usage.Error.Code.Should().Be(nameof(ToolUsageCommand.UsedAt));
        repo.Usages.Should().BeEmpty();
    }

    [Fact]
    public async Task Unmount_cannot_precede_usage_already_recorded_for_the_mount()
    {
        var repo = new MemoryRepository { Tool = AvailableTool() };
        var service = Service(repo);
        var mountedAt = new DateTime(2026, 8, 26, 4, 0, 0, DateTimeKind.Utc);
        var mount = await service.MountAsync(new ToolMountCommand(
            "mount-unmount-chronology", "TOOL-01", "EQ01", mountedAt, ActorId: "operator"));
        (await service.RecordUsageAsync(new ToolUsageCommand(
            "usage-before-unmount", "TOOL-01", "EQ01", 1m, 0m,
            mountedAt.AddMinutes(10), MountId: mount.Value.MountId, ActorId: "operator")))
            .IsSuccess.Should().BeTrue();

        var unmount = await service.UnmountAsync(new ToolUnmountCommand(
            "unmount-before-usage", mount.Value.MountId, mountedAt.AddMinutes(5),
            ActorId: "operator"));

        unmount.IsFailure.Should().BeTrue();
        unmount.Error.Code.Should().Be(nameof(ToolUnmountCommand.UnmountedAt));
        repo.Mounts.Single().UnmountedAt.Should().BeNull();
    }

    [Fact]
    public async Task Mount_rejects_a_tool_for_a_different_equipment_class()
    {
        var repo = new MemoryRepository
        {
            Tool = AvailableTool() with { EquipmentClassId = "EQC-PRECISION" },
        };

        var result = await new ToolService(
            repo,
            new EquipmentDirectoryStub("EQ01", "EQC-GENERAL")).MountAsync(new ToolMountCommand(
            "mount-class-mismatch", "TOOL-01", "EQ01", DateTime.UtcNow,
            "PORT-A", "operator"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.Tool.EquipmentClassMismatch");
        repo.Mounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Mount_rejects_an_equipment_position_that_is_already_occupied()
    {
        var repo = new MemoryRepository { Tool = AvailableTool() };
        repo.Mounts.Add(new ToolMountRecord(
            "MOUNT-OTHER", "mount-other", "hash-other", "TOOL-OTHER", "EQ01",
            "PORT-A", DateTime.UtcNow.AddMinutes(-1), "operator", null, null,
            null, null, null, DateTime.UtcNow.AddMinutes(-1)));

        var result = await Service(repo).MountAsync(new ToolMountCommand(
            "mount-position-conflict", "TOOL-01", "EQ01", DateTime.UtcNow,
            "PORT-A", "operator"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.Tool.PositionOccupied");
        repo.Mounts.Should().ContainSingle();
    }

    [Fact]
    public async Task Write_commands_fail_closed_without_an_actor()
    {
        var previous = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        try
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
            var result = await Service(new MemoryRepository()).SaveAsync(
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

    private static ToolService Service(MemoryRepository repository)
        => new(repository, new EquipmentDirectoryStub("EQ01", "EQC-GENERAL"));

    private sealed class EquipmentDirectoryStub(
        string equipmentId,
        string equipmentClassId,
        bool isValid = true,
        bool equipmentClassExists = true) : IEquipmentDirectory
    {
        public Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(
            string plantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([equipmentId]);

        public Task<EquipmentDirectoryEntry?> GetEquipmentAsync(
            string requestedEquipmentId, CancellationToken ct = default)
            => Task.FromResult<EquipmentDirectoryEntry?>(
                requestedEquipmentId == equipmentId
                    ? new EquipmentDirectoryEntry(equipmentId, "PLANT01", equipmentClassId, isValid)
                    : null);

        public Task<bool> EquipmentClassExistsAsync(
            string requestedEquipmentClassId,
            CancellationToken ct = default)
            => Task.FromResult(equipmentClassExists);
    }

    private sealed class MemoryRepository : IToolRepository
    {
        public ToolRecord? Tool { get; set; }
        public List<ToolMountRecord> Mounts { get; } = new();
        public List<ToolUsageRecord> Usages { get; } = new();
        public List<ToolInspectionRecord> Inspections { get; } = new();
        public List<ToolSaveCommandRecord> SaveCommands { get; } = new();

        public Task<ToolRecord?> GetToolAsync(string toolId, CancellationToken ct = default)
            => Task.FromResult(Tool?.ToolId == toolId ? Tool : null);
        public Task<ToolSaveCommandRecord?> GetSaveCommandAsync(
            string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(SaveCommands.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey));
        public Task<bool> TrySaveToolAsync(
            ToolRecord tool,
            string? expectedStatus,
            int expectedVersion,
            ToolSaveCommandRecord command,
            string actorId,
            CancellationToken ct = default)
        {
            var active = Mounts.SingleOrDefault(x => x.ToolId == tool.ToolId && x.UnmountedAt is null);
            if (SaveCommands.Any(x => x.IdempotencyKey == command.IdempotencyKey))
                return Task.FromResult(false);
            if ((Tool is null && expectedVersion != 0)
                || (Tool is not null && Tool.Version != expectedVersion))
                return Task.FromResult(false);
            if (Tool is not null && !string.Equals(Tool.Status, expectedStatus, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
            if (active is not null && (Tool is null || !tool.IsActive
                                      || !string.Equals(tool.Status, Tool.Status, StringComparison.OrdinalIgnoreCase)
                                      || !string.Equals(tool.EquipmentClassId, Tool.EquipmentClassId, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(false);
            if (active is null && tool.Status.Equals("Mounted", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
            Tool = tool;
            SaveCommands.Add(command);
            return Task.FromResult(true);
        }
        public Task<ToolMountRecord?> GetMountAsync(string mountId, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.MountId == mountId));
        public Task<ToolMountRecord?> GetActiveMountAsync(string toolId, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.ToolId == toolId && x.UnmountedAt is null));
        public Task<ToolMountRecord?> GetActiveMountAtPositionAsync(
            string equipmentId, string positionCode, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.EquipmentId == equipmentId
                                                          && x.PositionCode == positionCode
                                                          && x.UnmountedAt is null));
        public Task<ToolMountRecord?> GetMountByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.IdempotencyKey == key));
        public Task<ToolMountRecord?> GetUnmountByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Mounts.SingleOrDefault(x => x.UnmountIdempotencyKey == key));
        public Task<DateTime?> GetLatestUsageAtAsync(string mountId, CancellationToken ct = default)
            => Task.FromResult(Usages
                .Where(x => x.MountId == mountId)
                .Select(x => (DateTime?)x.UsedAt)
                .Max());
        public Task<bool> TryMountAsync(
            ToolMountRecord mount,
            string? expectedEquipmentClassId,
            CancellationToken ct = default)
        {
            if (Mounts.Any(x => x.ToolId == mount.ToolId && x.UnmountedAt is null)) return Task.FromResult(false);
            if (!string.Equals(
                    Tool!.EquipmentClassId,
                    expectedEquipmentClassId,
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
            if (mount.PositionCode is not null
                && Mounts.Any(x => x.EquipmentId == mount.EquipmentId
                                   && x.PositionCode == mount.PositionCode
                                   && x.UnmountedAt is null))
                return Task.FromResult(false);
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
        public Task<bool> TryRecordUsageAsync(
            ToolUsageRecord usage,
            string? expectedEquipmentClassId,
            CancellationToken ct = default)
        {
            if (Usages.Any(x => x.IdempotencyKey == usage.IdempotencyKey)) return Task.FromResult(false);
            if (!string.Equals(
                    Tool!.EquipmentClassId,
                    expectedEquipmentClassId,
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
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
