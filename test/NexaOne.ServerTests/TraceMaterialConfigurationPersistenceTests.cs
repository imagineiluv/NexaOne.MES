using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class TraceMaterialConfigurationPersistenceTests :
    IClassFixture<TraceMaterialConfigurationPersistenceTests.ConfigurationFactory>
{
    private readonly ConfigurationFactory _factory;

    public TraceMaterialConfigurationPersistenceTests(ConfigurationFactory factory) => _factory = factory;

    public sealed class ConfigurationFactory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(), $"nexaone-trace-material-config-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False;Default Timeout=10";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "trace-material-config-secret-key-32-bytes!!!!");
            builder.UseSetting("Jwt:Issuer", "trace-material-config-test");
            builder.UseSetting("Jwt:Audience", "trace-material-config-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    [Fact]
    public async Task Binding_create_replay_and_retire_survive_new_service_instances()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var command = CreateBinding(suffix);

        var created = await BindingService().ExecuteAsync(command);
        var replay = await BindingService().ExecuteAsync(command);
        var retiredAt = command.EffectiveAt.AddHours(1);
        var retired = await BindingService().ExecuteAsync(new TraceBindingCommand(
            TraceBindingOperations.Retire,
            command.BindingId,
            1,
            $"binding-retire:{suffix}",
            "TEST",
            $"binding-retire-source:{suffix}",
            retiredAt,
            retiredAt,
            ActorId: "maintainer"));

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue(replay.IsFailure ? replay.Error.Description : string.Empty);
        replay.Value.IsReplay.Should().BeTrue();
        replay.Value.EffectiveFrom.Kind.Should().Be(DateTimeKind.Utc);
        replay.Value.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        retired.IsSuccess.Should().BeTrue(retired.IsFailure ? retired.Error.Description : string.Empty);
        retired.Value.Should().BeEquivalentTo(new
        {
            IsActive = false,
            Version = 2,
            EffectiveTo = (DateTime?)retiredAt,
        });
    }

    [Fact]
    public async Task Retired_binding_rejects_a_new_effective_window_that_overlaps_its_history()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var original = CreateBinding(suffix);
        (await BindingService().ExecuteAsync(original)).IsSuccess.Should().BeTrue();
        var retiredAt = original.EffectiveAt.AddHours(2);
        (await BindingService().ExecuteAsync(new TraceBindingCommand(
            TraceBindingOperations.Retire, original.BindingId, 1,
            $"binding-retire-overlap:{suffix}", "TEST", $"binding-retire-overlap-source:{suffix}",
            retiredAt, retiredAt, ActorId: "maintainer"))).IsSuccess.Should().BeTrue();

        var overlap = await BindingService().ExecuteAsync(original with
        {
            BindingId = $"B-overlap-{suffix}",
            IdempotencyKey = $"binding-overlap:{suffix}",
            SourceEventId = $"binding-overlap-source:{suffix}",
            OccurredAt = original.OccurredAt.AddHours(1),
            EffectiveAt = original.EffectiveAt.AddHours(1),
        });
        var boundary = await BindingService().ExecuteAsync(original with
        {
            BindingId = $"B-boundary-{suffix}",
            IdempotencyKey = $"binding-boundary:{suffix}",
            SourceEventId = $"binding-boundary-source:{suffix}",
            OccurredAt = retiredAt,
            EffectiveAt = retiredAt,
        });

        overlap.IsFailure.Should().BeTrue();
        overlap.Error.Code.Should().Be("IVT.TraceBinding.CreateConflict");
        boundary.IsSuccess.Should().BeTrue(boundary.IsFailure ? boundary.Error.Description : string.Empty);
    }

    [Fact]
    public async Task Future_retire_cutoff_does_not_deactivate_the_persisted_binding()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var binding = CreateBinding(suffix);
        (await BindingService().ExecuteAsync(binding)).IsSuccess.Should().BeTrue();
        var future = DateTime.UtcNow.AddMinutes(5);

        var retired = await BindingService().ExecuteAsync(new TraceBindingCommand(
            TraceBindingOperations.Retire, binding.BindingId, 1,
            $"binding-retire-future:{suffix}", "TEST", $"binding-retire-future-source:{suffix}",
            DateTime.UtcNow, future, ActorId: "maintainer", Reason: "scheduled change"));

        retired.IsFailure.Should().BeTrue();
        retired.Error.Code.Should().Be("IVT.TraceBinding.FutureRetire");
        Scalar<long>(
                "SELECT IS_ACTIVE FROM IVT_TRACE_CONSUMPTION_BINDING WHERE BINDING_ID=@id",
                ("@id", binding.BindingId))
            .Should().Be(1);
        Scalar<long>(
                "SELECT COUNT(*) FROM IVT_TRACE_BINDING_COMMAND WHERE BINDING_ID=@id",
                ("@id", binding.BindingId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Retire_requires_real_fdc_raw_backlog_to_reach_the_durable_ivt_cursor()
    {
        // This scenario exercises the real source over every active binding. Keep its database
        // isolated so active bindings intentionally left by other persistence tests cannot add
        // unrelated scopes (or make the result depend on xUnit execution order).
        using var isolatedFactory = new ConfigurationFactory();
        using var client = isolatedFactory.CreateClient();
        var dataSource = new EesDataSource
        {
            Provider = isolatedFactory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = isolatedFactory.ConnectionString,
        };
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var effectiveFrom = DateTime.UtcNow;
        var command = CreateBinding(suffix) with
        {
            OccurredAt = effectiveFrom,
            EffectiveAt = effectiveFrom,
        };
        var configurationService = new TraceBindingService(
            new TraceBindingRepository(dataSource),
            new EmptyTraceSource(),
            TraceMaintenanceGate.Open());
        (await configurationService.ExecuteAsync(command)).IsSuccess.Should().BeTrue();
        var collectedAt = NextUtcAfter(command.EffectiveAt);
        ExecuteSql(isolatedFactory.ConnectionString, """
            INSERT INTO FDC_COLLECT_DATA
              (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY,
               LOWER_LIMIT, UPPER_LIMIT)
            VALUES (@collectId, @equipmentId, @parameterId, 7, @collectedAt, 'Good', 0, 100);
            """,
            ("@collectId", $"COL-{suffix}"),
            ("@equipmentId", command.EquipmentId!),
            ("@parameterId", command.ParameterId!),
            ("@collectedAt", DbTimestamp(collectedAt)));

        var traceSource = new FdcTraceSource(
            new FdcCollectDataRepository(dataSource, new SqliteEesDbCapability()));
        var service = new TraceBindingService(
            new TraceBindingRepository(dataSource), traceSource, TraceMaintenanceGate.Open());
        var retiredAt = NextUtcAfter(collectedAt);
        var retire = new TraceBindingCommand(
            TraceBindingOperations.Retire, command.BindingId, 1,
            $"binding-retire-drain:{suffix}", "TEST", $"binding-retire-drain-source:{suffix}",
            retiredAt, retiredAt, ActorId: "maintainer");

        var beforeDrain = await service.ExecuteAsync(retire);
        var ingested = await new TraceIngestionService(
                traceSource,
                new TraceProjectionRepository(dataSource, new SqliteEesDbCapability()))
            .EnqueueAsync(100);
        var afterDrain = await service.ExecuteAsync(retire);

        beforeDrain.IsFailure.Should().BeTrue();
        beforeDrain.Error.Code.Should().Be("IVT.TraceBinding.DrainRequired");
        ingested.Should().BeGreaterThanOrEqualTo(1);
        afterDrain.IsSuccess.Should().BeTrue(afterDrain.IsFailure ? afterDrain.Error.Description : string.Empty);
    }

    [Fact]
    public async Task Concurrent_active_binding_for_the_same_source_allows_one_winner()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var first = CreateBinding(suffix) with { BindingId = $"B1-{suffix}" };
        var second = CreateBinding(suffix) with
        {
            BindingId = $"B2-{suffix}",
            PlantId = "PLANT-OTHER",
            FeedPointId = "FEED-OTHER",
            IdempotencyKey = $"binding-create-2:{suffix}",
            SourceEventId = $"binding-source-2:{suffix}",
        };

        var results = await Task.WhenAll(
            BindingService().ExecuteAsync(first),
            BindingService().ExecuteAsync(second));

        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Count(result => result.IsFailure).Should().Be(1);
        results.Single(result => result.IsFailure).Error.Code
            .Should().Be("IVT.TraceBinding.CreateConflict");
    }

    [Fact]
    public async Task Binding_idempotency_and_source_event_identities_are_ordinal_case_sensitive()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var first = CreateBinding(suffix) with
        {
            IdempotencyKey = $"Binding-Key:{suffix}",
            SourceSystem = "MES-Case",
            SourceEventId = $"Binding-Event:{suffix}",
        };
        var keyCaseVariant = first with
        {
            BindingId = $"BK-{suffix}",
            EquipmentId = $"EQ-K-{suffix}",
            IdempotencyKey = first.IdempotencyKey.ToLowerInvariant(),
            SourceEventId = $"binding-event-key:{suffix}",
        };
        var sourceCaseVariant = first with
        {
            BindingId = $"BS-{suffix}",
            EquipmentId = $"EQ-S-{suffix}",
            IdempotencyKey = $"binding-source-case:{suffix}",
            SourceSystem = first.SourceSystem.ToLowerInvariant(),
            SourceEventId = first.SourceEventId.ToLowerInvariant(),
        };

        var results = new[]
        {
            await BindingService().ExecuteAsync(first),
            await BindingService().ExecuteAsync(keyCaseVariant),
            await BindingService().ExecuteAsync(sourceCaseVariant),
        };

        results.Should().OnlyContain(result => result.IsSuccess,
            "opaque idempotency/source identities use ordinal semantics in SQLite and MSSQL");
    }

    [Fact]
    public async Task Binding_command_ledger_rejects_update_delete_and_replace()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var command = CreateBinding(suffix);
        var result = await BindingService().ExecuteAsync(command);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);

        AssertLedgerIsAppendOnly(
            "IVT_TRACE_BINDING_COMMAND",
            "BINDING_ID",
            command.BindingId);
        Action invalidSnapshot = () => ExecuteLedgerMutation("""
            INSERT INTO IVT_TRACE_BINDING_COMMAND
            SELECT 'BAD-' || COMMAND_ID, COMMAND_TYPE, 'BAD-' || IDEMPOTENCY_KEY, REQUEST_HASH,
                   BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
                   CALCULATION_MODE, SCALE_FACTOR, PULSE_QUANTITY, OUTPUT_UNIT,
                   EFFECTIVE_FROM, datetime(EFFECTIVE_FROM, '+1 hour'), RESULT_IS_ACTIVE,
                   EXPECTED_VERSION, RESULT_VERSION, ACTOR_ID, OCCURRED_AT,
                   SOURCE_SYSTEM, 'BAD-' || SOURCE_EVENT_ID, CORRELATION_ID, REASON,
                   CREATED_BY, CREATED_AT
              FROM IVT_TRACE_BINDING_COMMAND
             WHERE BINDING_ID=@aggregateId LIMIT 1;
            """, command.BindingId);
        invalidSnapshot.Should().Throw<SqliteException>().WithMessage("*CHECK constraint failed*");
    }

    [Fact]
    public async Task Sqlite_command_ledgers_keep_their_binding_and_feed_session_parents()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var binding = CreateBinding(suffix);
        (await BindingService().ExecuteAsync(binding)).IsSuccess.Should().BeTrue();
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);
        (await FeedService().ExecuteAsync(mount)).IsSuccess.Should().BeTrue();

        Action deleteBinding = () => ExecuteSql(
            "DELETE FROM IVT_TRACE_CONSUMPTION_BINDING WHERE BINDING_ID=@id;",
            ("@id", binding.BindingId));
        Action deleteSession = () => ExecuteSql(
            "DELETE FROM IVT_MATERIAL_FEED_SESSION WHERE FEED_SESSION_ID=@id;",
            ("@id", mount.FeedSessionId));

        deleteBinding.Should().Throw<SqliteException>().WithMessage("*command history*");
        deleteSession.Should().Throw<SqliteException>().WithMessage("*command history*");
    }

    [Fact]
    public async Task Feed_unmount_closes_the_interval_but_keeps_the_lot_reserved_pending_drain()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);

        var mounted = await FeedService().ExecuteAsync(mount);
        var replay = await FeedService().ExecuteAsync(mount);
        var unmounted = await FeedService().ExecuteAsync(CloseFeed(
            FeedSessionOperations.Unmount, suffix, mount.FeedSessionId, "unmount"));
        var secondMount = MountFeed(suffix, lotId) with
        {
            FeedSessionId = $"FS2-{suffix}",
            IdempotencyKey = $"feed-mount-2:{suffix}",
            SourceEventId = $"feed-mount-source-2:{suffix}",
            OccurredAt = mount.OccurredAt.AddHours(2),
        };
        var remounted = await FeedService().ExecuteAsync(secondMount);

        mounted.IsSuccess.Should().BeTrue(mounted.IsFailure ? mounted.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        replay.Value.MountedAt.Kind.Should().Be(DateTimeKind.Utc);
        replay.Value.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        unmounted.IsSuccess.Should().BeTrue(unmounted.IsFailure ? unmounted.Error.Description : string.Empty);
        unmounted.Value.Should().BeEquivalentTo(new { Status = "Unmounted", Version = 2 });
        remounted.IsFailure.Should().BeTrue();
        remounted.Error.Code.Should().Be("IVT.FeedSession.MountConflict");
        Scalar<string>(
                "SELECT ACTIVE_FEED_SESSION_ID FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id",
                ("@id", lotId))
            .Should().Be(mount.FeedSessionId);
    }

    [Fact]
    public async Task Future_feed_events_do_not_reserve_or_release_material_lots()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);

        var futureMount = await FeedService().ExecuteAsync(mount with
        {
            OccurredAt = DateTime.UtcNow.AddMinutes(5),
        });
        futureMount.IsFailure.Should().BeTrue();
        Scalar<long>(
                "SELECT COUNT(*) FROM IVT_MATERIAL_FEED_SESSION WHERE FEED_SESSION_ID=@id",
                ("@id", mount.FeedSessionId))
            .Should().Be(0);
        Scalar<long>(
                "SELECT COUNT(*) FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id AND ACTIVE_FEED_SESSION_ID IS NOT NULL",
                ("@id", lotId))
            .Should().Be(0);

        (await FeedService().ExecuteAsync(mount)).IsSuccess.Should().BeTrue();
        var futureUnmount = await FeedService().ExecuteAsync(CloseFeed(
            FeedSessionOperations.Unmount, suffix, mount.FeedSessionId,
            "future-unmount", DateTime.UtcNow.AddMinutes(5)));

        futureUnmount.IsFailure.Should().BeTrue();
        Scalar<string>(
                "SELECT ACTIVE_FEED_SESSION_ID FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id",
                ("@id", lotId))
            .Should().Be(mount.FeedSessionId);
        Scalar<string>(
                "SELECT STATUS FROM IVT_MATERIAL_FEED_SESSION WHERE FEED_SESSION_ID=@id",
                ("@id", mount.FeedSessionId))
            .Should().Be("Mounted");
    }

    [Fact]
    public async Task Closed_feed_session_rejects_a_backdated_overlapping_mount()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var first = MountFeed(suffix, lotId);
        (await FeedService().ExecuteAsync(first)).IsSuccess.Should().BeTrue();
        var unmountedAt = first.OccurredAt.AddHours(1);
        (await FeedService().ExecuteAsync(CloseFeed(
            FeedSessionOperations.Unmount, suffix, first.FeedSessionId, "unmount-overlap",
            unmountedAt))).IsSuccess.Should().BeTrue();
        var nextLotSuffix = $"{suffix}n";
        var nextLotId = await ReceiveMaterialLot(nextLotSuffix);

        var overlap = await FeedService().ExecuteAsync(first with
        {
            FeedSessionId = $"FS-overlap-{suffix}",
            IdempotencyKey = $"feed-overlap:{suffix}",
            SourceEventId = $"feed-overlap-source:{suffix}",
            OccurredAt = first.OccurredAt.AddMinutes(30),
            MaterialLotId = nextLotId,
            MaterialId = $"MAT-{nextLotSuffix}",
        });
        var boundary = await FeedService().ExecuteAsync(first with
        {
            FeedSessionId = $"FS-boundary-{suffix}",
            IdempotencyKey = $"feed-boundary:{suffix}",
            SourceEventId = $"feed-boundary-source:{suffix}",
            OccurredAt = unmountedAt,
            MaterialLotId = nextLotId,
            MaterialId = $"MAT-{nextLotSuffix}",
        });

        overlap.IsFailure.Should().BeTrue();
        overlap.Error.Code.Should().Be("IVT.FeedSession.MountConflict");
        boundary.IsSuccess.Should().BeTrue(boundary.IsFailure ? boundary.Error.Description : string.Empty);
    }

    [Theory]
    [InlineData(MaterialLotOperations.Move)]
    [InlineData(MaterialLotOperations.Hold)]
    [InlineData(MaterialLotOperations.Scrap)]
    [InlineData(MaterialLotOperations.Adjustment)]
    public async Task Mounted_material_lot_rejects_inventory_lifecycle_mutations(string operation)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);
        (await FeedService().ExecuteAsync(mount)).IsSuccess.Should().BeTrue();
        var command = new MaterialLotCommand(
            $"lot-{operation}:{suffix}",
            $"lot-{operation}-key:{suffix}",
            operation,
            lotId,
            1,
            mount.OccurredAt.AddMinutes(1),
            "TEST",
            $"lot-{operation}-source:{suffix}",
            Quantity: operation == MaterialLotOperations.Scrap ? 1m
                : operation == MaterialLotOperations.Adjustment ? -1m : null,
            Location: operation == MaterialLotOperations.Move ? "LINE-02" : null,
            Reason: operation is MaterialLotOperations.Hold or MaterialLotOperations.Scrap
                or MaterialLotOperations.Adjustment ? "mounted guard" : null,
            ActorId: "material-operator");

        var result = await new MaterialLotService(new MaterialLotRepository(DataSource()))
            .ExecuteAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IVT.MaterialLot.MountedConflict");
        Scalar<long>("SELECT VERSION_NO FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id", ("@id", lotId))
            .Should().Be(1);
        Scalar<string>(
                "SELECT ACTIVE_FEED_SESSION_ID FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id",
                ("@id", lotId))
            .Should().Be(mount.FeedSessionId);
    }

    [Theory]
    [InlineData(MaterialLotOperations.Move)]
    [InlineData(MaterialLotOperations.Hold)]
    [InlineData(MaterialLotOperations.Scrap)]
    [InlineData(MaterialLotOperations.Adjustment)]
    public async Task Unmounted_material_lot_stays_reserved_until_a_durable_trace_drain_can_be_finalized(
        string operation)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);
        (await FeedService().ExecuteAsync(mount)).IsSuccess.Should().BeTrue();
        var unmount = await FeedService().ExecuteAsync(CloseFeed(
            FeedSessionOperations.Unmount, suffix, mount.FeedSessionId, "pending-drain"));
        unmount.IsSuccess.Should().BeTrue(unmount.IsFailure ? unmount.Error.Description : string.Empty);

        var command = new MaterialLotCommand(
            $"lot-pending-{operation}:{suffix}", $"lot-pending-{operation}-key:{suffix}",
            operation, lotId, 1, mount.OccurredAt.AddHours(2),
            "TEST", $"lot-pending-{operation}-source:{suffix}",
            Quantity: operation == MaterialLotOperations.Scrap ? 1m
                : operation == MaterialLotOperations.Adjustment ? -1m : null,
            Location: operation == MaterialLotOperations.Move ? "LINE-02" : null,
            Reason: operation is MaterialLotOperations.Hold or MaterialLotOperations.Scrap
                or MaterialLotOperations.Adjustment ? "pending drain guard" : null,
            ActorId: "material-operator");

        var result = await new MaterialLotService(new MaterialLotRepository(DataSource()))
            .ExecuteAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IVT.MaterialLot.MountedConflict");
        Scalar<string>(
                "SELECT ACTIVE_FEED_SESSION_ID FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id",
                ("@id", lotId))
            .Should().Be(mount.FeedSessionId);
    }

    [Fact]
    public async Task Trace_captured_before_unmount_can_post_after_unmount_without_releasing_the_lot()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);
        (await FeedService().ExecuteAsync(mount)).IsSuccess.Should().BeTrue();
        var unmountedAt = mount.OccurredAt.AddHours(1);
        (await FeedService().ExecuteAsync(CloseFeed(
            FeedSessionOperations.Unmount, suffix, mount.FeedSessionId, "delayed-trace",
            unmountedAt))).IsSuccess.Should().BeTrue();

        var consumed = await new ConsumptionService(new ConsumptionRepository(DataSource()))
            .ConsumeAsync(new MaterialConsumptionCommand(
                $"CON-LATE-{suffix}", $"consume-late:{suffix}", mount.PlantId!, mount.EquipmentId!,
                lotId, mount.MaterialId!, 1m, "kg", "Trace", unmountedAt.AddMinutes(-1),
                "FDC", $"COL-LATE-{suffix}", TraceId: $"COL-LATE-{suffix}",
                OperatorId: "operator", FeedSessionId: mount.FeedSessionId,
                CorrelationId: mount.FeedSessionId));

        consumed.IsSuccess.Should().BeTrue(consumed.IsFailure ? consumed.Error.Description : string.Empty);
        Scalar<string>(
                "SELECT ACTIVE_FEED_SESSION_ID FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id",
                ("@id", lotId))
            .Should().Be(mount.FeedSessionId);
    }

    [Fact]
    public async Task Legacy_lowercase_trace_replay_promotes_typed_feed_session_without_changing_the_hash()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);
        (await FeedService().ExecuteAsync(mount)).IsSuccess.Should().BeTrue();
        var service = new ConsumptionService(new ConsumptionRepository(DataSource()));
        var legacy = new MaterialConsumptionCommand(
                $"CON-{suffix}", $"consume:{suffix}", mount.PlantId!, mount.EquipmentId!,
                lotId, mount.MaterialId!, 1m, "kg", "trace", mount.OccurredAt.AddMinutes(10),
                "FDC", $"COL-{suffix}", TraceId: $"COL-{suffix}",
                OperatorId: "operator", CorrelationId: mount.FeedSessionId);

        var consumed = await service.ConsumeAsync(legacy);
        var replay = await service.ConsumeAsync(legacy with { FeedSessionId = mount.FeedSessionId });

        consumed.IsSuccess.Should().BeTrue(consumed.IsFailure ? consumed.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue(replay.IsFailure ? replay.Error.Description : string.Empty);
        replay.Value.Should().BeEquivalentTo(consumed.Value);
        Scalar<string>(
                "SELECT FEED_SESSION_ID FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE CONSUMPTION_ID=@id",
                ("@id", $"CON-{suffix}"))
            .Should().Be(mount.FeedSessionId);
        Scalar<string>(
                "SELECT CONSUMPTION_MODE FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE CONSUMPTION_ID=@id",
                ("@id", $"CON-{suffix}"))
            .Should().Be("trace");
    }

    [Fact]
    public async Task Sqlite_typed_feed_session_reference_rejects_orphan_children_and_parent_deletes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);
        (await FeedService().ExecuteAsync(mount)).IsSuccess.Should().BeTrue();
        var service = new ConsumptionService(new ConsumptionRepository(DataSource()));
        var sourceId = $"CON-FK-{suffix}";
        var consumed = await service.ConsumeAsync(new MaterialConsumptionCommand(
            sourceId, $"consume-fk:{suffix}", mount.PlantId!, mount.EquipmentId!,
            lotId, mount.MaterialId!, 1m, "kg", "Trace", mount.OccurredAt.AddMinutes(10),
            "FDC", $"COL-FK-{suffix}", TraceId: $"COL-FK-{suffix}",
            OperatorId: "operator", FeedSessionId: mount.FeedSessionId,
            CorrelationId: mount.FeedSessionId));
        consumed.IsSuccess.Should().BeTrue(consumed.IsFailure ? consumed.Error.Description : string.Empty);

        const string copyConsumption = """
            INSERT INTO IVT_MATERIAL_CONSUMPTION_HISTORY
              (CONSUMPTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
               MATERIAL_LOT_ID, MATERIAL_ID, PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID,
               RECIPE_ID, RECIPE_VERSION, CONSUMPTION_MODE, QUANTITY, UNIT, TRACE_ID, TAG_ID,
               SOURCE_EVENT_ID, SOURCE_SYSTEM, OPERATOR_ID, FEED_SESSION_ID, CORRELATION_ID,
               REVERSAL_OF_ID, STATUS, METADATA_JSON, OCCURRED_AT, CREATED_BY, CREATED_AT)
            SELECT @newId, @newKey, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                   @materialLotId, @materialId, PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID,
                   RECIPE_ID, RECIPE_VERSION, CONSUMPTION_MODE, QUANTITY, UNIT, TRACE_ID, TAG_ID,
                   @newSource, SOURCE_SYSTEM, OPERATOR_ID, @feedSessionId, @feedSessionId,
                   REVERSAL_OF_ID, STATUS, METADATA_JSON, OCCURRED_AT, CREATED_BY, CREATED_AT
              FROM IVT_MATERIAL_CONSUMPTION_HISTORY
             WHERE CONSUMPTION_ID=@sourceId;
            """;
        Action orphanInsert = () => ExecuteSql(
            copyConsumption,
            ("@newId", $"ORPHAN-{suffix}"), ("@newKey", $"orphan:{suffix}"),
            ("@newSource", $"ORPHAN-SOURCE-{suffix}"), ("@feedSessionId", $"MISSING-{suffix}"),
            ("@materialLotId", lotId), ("@materialId", mount.MaterialId!),
            ("@sourceId", sourceId));
        orphanInsert.Should().Throw<SqliteException>().WithMessage("*feed session does not exist*");

        var historySession = $"FS-HISTORY-{suffix}";
        var historySuffix = $"{suffix}h";
        var historyLotId = await ReceiveMaterialLot(historySuffix);
        var historyMaterialId = $"MAT-{historySuffix}";
        ExecuteSql("""
            INSERT INTO IVT_MATERIAL_FEED_SESSION
              (FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
               MATERIAL_LOT_ID, MATERIAL_ID, MOUNTED_AT, MOUNTED_BY, STATUS, VERSION_NO)
            VALUES
              (@sessionId, @plantId, @equipmentId, @feedPointId,
               @lotId, @materialId, @mountedAt, 'operator', 'Mounted', 1);
            UPDATE IVT_MATERIAL_FEED_SESSION
               SET UNMOUNTED_AT=@unmountedAt,
                   UNMOUNTED_BY='operator',
                   STATUS='Unmounted',
                   VERSION_NO=2
             WHERE FEED_SESSION_ID=@sessionId;
            """,
            ("@sessionId", historySession), ("@plantId", mount.PlantId!),
            ("@equipmentId", mount.EquipmentId!), ("@feedPointId", $"HISTORY-{suffix}"),
            ("@lotId", historyLotId), ("@materialId", historyMaterialId),
            ("@mountedAt", DbTimestamp(mount.OccurredAt.AddHours(-2))),
            ("@unmountedAt", DbTimestamp(mount.OccurredAt.AddHours(-1))));
        ExecuteSql(
            copyConsumption,
            ("@newId", $"DIRECT-{suffix}"), ("@newKey", $"direct:{suffix}"),
            ("@newSource", $"DIRECT-SOURCE-{suffix}"), ("@feedSessionId", historySession),
            ("@materialLotId", historyLotId), ("@materialId", historyMaterialId),
            ("@sourceId", sourceId));

        Action deleteReferencedParent = () => ExecuteSql(
            "DELETE FROM IVT_MATERIAL_FEED_SESSION WHERE FEED_SESSION_ID=@id;",
            ("@id", historySession));
        deleteReferencedParent.Should().Throw<SqliteException>();
    }

    [Fact]
    public async Task Concurrent_mounts_for_the_same_feed_point_allow_one_winner()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var first = MountFeed(suffix, lotId) with { FeedSessionId = $"FA-{suffix}" };
        var second = MountFeed(suffix, lotId) with
        {
            FeedSessionId = $"FB-{suffix}",
            IdempotencyKey = $"feed-mount-b:{suffix}",
            SourceEventId = $"feed-mount-source-b:{suffix}",
        };

        var results = await Task.WhenAll(
            FeedService().ExecuteAsync(first),
            FeedService().ExecuteAsync(second));

        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Count(result => result.IsFailure).Should().Be(1);
        results.Single(result => result.IsFailure).Error.Code
            .Should().Be("IVT.FeedSession.MountConflict");
    }

    [Fact]
    public async Task Concurrent_mount_and_hold_for_the_same_material_lot_allow_one_winner()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var mount = MountFeed(suffix, lotId);
        var hold = new MaterialLotCommand(
            $"lot-hold-race:{suffix}", $"lot-hold-race-key:{suffix}",
            MaterialLotOperations.Hold, lotId, 1, mount.OccurredAt,
            "TEST", $"lot-hold-race-source:{suffix}",
            Reason: "race", ActorId: "material-operator");

        var feedTask = FeedService().ExecuteAsync(mount);
        var holdTask = new MaterialLotService(new MaterialLotRepository(DataSource()))
            .ExecuteAsync(hold);
        await Task.WhenAll(feedTask, holdTask);
        var feedResult = await feedTask;
        var holdResult = await holdTask;

        new[] { feedResult.IsSuccess, holdResult.IsSuccess }
            .Count(success => success).Should().Be(1);
        var activeSession = Scalar<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_FEED_SESSION WHERE MATERIAL_LOT_ID=@id AND STATUS='Mounted' AND UNMOUNTED_AT IS NULL",
            ("@id", lotId));
        var status = Scalar<string>(
            "SELECT STATUS FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id", ("@id", lotId));
        if (feedResult.IsSuccess)
        {
            activeSession.Should().Be(1);
            status.Should().Be("InStock");
        }
        else
        {
            activeSession.Should().Be(0);
            status.Should().Be("Hold");
        }
    }

    [Fact]
    public async Task Feed_session_command_ledger_rejects_update_delete_and_replace()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = await ReceiveMaterialLot(suffix);
        var command = MountFeed(suffix, lotId);
        var result = await FeedService().ExecuteAsync(command);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);

        AssertLedgerIsAppendOnly(
            "IVT_FEED_SESSION_COMMAND",
            "FEED_SESSION_ID",
            command.FeedSessionId);
        Action invalidSnapshot = () => ExecuteLedgerMutation("""
            INSERT INTO IVT_FEED_SESSION_COMMAND
            SELECT 'BAD-' || COMMAND_ID, 'Unmount', 'BAD-' || IDEMPOTENCY_KEY, REQUEST_HASH,
                   FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
                   MATERIAL_LOT_ID, MATERIAL_ID, PROCESS_LOT_ID, WORK_ORDER_ID,
                   PROCESS_ID, RECIPE_ID, RECIPE_VERSION, MOUNTED_AT, MOUNTED_BY,
                   NULL, NULL, 'Mounted', 1, 2, ACTOR_ID, OCCURRED_AT,
                   SOURCE_SYSTEM, 'BAD-' || SOURCE_EVENT_ID, CORRELATION_ID, REASON,
                   CREATED_BY, CREATED_AT
              FROM IVT_FEED_SESSION_COMMAND
             WHERE FEED_SESSION_ID=@aggregateId LIMIT 1;
            """, command.FeedSessionId);
        invalidSnapshot.Should().Throw<SqliteException>().WithMessage("*CHECK constraint failed*");
    }

    private TraceBindingService BindingService()
    {
        _ = _factory.CreateClient();
        return new TraceBindingService(
            new TraceBindingRepository(DataSource()),
            new EmptyTraceSource(),
            TraceMaintenanceGate.Open());
    }

    private FeedSessionService FeedService()
    {
        _ = _factory.CreateClient();
        var dataSource = DataSource();
        return new FeedSessionService(
            new FeedSessionRepository(dataSource),
            new MaterialLotRepository(dataSource));
    }

    private async Task<string> ReceiveMaterialLot(string suffix)
    {
        var lotId = $"ML-{suffix}";
        var result = await new MaterialLotService(new MaterialLotRepository(DataSource()))
            .ExecuteAsync(new MaterialLotCommand(
                $"receive-{suffix}",
                $"receive:{suffix}",
                MaterialLotOperations.Receive,
                lotId,
                0,
                new DateTime(2026, 8, 28, 4, 30, 0, DateTimeKind.Utc),
                "TEST",
                $"receive-source:{suffix}",
                MaterialId: $"MAT-{suffix}",
                Quantity: 100m,
                Unit: "kg",
                Location: "STORE",
                ActorId: "material-operator"));
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        return lotId;
    }

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnectionString,
    };

    private void AssertLedgerIsAppendOnly(string table, string aggregateColumn, string aggregateId)
    {
        Action update = () => ExecuteLedgerMutation(
            $"UPDATE {table} SET ACTOR_ID='tampered' WHERE {aggregateColumn}=@aggregateId;",
            aggregateId);
        Action delete = () => ExecuteLedgerMutation(
            $"DELETE FROM {table} WHERE {aggregateColumn}=@aggregateId;",
            aggregateId);
        Action replace = () => ExecuteLedgerMutation(
            $"INSERT OR REPLACE INTO {table} SELECT * FROM {table} WHERE {aggregateColumn}=@aggregateId LIMIT 1;",
            aggregateId);

        update.Should().Throw<SqliteException>().WithMessage("*append-only*");
        delete.Should().Throw<SqliteException>().WithMessage("*append-only*");
        replace.Should().Throw<SqliteException>().WithMessage("*replacement is forbidden*");
    }

    private void ExecuteLedgerMutation(string sql, string aggregateId)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@aggregateId", aggregateId);
        command.ExecuteNonQuery();
    }

    private void ExecuteSql(string sql, params (string Name, object Value)[] parameters)
        => ExecuteSql(_factory.ConnectionString, sql, parameters);

    private T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static void ExecuteSql(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static string DbTimestamp(DateTime value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static DateTime NextUtcAfter(DateTime previous)
    {
        var current = DateTime.UtcNow;
        while (current <= previous) current = DateTime.UtcNow;
        return current;
    }

    private static TraceBindingCommand CreateBinding(string suffix)
    {
        var effective = new DateTime(2026, 8, 28, 4, 0, 0, DateTimeKind.Utc);
        return new TraceBindingCommand(
            TraceBindingOperations.Create,
            $"B-{suffix}",
            0,
            $"binding-create:{suffix}",
            "TEST",
            $"binding-source:{suffix}",
            effective,
            effective,
            PlantId: "PLANT-01",
            EquipmentId: $"EQ-{suffix}",
            ParameterId: "FLOW-01",
            FeedPointId: "FEED-01",
            CalculationMode: "CounterDelta",
            ScaleFactor: 1m,
            OutputUnit: "kg",
            ActorId: "operator");
    }

    private static FeedSessionCommand MountFeed(string suffix, string materialLotId) => new(
        FeedSessionOperations.Mount,
        $"FS-{suffix}",
        0,
        $"feed-mount:{suffix}",
        "TEST",
        $"feed-mount-source:{suffix}",
        new DateTime(2026, 8, 28, 5, 0, 0, DateTimeKind.Utc),
        PlantId: "PLANT-01",
        EquipmentId: $"EQ-{suffix}",
        FeedPointId: "FEED-01",
        MaterialLotId: materialLotId,
        MaterialId: $"MAT-{suffix}",
        ProcessLotId: $"PLOT-{suffix}",
        WorkOrderId: $"WO-{suffix}",
        ProcessId: "WASH",
        RecipeId: "RECIPE-01",
        RecipeVersion: 1,
        ActorId: "operator");

    private static FeedSessionCommand CloseFeed(
        string operation,
        string suffix,
        string feedSessionId,
        string eventName,
        DateTime? occurredAt = null) => new(
        operation,
        feedSessionId,
        1,
        $"feed-{eventName}:{suffix}",
        "TEST",
        $"feed-{eventName}-source:{suffix}",
        occurredAt ?? new DateTime(2026, 8, 28, 6, 0, 0, DateTimeKind.Utc),
        ActorId: "operator",
        Reason: eventName);

    private sealed class EmptyTraceSource : IFdcTraceSource
    {
        public Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
            IReadOnlyCollection<FdcTraceReadScope> scopes,
            int maxCount,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FdcTraceSample>>([]);
    }
}
