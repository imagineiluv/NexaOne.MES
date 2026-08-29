using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.UnitTests.Ivt;

public sealed class TraceBindingServiceTests
{
    [Fact]
    public void Maintenance_gate_requires_explicit_mode_and_every_trace_writer_to_be_off()
    {
        static TraceMaintenanceGate Gate(params (string Key, string Value)[] values)
        {
            var settings = values.ToDictionary(item => item.Key, item => (string?)item.Value);
            return TraceMaintenanceGate.From(new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build());
        }

        Gate(("Ivt:TraceConfiguration:MaintenanceMode", "true")).IsOpen.Should().BeTrue();
        Gate().IsOpen.Should().BeFalse();
        Gate(("Ivt:TraceConfiguration:MaintenanceMode", "true"),
            ("Worker:Fdc:Enabled", "true")).IsOpen.Should().BeFalse();
        Gate(("Ivt:TraceConfiguration:MaintenanceMode", "true"),
            ("Worker:Fdc:Retention:Enabled", "true")).IsOpen.Should().BeFalse();
        Gate(("Ivt:TraceConfiguration:MaintenanceMode", "true"),
            ("Worker:Ivt:TraceMaterialConsumption:Enabled", "true")).IsOpen.Should().BeFalse();
        Gate(("Ivt:TraceConfiguration:MaintenanceMode", "true"),
            ("Ivt:TraceProjection:Enabled", "true")).IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Create_fails_closed_when_trace_configuration_is_not_in_quiesced_maintenance()
    {
        var repository = new MemoryRepository();
        var service = new TraceBindingService(
            repository,
            new EmptyTraceSource(),
            TraceMaintenanceGate.Closed("maintenance mode is disabled"));

        var result = await service.ExecuteAsync(CreateCommand());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IVT.TraceBinding.MaintenanceRequired");
        repository.Bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_records_an_active_versioned_binding_and_replays_the_same_command()
    {
        var repository = new MemoryRepository();
        var traceSource = new EmptyTraceSource();
        var service = new TraceBindingService(
            repository,
            traceSource,
            TraceMaintenanceGate.Open());
        var command = CreateCommand();

        var created = await service.ExecuteAsync(command);
        var replay = await service.ExecuteAsync(command);

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        created.Value.Should().BeEquivalentTo(new
        {
            BindingId = "BIND-1",
            IsActive = true,
            Version = 1,
            LastOperation = TraceBindingOperations.Create,
            ActorId = "operator-1",
            IsReplay = false,
        });
        replay.IsSuccess.Should().BeTrue(replay.IsFailure ? replay.Error.Description : string.Empty);
        replay.Value.Should().BeEquivalentTo(created.Value with { IsReplay = true });
        repository.Bindings.Should().ContainSingle();
        repository.Writes.Should().ContainSingle();
        traceSource.Scopes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ScopeId = "BIND-1",
            EquipmentId = "EQ-1",
            ParameterId = "FLOW-1",
            EffectiveFrom = DateTime.UnixEpoch,
        });
    }

    [Fact]
    public async Task Exact_committed_replay_remains_readable_after_maintenance_closes()
    {
        var repository = new MemoryRepository();
        var command = CreateCommand();
        var created = await new TraceBindingService(
            repository, new EmptyTraceSource(), TraceMaintenanceGate.Open())
            .ExecuteAsync(command);

        var replay = await new TraceBindingService(
            repository, new EmptyTraceSource(), TraceMaintenanceGate.Closed("normal operation"))
            .ExecuteAsync(command);

        created.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue(replay.IsFailure ? replay.Error.Description : string.Empty);
        replay.Value.Should().BeEquivalentTo(created.Value with { IsReplay = true });
    }

    [Fact]
    public async Task Create_rejects_a_start_before_the_V150_completeness_boundary()
    {
        var repository = new MemoryRepository();
        var boundary = DateTime.UnixEpoch.AddDays(1);
        var service = new TraceBindingService(
            repository,
            new GapTraceSource(boundary),
            TraceMaintenanceGate.Open());

        var result = await service.ExecuteAsync(CreateCommand());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IVT.TraceBinding.SourceGap");
        result.Error.Description.Should().Contain(boundary.ToString("o"));
        repository.Bindings.Should().BeEmpty();
        repository.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_rejects_values_that_do_not_fit_decimal_18_6_before_repository_write()
    {
        var repository = new MemoryRepository();
        var service = new TraceBindingService(
            repository,
            new EmptyTraceSource(),
            TraceMaintenanceGate.Open());

        var tooSmall = await service.ExecuteAsync(CreateCommand() with
        {
            IdempotencyKey = "binding:small",
            SourceEventId = "binding-small",
            ScaleFactor = 0.0000001m,
        });
        var tooLarge = await service.ExecuteAsync(CreateCommand() with
        {
            IdempotencyKey = "binding:large",
            SourceEventId = "binding-large",
            ScaleFactor = 1_000_000_000_000m,
        });

        tooSmall.IsFailure.Should().BeTrue();
        tooLarge.IsFailure.Should().BeTrue();
        repository.Bindings.Should().BeEmpty();
        repository.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Retire_uses_revision_cas_and_appends_a_replayable_audit_command()
    {
        var repository = new MemoryRepository();
        var service = new TraceBindingService(
            repository,
            new EmptyTraceSource(),
            TraceMaintenanceGate.Open());
        _ = await service.ExecuteAsync(CreateCommand());
        var retiredAt = DateTime.UnixEpoch.AddDays(1);
        var command = new TraceBindingCommand(
            TraceBindingOperations.Retire,
            "BIND-1",
            1,
            "binding:retire:1",
            "MES",
            "binding-event-2",
            retiredAt,
            retiredAt,
            ActorId: "maintainer-1",
            CorrelationId: "change-42",
            Reason: "Replace flow meter");

        var retired = await service.ExecuteAsync(command);
        var replay = await service.ExecuteAsync(command);
        var stale = await service.ExecuteAsync(command with
        {
            IdempotencyKey = "binding:retire:stale",
            SourceEventId = "binding-event-stale",
        });

        retired.IsSuccess.Should().BeTrue(retired.IsFailure ? retired.Error.Description : string.Empty);
        retired.Value.Should().BeEquivalentTo(new
        {
            BindingId = "BIND-1",
            EffectiveTo = (DateTime?)retiredAt,
            IsActive = false,
            Version = 2,
            LastOperation = TraceBindingOperations.Retire,
            ActorId = "maintainer-1",
            CorrelationId = "change-42",
            Reason = "Replace flow meter",
            IsReplay = false,
        });
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(retired.Value with { IsReplay = true });
        stale.IsFailure.Should().BeTrue();
        stale.Error.Code.Should().Be("IVT.TraceBinding.VersionConflict");
        repository.Writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Retire_rejects_raw_trace_that_has_not_reached_the_durable_ingestion_cursor()
    {
        var repository = new MemoryRepository();
        var created = await new TraceBindingService(
            repository, new EmptyTraceSource(), TraceMaintenanceGate.Open())
            .ExecuteAsync(CreateCommand());
        var retiredAt = DateTime.UnixEpoch.AddDays(1);
        var pending = new FdcTraceSample(
            "BIND-1", "COLLECT-PENDING", "EQ-1", "FLOW-1", 5m, "Good",
            retiredAt.AddSeconds(-1));
        var service = new TraceBindingService(
            repository, new FixedTraceSource(pending), TraceMaintenanceGate.Open());

        var retired = await service.ExecuteAsync(new TraceBindingCommand(
            TraceBindingOperations.Retire,
            "BIND-1",
            1,
            "binding:retire:pending",
            "MES",
            "binding-event-pending",
            retiredAt,
            retiredAt,
            ActorId: "maintainer-1"));

        created.IsSuccess.Should().BeTrue();
        retired.IsFailure.Should().BeTrue();
        retired.Error.Code.Should().Be("IVT.TraceBinding.DrainRequired");
        repository.Bindings["BIND-1"].IsActive.Should().BeTrue();
        repository.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Retire_rejects_a_future_cutoff_before_immediately_deactivating_the_binding()
    {
        var repository = new MemoryRepository();
        var service = new TraceBindingService(
            repository, new EmptyTraceSource(), TraceMaintenanceGate.Open());
        (await service.ExecuteAsync(CreateCommand())).IsSuccess.Should().BeTrue();

        var future = DateTime.UtcNow.AddMinutes(5);
        var retired = await service.ExecuteAsync(new TraceBindingCommand(
            TraceBindingOperations.Retire,
            "BIND-1",
            1,
            "binding:retire:future",
            "MES",
            "binding-event-future",
            DateTime.UtcNow,
            future,
            ActorId: "maintainer-1",
            Reason: "scheduled replacement"));

        retired.IsFailure.Should().BeTrue();
        retired.Error.Code.Should().Be("IVT.TraceBinding.FutureRetire");
        repository.Bindings["BIND-1"].IsActive.Should().BeTrue();
        repository.Writes.Should().ContainSingle();
    }

    private static TraceBindingCommand CreateCommand() => new(
        TraceBindingOperations.Create,
        "BIND-1",
        0,
        "binding:create:1",
        "MES",
        "binding-event-1",
        DateTime.UnixEpoch.AddHours(1),
        DateTime.UnixEpoch,
        PlantId: "PLANT-1",
        EquipmentId: "EQ-1",
        ParameterId: "FLOW-1",
        FeedPointId: "FEED-1",
        CalculationMode: "CounterDelta",
        ScaleFactor: 1m,
        OutputUnit: "kg",
        ActorId: "operator-1");

    private sealed class MemoryRepository : ITraceBindingRepository
    {
        public Dictionary<string, TraceBindingState> Bindings { get; } =
            new(StringComparer.Ordinal);
        public List<TraceBindingWrite> Writes { get; } = [];

        public Task<TraceBindingState?> GetAsync(string bindingId, CancellationToken ct = default) =>
            Task.FromResult(Bindings.GetValueOrDefault(bindingId));

        public Task<TraceBindingCursor?> GetIngestionCursorAsync(
            string bindingId,
            CancellationToken ct = default) => Task.FromResult<TraceBindingCursor?>(null);

        public Task<TraceBindingWrite?> GetByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken ct = default) => Task.FromResult(
                Writes.SingleOrDefault(write => write.IdempotencyKey == idempotencyKey));

        public Task<TraceBindingWrite?> GetBySourceEventAsync(
            string sourceSystem,
            string sourceEventId,
            CancellationToken ct = default) => Task.FromResult(
                Writes.SingleOrDefault(write => write.SourceSystem == sourceSystem
                                                && write.SourceEventId == sourceEventId));

        public Task<bool> TryCreateAsync(
            TraceBindingState binding,
            TraceBindingWrite write,
            CancellationToken ct = default)
        {
            Bindings.Add(binding.BindingId, binding);
            Writes.Add(write);
            return Task.FromResult(true);
        }

        public Task<bool> TryRetireAsync(
            TraceBindingState binding,
            int expectedVersion,
            TraceBindingWrite write,
            CancellationToken ct = default)
        {
            if (!Bindings.TryGetValue(binding.BindingId, out var current)
                || current.Version != expectedVersion
                || !current.IsActive)
            {
                return Task.FromResult(false);
            }

            Bindings[binding.BindingId] = binding;
            Writes.Add(write);
            return Task.FromResult(true);
        }
    }

    private sealed class EmptyTraceSource : IFdcTraceSource
    {
        public List<FdcTraceReadScope> Scopes { get; } = [];

        public Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
            IReadOnlyCollection<FdcTraceReadScope> scopes,
            int maxCount,
            CancellationToken ct = default)
        {
            Scopes.AddRange(scopes);
            return Task.FromResult<IReadOnlyList<FdcTraceSample>>([]);
        }
    }

    private sealed class GapTraceSource(DateTime completenessBoundary) : IFdcTraceSource
    {
        public Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
            IReadOnlyCollection<FdcTraceReadScope> scopes,
            int maxCount,
            CancellationToken ct = default)
        {
            var scope = scopes.Single();
            throw new FdcTraceGapException(
                scope.ScopeId,
                scope.EffectiveFrom,
                completenessBoundary);
        }
    }

    private sealed class FixedTraceSource(params FdcTraceSample[] samples) : IFdcTraceSource
    {
        public Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
            IReadOnlyCollection<FdcTraceReadScope> scopes,
            int maxCount,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FdcTraceSample>>(samples.Take(maxCount).ToArray());
    }
}
