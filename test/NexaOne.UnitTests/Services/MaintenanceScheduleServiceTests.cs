using NexaOne.EMS.Application.MaintenanceSchedules;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.UnitTests.Services;

public sealed class MaintenanceScheduleServiceTests
{
    [Fact]
    public async Task Create_validates_trigger_fields_and_rejects_unimplemented_auto_work_order()
    {
        var repository = new MemoryRepository("PLAN-CAL", "PLAN-METER", "PLAN-COND", "PLAN-AUTO");
        var service = new MaintenanceScheduleService(repository);
        var due = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

        var calendar = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "CAL", "PLAN-CAL", "calendar", 1m, "day", NextDueAt: due, ActorId: "planner"));
        var meter = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "METER", "PLAN-METER", "Meter", MeterParameterId: "RUN-HOURS",
            MeterThreshold: 100m, MeterBaselineValue: 500m, NextMeterDueValue: 600m,
            ActorId: "planner"));
        var condition = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "COND", "PLAN-COND", "Condition", ConditionRuleId: "RULE-VIBRATION",
            ActorId: "planner"));
        var auto = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "AUTO", "PLAN-AUTO", "Calendar", 1m, "Day", NextDueAt: due,
            AutoCreateWorkOrder: true, ActorId: "planner"));
        var mixed = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "MIXED", "PLAN-CAL", "Calendar", 1m, "Day", NextDueAt: due,
            MeterParameterId: "RUN-HOURS", ActorId: "planner"));

        calendar.IsSuccess.Should().BeTrue();
        calendar.Value.TriggerType.Should().Be("Calendar");
        calendar.Value.IntervalUnit.Should().Be("Day");
        meter.IsSuccess.Should().BeTrue();
        condition.IsSuccess.Should().BeTrue();
        auto.IsFailure.Should().BeTrue();
        auto.Error.Code.Should().Be("EMS.MaintenanceSchedule.AutoWorkOrderUnavailable");
        mixed.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Create_requires_a_preventive_maintenance_plan()
    {
        var service = new MaintenanceScheduleService(new MemoryRepository("PLAN-PM"));

        var result = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "BM-SCHEDULE", "PLAN-BM", "Calendar", 1m, "Day",
            NextDueAt: DateTime.UtcNow.AddDays(1), ActorId: "planner"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.MaintenanceSchedule.PreventivePlanRequired");
    }

    [Fact]
    public async Task Update_uses_version_guard_and_preserves_authenticated_audit()
    {
        var repository = new MemoryRepository("PLAN-1");
        var service = new MaintenanceScheduleService(repository);
        var due = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        var created = await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "CAL", "PLAN-1", "Calendar", 1m, "Day", NextDueAt: due, ActorId: "creator"));

        var updated = await service.UpdateAsync(new MaintenanceScheduleUpdateCommand(
            "CAL", 1, "PLAN-1", "Calendar", 2m, "Day", NextDueAt: due,
            ActorId: "editor"));
        var stale = await service.UpdateAsync(new MaintenanceScheduleUpdateCommand(
            "CAL", 1, "PLAN-1", "Calendar", 3m, "Day", NextDueAt: due,
            ActorId: "editor"));

        created.IsSuccess.Should().BeTrue();
        updated.IsSuccess.Should().BeTrue();
        updated.Value.Version.Should().Be(2);
        updated.Value.CreatedBy.Should().Be("creator");
        updated.Value.UpdatedBy.Should().Be("editor");
        stale.IsFailure.Should().BeTrue();
        stale.Error.Code.Should().Be("EMS.MaintenanceSchedule.VersionConflict");
    }

    [Fact]
    public async Task Calendar_acknowledgement_advances_cadence_and_is_idempotent()
    {
        var repository = new MemoryRepository("PLAN-1");
        var service = new MaintenanceScheduleService(repository);
        var due = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "CAL", "PLAN-1", "Calendar", 1m, "Day", NextDueAt: due, ActorId: "planner"));
        var command = new MaintenanceScheduleAcknowledgeCommand(
            "CAL", 1, "ack-calendar", due.AddDays(2).AddHours(12),
            ClientChannel: "POP", DeviceId: "PANEL-01", CorrelationId: "corr-cal",
            Remark: "manual PM complete", ActorId: "operator-7");

        var first = await service.AcknowledgeAsync(command);
        var replay = await service.AcknowledgeAsync(command);
        var conflict = await service.AcknowledgeAsync(command with { Remark = "changed" });

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        first.Value.DueAt.Should().Be(due);
        first.Value.NextDueAt.Should().Be(due.AddDays(3));
        first.Value.AcknowledgedBy.Should().Be("operator-7");
        first.Value.FromVersion.Should().Be(1);
        first.Value.ToVersion.Should().Be(2);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.AcknowledgementId.Should().Be(first.Value.AcknowledgementId);
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.MaintenanceSchedule.IdempotencyConflict");
        repository.Acknowledgements.Should().ContainSingle();
    }

    [Fact]
    public async Task Meter_and_condition_acknowledgements_require_due_evidence()
    {
        var repository = new MemoryRepository("PLAN-METER", "PLAN-COND");
        var service = new MaintenanceScheduleService(repository);
        await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "METER", "PLAN-METER", "Meter", MeterParameterId: "RUN-HOURS",
            MeterThreshold: 100m, MeterBaselineValue: 500m, NextMeterDueValue: 600m,
            ActorId: "planner"));
        await service.CreateAsync(new MaintenanceScheduleCreateCommand(
            "COND", "PLAN-COND", "Condition", ConditionRuleId: "RULE-VIBRATION",
            ActorId: "planner"));
        var at = new DateTime(2026, 8, 26, 5, 0, 0, DateTimeKind.Utc);

        var meterEarly = await service.AcknowledgeAsync(new MaintenanceScheduleAcknowledgeCommand(
            "METER", 1, "meter-early", at, ObservedMeterValue: 599m, ActorId: "operator"));
        var meterDue = await service.AcknowledgeAsync(new MaintenanceScheduleAcknowledgeCommand(
            "METER", 1, "meter-due", at, ObservedMeterValue: 625m, ActorId: "operator"));
        var conditionFalse = await service.AcknowledgeAsync(new MaintenanceScheduleAcknowledgeCommand(
            "COND", 1, "condition-false", at, ConditionMet: false, ActorId: "operator"));
        var conditionDue = await service.AcknowledgeAsync(new MaintenanceScheduleAcknowledgeCommand(
            "COND", 1, "condition-due", at, ConditionMet: true, ActorId: "operator"));

        meterEarly.IsFailure.Should().BeTrue();
        meterEarly.Error.Code.Should().Be("EMS.MaintenanceSchedule.NotDue");
        meterDue.IsSuccess.Should().BeTrue();
        meterDue.Value.MeterDueValue.Should().Be(600m);
        meterDue.Value.NextMeterDueValue.Should().Be(725m);
        conditionFalse.IsFailure.Should().BeTrue();
        conditionDue.IsSuccess.Should().BeTrue();
        conditionDue.Value.ConditionRuleId.Should().Be("RULE-VIBRATION");
    }

    private sealed class MemoryRepository : IMaintenanceScheduleRepository
    {
        private readonly HashSet<string> _plans;
        private readonly Dictionary<string, MaintenanceScheduleRecord> _schedules = new();
        public List<MaintenanceScheduleAcknowledgementRecord> Acknowledgements { get; } = new();

        public MemoryRepository(params string[] plans) => _plans = plans.ToHashSet(StringComparer.Ordinal);

        public Task<bool> MaintenancePlanExistsAsync(string maintenancePlanId, CancellationToken ct = default)
            => Task.FromResult(_plans.Contains(maintenancePlanId));

        public Task<MaintenanceScheduleRecord?> GetAsync(string scheduleId, CancellationToken ct = default)
            => Task.FromResult(_schedules.GetValueOrDefault(scheduleId));

        public Task<bool> TryCreateAsync(MaintenanceScheduleRecord schedule, CancellationToken ct = default)
        {
            if (_schedules.ContainsKey(schedule.ScheduleId)
                || _schedules.Values.Any(item => item.MaintenancePlanId == schedule.MaintenancePlanId))
                return Task.FromResult(false);
            _schedules.Add(schedule.ScheduleId, schedule);
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateAsync(
            MaintenanceScheduleRecord schedule,
            int expectedVersion,
            CancellationToken ct = default)
        {
            if (!_schedules.TryGetValue(schedule.ScheduleId, out var current)
                || current.Version != expectedVersion
                || _schedules.Values.Any(item => item.ScheduleId != schedule.ScheduleId
                                                  && item.MaintenancePlanId == schedule.MaintenancePlanId))
                return Task.FromResult(false);
            _schedules[schedule.ScheduleId] = schedule;
            return Task.FromResult(true);
        }

        public Task<MaintenanceScheduleAcknowledgementRecord?> GetAcknowledgementAsync(
            string idempotencyKey,
            CancellationToken ct = default)
            => Task.FromResult(Acknowledgements.SingleOrDefault(item => item.IdempotencyKey == idempotencyKey));

        public Task<bool> TryAcknowledgeAsync(
            MaintenanceScheduleRecord schedule,
            int expectedVersion,
            MaintenanceScheduleAcknowledgementRecord acknowledgement,
            CancellationToken ct = default)
        {
            if (!_schedules.TryGetValue(schedule.ScheduleId, out var current)
                || current.Version != expectedVersion
                || Acknowledgements.Any(item => item.IdempotencyKey == acknowledgement.IdempotencyKey))
                return Task.FromResult(false);
            _schedules[schedule.ScheduleId] = schedule;
            Acknowledgements.Add(acknowledgement);
            return Task.FromResult(true);
        }
    }
}
