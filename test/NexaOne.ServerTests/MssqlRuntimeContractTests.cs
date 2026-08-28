using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.EMS.Infrastructure;
using NexaOne.EST.Infrastructure;
using NexaOne.FDC.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Domain;
using NexaOne.IVT.Infrastructure;
using NexaOne.MDM.Infrastructure;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.Mrp;
using NexaOne.POM.Domain.Mrp;
using NexaOne.POM.Infrastructure;
using NexaOne.QMS.Infrastructure;
using NexaOne.RMS.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Prc;
using NexaOne.ServiceContracts.Qms;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>
/// Runs representative module runtime paths against the fully migrated SQL Server schema. These
/// tests deliberately use module services/repositories and the shipped MSSQL named-query files;
/// they are not substitutes for the parser-only dialect suite.
/// </summary>
[Trait("Category", "MssqlContract")]
public sealed class MssqlRuntimeContractTests
{
    private readonly ITestOutputHelper _output;

    public MssqlRuntimeContractTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Ivt_trace_inbox_round_trip_is_idempotent_cursor_backed_and_queryable()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Suffix();
        var bindingId = $"TB_{suffix}";
        var firstCollectId = $"TC1_{suffix}";
        var secondCollectId = $"TC2_{suffix}";
        var collectedAt = new DateTime(2041, 3, 5, 1, 2, 3, DateTimeKind.Utc);
        await database.ExecuteAsync(
            """
            INSERT INTO IVT_TRACE_CONSUMPTION_BINDING
                (BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
                 CALCULATION_MODE, SCALE_FACTOR, PULSE_QUANTITY, OUTPUT_UNIT,
                 EFFECTIVE_FROM, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@bindingId, @plantId, @equipmentId, @parameterId, @feedPointId,
                 'CounterDelta', 1, NULL, 'kg', @effectiveFrom, 1,
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME());
            """,
            new
            {
                bindingId,
                plantId = $"TP_{suffix}",
                equipmentId = $"TE_{suffix}",
                parameterId = $"PARAM_{suffix}",
                feedPointId = $"FEED_{suffix}",
                effectiveFrom = collectedAt.AddMinutes(-1),
            });

        var dialect = (INexaOneEESDbCapability)database.DataSource.Provider;
        var repository = new TraceProjectionRepository(database.DataSource, dialect);
        var first = new TraceProjectionItem(
            bindingId, firstCollectId, $"TP_{suffix}", $"TE_{suffix}", $"PARAM_{suffix}",
            $"FEED_{suffix}", "CounterDelta", 1m, null, "kg", 10m, "Good", collectedAt);
        var second = first with { CollectId = secondCollectId, RawValue = 13.5m };

        (await repository.AddToInboxAsync([first, first])).Should().Be(1);
        (await repository.AddToInboxAsync([first])).Should().Be(0);
        (await repository.AddToInboxAsync([second])).Should().Be(1);

        var bindings = await repository.GetSourceBindingsAsync();
        bindings.Should().ContainSingle(binding =>
            binding.BindingId == bindingId
            && binding.LastEnqueuedCollectId == secondCollectId
            && binding.LastEnqueuedAt == collectedAt);

        var claimed = (await repository.GetPendingAsync(5000))
            .Where(item => item.BindingId == bindingId)
            .OrderBy(item => item.CollectId, StringComparer.Ordinal)
            .ToArray();
        claimed.Should().HaveCount(2);
        claimed.Select(item => item.LeaseOwnerId).Should().OnlyContain(owner => !string.IsNullOrWhiteSpace(owner));
        claimed.Select(item => item.LeaseOwnerId).Distinct().Should().ContainSingle();

        await repository.CompleteAsync(
            claimed[0],
            new TraceProjectionState(bindingId, firstCollectId, 10m, collectedAt),
            "Ignored",
            null,
            "counter baseline");
        await repository.CompleteAsync(
            claimed[1],
            new TraceProjectionState(bindingId, secondCollectId, 13.5m, collectedAt),
            "Applied",
            null,
            null);
        await repository.ReleaseLeaseAsync(bindingId, claimed[0].LeaseOwnerId!);

        var restored = await repository.GetStateAsync(bindingId);
        restored.Should().Be(new TraceProjectionState(bindingId, secondCollectId, 13.5m, collectedAt));
        (await repository.AddToInboxAsync([second])).Should().Be(0);
        (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@bindingId AND IS_WORK_ITEM=1;",
                new { bindingId }))
            .Should().Be(0);

        Func<Task> hideTerminalStateInReadyQueue = () => database.ExecuteAsync(
            """
            UPDATE IVT_TRACE_PROJECTION_INBOX
               SET IS_WORK_ITEM=1
             WHERE BINDING_ID=@bindingId AND COLLECT_ID=@collectId;
            """,
            new { bindingId, collectId = secondCollectId });
        await hideTerminalStateInReadyQueue.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>(
            "the SQL Server CHECK must keep STATUS and the filtered work-set flag equivalent");
    }

    [Fact]
    public async Task Fdc_effect_lifecycle_rejects_inconsistent_terminal_state_and_version()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Suffix();
        var plantId = $"FP_{suffix}";
        var equipmentId = $"FE_{suffix}";
        var ruleId = $"FR_{suffix}";
        var effectId = $"FX_{suffix}";
        await SeedEquipmentAsync(database, equipmentId, plantId);
        await database.ExecuteAsync(
            """
            INSERT INTO FDC_INTERLOCK_RULE
                (RULE_ID, RULE_NAME, EQUIPMENT_ID, PARAMETER_ID, OPERATOR,
                 THRESHOLD_VALUE, ACTION, PRIORITY, IS_ACTIVE,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@ruleId, 'contract rule', @equipmentId, 'TEMP', 'GT',
                 80, 'STOP', 1, 1,
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME());

            INSERT INTO FDC_INTERLOCK_HISTORY
                (HISTORY_ID, RULE_ID, EQUIPMENT_ID, PARAMETER_ID, TRIGGER_VALUE,
                 ACTION, MESSAGE, TRIGGERED_AT, IS_RESOLVED, EFFECT_STATE, VERSION,
                 APPLY_ACK_ID, APPLY_CONFIRMED_AT,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@effectId, @ruleId, @equipmentId, 'TEMP', 90,
                 'STOP', 'contract effect', SYSUTCDATETIME(), 0, 'Applied', 1,
                 'apply-contract', SYSUTCDATETIME(),
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME());
            """,
            new { ruleId, equipmentId, effectId });

        Func<Task> inconsistentTerminal = () => database.ExecuteAsync(
            "UPDATE FDC_INTERLOCK_HISTORY SET IS_RESOLVED=1 WHERE HISTORY_ID=@effectId;",
            new { effectId });
        await inconsistentTerminal.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>(
            "IS_RESOLVED and EFFECT_STATE must cross the terminal boundary together");

        Func<Task> invalidVersion = () => database.ExecuteAsync(
            "UPDATE FDC_INTERLOCK_HISTORY SET VERSION=0 WHERE HISTORY_ID=@effectId;",
            new { effectId });
        await invalidVersion.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();

        Func<Task> unchangedVersion = () => database.ExecuteAsync(
            "UPDATE FDC_INTERLOCK_HISTORY SET LAST_ERROR='retry' WHERE HISTORY_ID=@effectId;",
            new { effectId });
        await unchangedVersion.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>(
            "every durable direct-writer mutation must advance the optimistic version");

        Func<Task> skipNormalization = () => database.ExecuteAsync(
            """
            UPDATE FDC_INTERLOCK_HISTORY
               SET IS_RESOLVED=1, EFFECT_STATE='Resolved', VERSION=2,
                   CONDITION_NORMALIZED_AT=DATEADD(millisecond, 1, APPLY_CONFIRMED_AT),
                   CONDITION_NORMALIZED_VALUE=50,
                   RELEASE_ACK_ID='release-contract',
                   RELEASE_CONFIRMED_AT=DATEADD(millisecond, 2, APPLY_CONFIRMED_AT),
                   RESOLVED_AT=DATEADD(millisecond, 2, APPLY_CONFIRMED_AT)
             WHERE HISTORY_ID=@effectId;
            """,
            new { effectId });
        await skipNormalization.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>(
            "an Applied effect cannot jump directly across the normalized/release lifecycle");

        await database.ExecuteAsync(
            """
            UPDATE FDC_INTERLOCK_HISTORY
               SET EFFECT_STATE='ConditionNormalized', VERSION=2,
                   CONDITION_NORMALIZED_AT=DATEADD(millisecond, 1, APPLY_CONFIRMED_AT),
                   CONDITION_NORMALIZED_VALUE=50
             WHERE HISTORY_ID=@effectId;

            UPDATE FDC_INTERLOCK_HISTORY
               SET EFFECT_STATE='Applied', VERSION=3,
                   CONDITION_NORMALIZED_AT=NULL, CONDITION_NORMALIZED_VALUE=NULL
             WHERE HISTORY_ID=@effectId;

            UPDATE FDC_INTERLOCK_HISTORY
               SET EFFECT_STATE='ConditionNormalized', VERSION=4,
                   CONDITION_NORMALIZED_AT=DATEADD(millisecond, 1, APPLY_CONFIRMED_AT),
                   CONDITION_NORMALIZED_VALUE=50
             WHERE HISTORY_ID=@effectId;

            UPDATE FDC_INTERLOCK_HISTORY
               SET IS_RESOLVED=1, EFFECT_STATE='Resolved', VERSION=6,
                   RELEASE_ACK_ID='release-contract',
                   RELEASE_CONFIRMED_AT=DATEADD(millisecond, 2, APPLY_CONFIRMED_AT),
                   RESOLVED_AT=DATEADD(millisecond, 2, APPLY_CONFIRMED_AT)
             WHERE HISTORY_ID=@effectId;
            """,
            new { effectId });

        Func<Task> mutateTerminal = () => database.ExecuteAsync(
            "UPDATE FDC_INTERLOCK_HISTORY SET VERSION=7 WHERE HISTORY_ID=@effectId;",
            new { effectId });
        await mutateTerminal.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>(
            "resolved physical-effect evidence is terminal");

        Func<Task> deleteEvidence = () => database.ExecuteAsync(
            "DELETE FROM FDC_INTERLOCK_HISTORY WHERE HISTORY_ID=@effectId;",
            new { effectId });
        await deleteEvidence.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>(
            "physical-effect history is append-only");
    }

    [Fact]
    public async Task Fdc_runtime_lease_uses_monotonic_fence_and_owner_CAS()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var lease = new FdcRuntimeLease(database.DataSource);
        var before = await lease.GetStateAsync();
        before.HasOwnerTuple.Should().BeFalse(
            "the isolated MSSQL contract database must not have a live FDC runtime writer");

        var firstOwner = $"contract-a-{Suffix()}";
        var firstRevision = Revision("contract-a");
        var first = await lease.TryAcquireAsync(
            firstOwner, firstRevision, TimeSpan.FromSeconds(30));
        first.Acquired.Should().BeTrue();
        first.Grant.Should().NotBeNull();
        first.State.FenceToken.Should().BeGreaterThan(before.FenceToken);

        var competing = await lease.TryAcquireAsync(
            $"contract-rival-{Suffix()}", Revision("rival"), TimeSpan.FromSeconds(30));
        competing.Acquired.Should().BeFalse();
        competing.Grant.Should().BeNull();
        competing.State.FenceToken.Should().Be(first.State.FenceToken);

        Func<Task> decrementFence = () => database.ExecuteAsync(
            "UPDATE FDC_RUNTIME_OWNERSHIP SET FENCE_TOKEN=FENCE_TOKEN-1 WHERE LEASE_SCOPE='GLOBAL';");
        await decrementFence.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();

        Func<Task> deleteFence = () => database.ExecuteAsync(
            "DELETE FROM FDC_RUNTIME_OWNERSHIP WHERE LEASE_SCOPE='GLOBAL';");
        await deleteFence.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();

        var renewed = await lease.TryRenewAsync(first.Grant!, TimeSpan.FromSeconds(30));
        renewed.Should().NotBeNull();
        (await lease.TryReleaseAsync(renewed!)).Should().BeTrue();

        var secondOwner = $"contract-b-{Suffix()}";
        var second = await lease.TryAcquireAsync(
            secondOwner, Revision("contract-b"), TimeSpan.FromSeconds(30));
        second.Acquired.Should().BeTrue();
        second.State.FenceToken.Should().Be(first.State.FenceToken + 1);
        (await lease.TryReleaseAsync(first.Grant!)).Should().BeFalse();
        (await lease.TryReleaseAsync(second.Grant!)).Should().BeTrue();
    }

    [Fact]
    public async Task Pom_lot_track_in_round_trip_is_versioned_idempotent_and_visible_to_mssql_queries()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Suffix();
        var plantId = $"LP_{suffix}";
        var lotId = $"LOT_{suffix}";
        var equipmentId = $"LE_{suffix}";
        await SeedEquipmentAsync(database, equipmentId, plantId);
        await database.ExecuteAsync(
            """
            INSERT INTO POM_LOT
                (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY,
                 LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD,
                 VERSION_NO, CREATED_BY, CREATED_AT)
            VALUES
                (@lotId, @plantId, NULL, @productId, 25, 0,
                 'Queued', 'Idle', @processId, 0, 'N', 1,
                 'mssql-contract', SYSUTCDATETIME());
            """,
            new
            {
                lotId,
                plantId,
                productId = $"ITEM_{suffix}",
                processId = $"PROC_{suffix}",
            });

        var dialect = (INexaOneEESDbCapability)database.DataSource.Provider;
        var lots = new LotRepository(database.DataSource, EmptyConfiguration());
        var service = new LotTrackingService(
            lots,
            lots,
            new LotHistoryRepository(database.DataSource, dialect),
            new LotMixingRelationRepository(database.DataSource),
            new PomWorkOrderRepository(database.DataSource),
            new TrackingMasterGateway(
                new EquipmentDirectory(database.DataSource),
                new TrackingRoutingDirectory(database.DataSource),
                new TrackingRecipeDirectory(database.DataSource),
                new TrackingDefectDirectory(database.DataSource)),
            new NotRequiredQualityGateway());
        var command = new TrackInCommand(
            plantId,
            lotId,
            equipmentId,
            null,
            null,
            "operator",
            ExpectedVersion: 1,
            IdempotencyKey: $"TRACK-IN:{suffix}",
            ClientChannel: "POP",
            DeviceId: "MSSQL-PANEL");

        var first = await service.TrackInAsync(command);
        var replay = await service.TrackInAsync(command);
        var route = await service.GetRouteAsync(lotId);

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue("an exact MSSQL retry must resolve through POM_LOT_EXECUTION");
        route.IsSuccess.Should().BeTrue();
        route.Value.Lot.VersionNo.Should().Be(2);
        route.Value.Lot.State.Should().Be(NexaOne.POM.Domain.LotState.Processing);
        route.Value.Histories.Should().ContainSingle(history =>
            history.ExecutionId == "TrackIn" && history.IdempotencyKey == command.IdempotencyKey);

        var rows = await database.QueryNamedAsync(
            "POM.LotTraceList",
            new { plantId, lotId });
        rows.Should().ContainSingle(row =>
            string.Equals(Value(row, "LOT_ID"), lotId, StringComparison.Ordinal)
            && string.Equals(Value(row, "IDEMPOTENCY_KEY"), command.IdempotencyKey, StringComparison.Ordinal));
        (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM POM_LOT_EXECUTION WHERE IDEMPOTENCY_KEY=@key;",
                new { key = command.IdempotencyKey }))
            .Should().Be(1);
    }

    [Fact]
    public async Task Ems_work_order_commands_round_trip_once_and_are_visible_to_mssql_queries()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Suffix();
        var equipmentId = $"ME_{suffix}";
        var workOrderId = $"MW_{suffix}";
        await SeedEquipmentAsync(database, equipmentId, $"MP_{suffix}");

        var service = new EmsService(
            new WorkOrderRepository(database.DataSource, EmptyConfiguration()),
            new MaintenancePlanRepository(database.DataSource, EmptyConfiguration()));
        var create = MaintenanceCommandContext.Create(
            "admin", $"EMS-CREATE:{suffix}", "MES", "MSSQL-PANEL", $"corr-create-{suffix}").Value;
        var start = MaintenanceCommandContext.Create(
            "admin", $"EMS-START:{suffix}", "MES", "MSSQL-PANEL", $"corr-start-{suffix}").Value;

        var created = await service.CreateWorkOrderAsync(
            workOrderId, equipmentId, "BM", "MSSQL contract maintenance", "admin", null, create);
        var createReplay = await service.CreateWorkOrderAsync(
            workOrderId, equipmentId, "BM", "MSSQL contract maintenance", "admin", null, create);
        var started = await service.StartWorkOrderAsync(workOrderId, start);
        var startReplay = await service.StartWorkOrderAsync(workOrderId, start);
        var byEquipment = await service.GetByEquipmentAsync(equipmentId);

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        createReplay.IsSuccess.Should().BeTrue("creation retries must use the immutable command ledger");
        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error.Description : string.Empty);
        startReplay.IsSuccess.Should().BeTrue("transition retries must use the action ledger");
        byEquipment.Value.Should().ContainSingle(workOrder =>
            workOrder.Id == workOrderId && workOrder.Status == WorkOrderStatus.InProgress);

        var rows = await database.QueryNamedAsync(
            "EMS.WorkOrderList",
            new { equipmentId, status = "InProgress" });
        rows.Should().ContainSingle(row => string.Equals(
            Value(row, "WO_ID"), workOrderId, StringComparison.Ordinal));
        (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM EMS_MAINTENANCE_ACTION_HISTORY WHERE WO_ID=@workOrderId;",
                new { workOrderId }))
            .Should().Be(2, "Create and Start each append one action despite exact retries");
        (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM EMS_WORK_ORDER_CREATE_COMMAND WHERE IDEMPOTENCY_KEY=@key;",
                new { key = create.IdempotencyKey }))
            .Should().Be(1);
    }

    [Fact]
    public async Task Est_oee_window_rebuild_replaces_summary_and_is_visible_to_mssql_queries()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Suffix();
        var plantId = $"OP_{suffix}";
        var equipmentId = $"OE_{suffix}";
        var runState = $"RUN_{suffix}";
        var downState = $"DOWN_{suffix}";
        var productId = $"OI_{suffix}";
        var lotId = $"OL_{suffix}";
        var start = new DateTime(2097, 4, 12, 0, 0, 0, DateTimeKind.Utc);
        await SeedEquipmentAsync(database, equipmentId, plantId);
        await database.ExecuteAsync(
            """
            INSERT INTO MDM_PRODUCT
                (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@productId, @productId, 'Contract', 'EA', 'Valid',
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME());

            INSERT INTO POM_LOT
                (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
                 ROUTE_STEPS, CURRENT_STEP, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT)
            VALUES
                (@lotId, @plantId, @productId, 100, 5, 'Completed', 'Idle',
                 @processId, 0, 'N', 1, 'mssql-contract', SYSUTCDATETIME());

            INSERT INTO POM_LOT_HISTORY
                (PLANT_ID, LOT_ID, EQUIPMENT_ID, PROCESS_ID, TRACK_IN_TIME, TRACK_OUT_TIME,
                 EXECUTION_ID, EXECUTION_USER, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
                 CREATED_AT)
            VALUES
                (@plantId, @lotId, @equipmentId, @processId, @trackInAt, @trackOutAt,
                 'TrackOut', 'mssql-contract', 100, 5, 'Completed', 'Idle', SYSUTCDATETIME());

            INSERT INTO EST_STATE_CATEGORY
                (STATE_ID, STATE_NAME, CATEGORY, IS_PRODUCTIVE, IS_DOWNTIME, IS_SCHEDULED,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@runState, @runState, 'Productive', 1, 0, 1,
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME()),
                (@downState, @downState, 'Breakdown', 0, 1, 1,
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME());

            INSERT INTO EST_OEE_TARGET
                (EQUIPMENT_ID, IDEAL_CYCLE_TIME_SEC, PLANNED_MINUTES, DESCRIPTION,
                 IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@equipmentId, 30, 480, 'MSSQL contract target', 1,
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME());

            INSERT INTO EST_EQUIPMENT_STATE_HISTORY
                (HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE,
                 CHANGED_AT, CHANGED_BY, SOURCE_TYPE)
            VALUES
                (@h1, @equipmentId, 'IDLE', @runState, @runState, @start,
                 'mssql-contract', 'TEST'),
                (@h2, @equipmentId, @runState, @downState, @downState, @downAt,
                 'mssql-contract', 'TEST'),
                (@h3, @equipmentId, @downState, @runState, @runState, @resumeAt,
                 'mssql-contract', 'TEST');
            """,
            new
            {
                equipmentId,
                plantId,
                productId,
                lotId,
                processId = $"PROC_{suffix}",
                trackInAt = start.AddHours(6.5),
                trackOutAt = start.AddHours(7),
                runState,
                downState,
                h1 = $"OH1_{suffix}",
                h2 = $"OH2_{suffix}",
                h3 = $"OH3_{suffix}",
                start,
                downAt = start.AddHours(4),
                resumeAt = start.AddHours(5),
            });

        var evidence = new OeeEvidenceSource(
            new OeePlanDirectory(database.DataSource),
            new OeeProductionDirectory(database.DataSource));
        var repository = new OeeAggregationRepository(database.DataSource, evidence);

        (await repository.AggregateWindowAsync(start, start.AddHours(8))).Should().Be(1);
        await database.ExecuteAsync(
            """
            UPDATE POM_LOT_HISTORY
               SET QTY=120, DEFECT_QTY=6
             WHERE PLANT_ID=@plantId AND LOT_ID=@lotId AND EXECUTION_ID='TrackOut';
            """,
            new { plantId, lotId });
        (await repository.AggregateWindowAsync(start, start.AddHours(8))).Should().Be(1);

        var rows = await database.QueryNamedAsync(
            "EST.OeeSummaryList",
            new { plantId, equipmentId });
        var row = rows.Should().ContainSingle(item => string.Equals(
            Value(item, "EQUIPMENT_ID"), equipmentId, StringComparison.Ordinal)).Subject;
        Decimal(row, "TOTAL_COUNT").Should().Be(120m);
        Decimal(row, "DEFECT_COUNT").Should().Be(6m);
        Decimal(row, "OPERATING_MINUTES").Should().Be(420m);
        Decimal(row, "DOWNTIME_MINUTES").Should().Be(60m);
        (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM EST_OEE_SUMMARY WHERE EQUIPMENT_ID=@equipmentId AND OEE_DATE=@date;",
                new { equipmentId, date = start.Date }))
            .Should().Be(1, "a deterministic rebuild replaces rather than duplicates the mart row");
    }

    [Fact]
    public async Task Pom_mrp_run_and_conversion_persist_queryable_pegging_and_converge_on_retry()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Suffix();
        var itemId = $"MI_{suffix}";
        var sourceRef = $"DEMAND_{suffix}";
        var purchaseOrders = new RecordingPurchaseOrderBridge();
        var repository = new MrpPlanningRepository(
            database.DataSource,
            new StubDemandSource(new MrpDemand(
                itemId, 12m, new DateTime(2042, 6, 30), sourceRef, $"PLANT_{suffix}")),
            new StubMrpMasterDirectory(new MrpMasterSnapshot(
                [],
                [new MrpItemPlanningEntry(itemId, 0m, 2, 1m, "Buy")],
                [new MrpVendorPlanningEntry(itemId, 2, 1m)])),
            new EmptyMrpInventoryDirectory(),
            purchaseOrders,
            new EmptyEquipmentDirectory());

        var run = await repository.RunAsync("mssql-contract");
        run.Status.Should().Be("Success", run.Message);
        run.PlannedOrderCount.Should().Be(1);

        var planned = await database.QueryNamedAsync(
            "POM.MrpPlannedOrderList",
            new { runId = run.RunId });
        var plannedRow = planned.Should().ContainSingle(row =>
            string.Equals(Value(row, "ITEM_ID"), itemId, StringComparison.Ordinal)
            && string.Equals(Value(row, "STATUS"), "Proposed", StringComparison.Ordinal)).Subject;
        var plannedOrderId = Value(plannedRow, "PLANNED_ORDER_ID");
        var pegging = await database.QueryNamedAsync(
            "POM.MrpPeggingList",
            new { runId = run.RunId });
        pegging.Should().ContainSingle(row =>
            string.Equals(Value(row, "PLANNED_ORDER_ID"), plannedOrderId, StringComparison.Ordinal)
            && string.Equals(Value(row, "DEMAND_REF"), sourceRef, StringComparison.Ordinal)
            && Decimal(row, "QTY") == 12m);

        var firstConversion = await repository.ConvertAsync(
            run.RunId, [plannedOrderId], null, "mssql-contract");
        var retry = await repository.ConvertAsync(
            run.RunId, [plannedOrderId], null, "mssql-contract");

        firstConversion.Message.Should().BeNull();
        firstConversion.Converted.Should().Be(1);
        firstConversion.PurchaseOrders.Should().Be(1);
        retry.Converted.Should().Be(0);
        retry.Message.Should().Contain("Proposed");
        purchaseOrders.Requests.Should().ContainSingle(request =>
            request.ProductId == itemId && request.Quantity == 12m);
        (await database.ScalarAsync<string>(
                "SELECT STATUS FROM MRP_PLANNED_ORDER WHERE PLANNED_ORDER_ID=@plannedOrderId;",
                new { plannedOrderId }))
            .Should().Be("Converted");
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static async Task SeedEquipmentAsync(
        MssqlContractDatabase database,
        string equipmentId,
        string plantId)
    {
        var areaId = $"AREA_{plantId}";
        if (areaId.Length > 50) areaId = areaId[..50];
        var equipmentClassId = $"CLASS_{plantId}";
        if (equipmentClassId.Length > 50) equipmentClassId = equipmentClassId[..50];
        await database.ExecuteAsync(
            """
            INSERT INTO MDM_EQUIPMENT
                (EQUIPMENT_ID, EQUIPMENT_NAME, DESCRIPTION, PLANT_ID, AREA_ID,
                 EQUIPMENT_TYPE, EQUIPMENT_CLASS_ID, VALID_STATE,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@equipmentId, @equipmentId, 'MSSQL contract equipment', @plantId, @areaId,
                 'Contract', @equipmentClassId, 'Active',
                 'mssql-contract', SYSUTCDATETIME(), 'mssql-contract', SYSUTCDATETIME());
            """,
            new
            {
                equipmentId,
                plantId,
                areaId,
                equipmentClassId,
            });
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private static string Revision(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Value(IDictionary<string, object> row, string key) =>
        Convert.ToString(row[key], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal Decimal(IDictionary<string, object> row, string key) =>
        Convert.ToDecimal(row[key], System.Globalization.CultureInfo.InvariantCulture);

    private sealed class NotRequiredQualityGateway : IProductionQualityGateway
    {
        public Task<ProductionQualityGateResult> EvaluateAsync(
            string lotId,
            string processId,
            string? workOrderId,
            CancellationToken ct = default)
            => Task.FromResult(ProductionQualityGateResult.NotRequired());
    }

    private sealed class StubDemandSource(MrpDemand demand) : IMrpDemandSource
    {
        public Task<IReadOnlyList<MrpDemand>> GetOpenDemandsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MrpDemand>>([demand]);
    }

    private sealed class StubMrpMasterDirectory(MrpMasterSnapshot snapshot) : IMrpMasterDirectory
    {
        public Task<MrpMasterSnapshot> GetSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(snapshot);
    }

    private sealed class EmptyMrpInventoryDirectory : IMrpInventoryDirectory
    {
        public Task<IReadOnlyList<MrpInventoryBalance>> GetBalancesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MrpInventoryBalance>>([]);
    }

    private sealed class RecordingPurchaseOrderBridge : IPurchaseOrderPlanningBridge
    {
        public List<MrpPurchaseOrderRequest> Requests { get; } = [];

        public Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MrpPurchaseReceipt>>([]);

        public Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
            MrpPurchaseOrderRequest request,
            CancellationToken ct = default)
        {
            var created = Requests.All(existing => existing.PurchaseOrderId != request.PurchaseOrderId);
            if (created)
                Requests.Add(request);
            return Task.FromResult(new PurchaseOrderEnsureResult(request.PurchaseOrderId, created));
        }
    }

    private sealed class EmptyEquipmentDirectory : IEquipmentDirectory
    {
        public Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(
            string plantId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<EquipmentDirectoryEntry?> GetEquipmentAsync(
            string equipmentId,
            CancellationToken ct = default)
            => Task.FromResult<EquipmentDirectoryEntry?>(null);

        public Task<bool> EquipmentClassExistsAsync(
            string equipmentClassId,
            CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
