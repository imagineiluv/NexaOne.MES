using NexaOne.EMS.Application.MaintenanceExecution;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.UnitTests.Services;

public sealed class MaintenanceExecutionServiceTests
{
    private static readonly DateTime At = new(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Checklist_requires_evidence_and_replays_only_the_exact_request()
    {
        var repository = new MemoryRepository();
        var service = new MaintenanceExecutionService(repository);
        var context = new EmsCommandContextDto("login-maintainer", "check-key", "POP", "PANEL-01");
        var empty = new MaintenanceCheckCommand(
            "CHECK-EMPTY", "WO-1", 1, "Temperature", At, context);
        var command = empty with
        {
            CheckResultId = "CHECK-1",
            MeasuredValue = 42.1m,
            Unit = "C",
            IsPass = true,
        };

        var invalid = await service.RecordCheckAsync(empty);
        var first = await service.RecordCheckAsync(command);
        var replay = await service.RecordCheckAsync(command);
        var conflict = await service.RecordCheckAsync(command with { Finding = "changed" });

        invalid.IsFailure.Should().BeTrue();
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        first.Value.RecordedBy.Should().Be("login-maintainer");
        first.Value.ClientChannel.Should().Be("POP");
        replay.IsSuccess.Should().BeTrue();
        replay.Value.CheckResultId.Should().Be(first.Value.CheckResultId);
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.MaintenanceExecution.IdempotencyConflict");
        repository.Checks.Should().ContainSingle();
    }

    [Fact]
    public async Task Labor_resolves_authenticated_worker_and_uses_optimistic_completion()
    {
        var repository = new MemoryRepository { WorkerId = "WORKER-1" };
        var service = new MaintenanceExecutionService(repository);
        var spoof = await service.StartLaborAsync(new MaintenanceLaborStartCommand(
            "LABOR-SPOOF", "WO-1", "Work", At,
            new EmsCommandContextDto("login-maintainer", "start-spoof"),
            WorkerId: "WORKER-OTHER"));
        var start = await service.StartLaborAsync(new MaintenanceLaborStartCommand(
            "LABOR-1", "WO-1", "inspection", At,
            new EmsCommandContextDto("login-maintainer", "start-key", "MOBILE", "TABLET-01")));
        var stale = await service.CompleteLaborAsync(new MaintenanceLaborCompleteCommand(
            "LABOR-1", 2, At.AddHours(1),
            new EmsCommandContextDto("login-maintainer", "end-stale")));
        var complete = await service.CompleteLaborAsync(new MaintenanceLaborCompleteCommand(
            "LABOR-1", 1, At.AddMinutes(90),
            new EmsCommandContextDto("login-maintainer", "end-key", "MOBILE", "TABLET-01")));
        var replay = await service.CompleteLaborAsync(new MaintenanceLaborCompleteCommand(
            "LABOR-1", 1, At.AddMinutes(90),
            new EmsCommandContextDto("login-maintainer", "end-key", "MOBILE", "TABLET-01")));

        spoof.IsFailure.Should().BeTrue();
        spoof.Error.Code.Should().Be("EMS.MaintenanceExecution.WorkerMappingMismatch");
        start.IsSuccess.Should().BeTrue(start.IsFailure ? start.Error.Description : string.Empty);
        start.Value.WorkerId.Should().Be("WORKER-1");
        start.Value.LaborType.Should().Be("Inspection");
        stale.IsFailure.Should().BeTrue();
        stale.Error.Code.Should().Be("EMS.MaintenanceExecution.LaborVersionConflict");
        complete.IsSuccess.Should().BeTrue(complete.IsFailure ? complete.Error.Description : string.Empty);
        complete.Value.LaborHours.Should().Be(1.5m);
        complete.Value.EndedBy.Should().Be("login-maintainer");
        complete.Value.Version.Should().Be(2);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Version.Should().Be(2);
    }

    [Fact]
    public async Task Execution_requires_an_active_work_order_and_authenticated_command_context()
    {
        var repository = new MemoryRepository { WorkOrderStatus = "Completed" };
        var service = new MaintenanceExecutionService(repository);

        var inactive = await service.RecordCheckAsync(new MaintenanceCheckCommand(
            "CHECK-1", "WO-1", 1, "Temperature", At,
            new EmsCommandContextDto("login-maintainer", "check-key"), IsPass: true));
        var missingActor = await service.StartLaborAsync(new MaintenanceLaborStartCommand(
            "LABOR-1", "WO-1", "Work", At,
            new EmsCommandContextDto("", "labor-key")));

        inactive.IsFailure.Should().BeTrue();
        inactive.Error.Code.Should().Be("EMS.MaintenanceExecution.WorkOrderNotActive");
        missingActor.IsFailure.Should().BeTrue();
        repository.Checks.Should().BeEmpty();
        repository.Labors.Should().BeEmpty();
    }

    private sealed class MemoryRepository : IMaintenanceExecutionRepository
    {
        public string? WorkOrderStatus { get; set; } = "InProgress";
        public string? WorkerId { get; set; }
        public List<MaintenanceCheckRecord> Checks { get; } = [];
        public List<MaintenanceLaborRecord> Labors { get; } = [];

        public Task<string?> GetWorkOrderStatusAsync(string workOrderId, CancellationToken ct = default)
            => Task.FromResult(WorkOrderStatus);

        public Task<bool> MaintenanceItemExistsAsync(string itemId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<string?> GetActiveWorkerIdAsync(string userId, DateTime at, CancellationToken ct = default)
            => Task.FromResult(WorkerId);

        public Task<MaintenanceCheckRecord?> GetCheckByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken ct = default)
            => Task.FromResult(Checks.SingleOrDefault(item => item.IdempotencyKey == idempotencyKey));

        public Task<bool> TryAddCheckAsync(MaintenanceCheckRecord record, CancellationToken ct = default)
        {
            if (Checks.Any(item => item.IdempotencyKey == record.IdempotencyKey
                                   || item.CheckResultId == record.CheckResultId
                                   || (item.WorkOrderId == record.WorkOrderId
                                       && item.ItemSequence == record.ItemSequence)))
                return Task.FromResult(false);
            Checks.Add(record);
            return Task.FromResult(true);
        }

        public Task<MaintenanceLaborRecord?> GetLaborAsync(string laborId, CancellationToken ct = default)
            => Task.FromResult(Labors.SingleOrDefault(item => item.LaborId == laborId));

        public Task<MaintenanceLaborRecord?> GetLaborByStartIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken ct = default)
            => Task.FromResult(Labors.SingleOrDefault(item => item.StartIdempotencyKey == idempotencyKey));

        public Task<MaintenanceLaborRecord?> GetLaborByEndIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken ct = default)
            => Task.FromResult(Labors.SingleOrDefault(item => item.EndIdempotencyKey == idempotencyKey));

        public Task<bool> TryStartLaborAsync(MaintenanceLaborRecord record, CancellationToken ct = default)
        {
            if (Labors.Any(item => item.LaborId == record.LaborId
                                   || item.StartIdempotencyKey == record.StartIdempotencyKey
                                   || (item.WorkOrderId == record.WorkOrderId
                                       && item.UserId == record.UserId && item.EndedAt is null)))
                return Task.FromResult(false);
            Labors.Add(record);
            return Task.FromResult(true);
        }

        public Task<bool> TryCompleteLaborAsync(
            MaintenanceLaborRecord record,
            int expectedVersion,
            CancellationToken ct = default)
        {
            var index = Labors.FindIndex(item => item.LaborId == record.LaborId);
            if (index < 0 || Labors[index].Version != expectedVersion
                          || Labors[index].EndedAt is not null
                          || Labors.Any(item => item.EndIdempotencyKey == record.EndIdempotencyKey))
                return Task.FromResult(false);
            Labors[index] = record;
            return Task.FromResult(true);
        }
    }
}
