using NexaDB.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Domain;
using NexaOne.POM.Infrastructure;
using NexaOne.ServiceContracts.Pom;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class WorkScopeProjectionPersistenceTests
{
    [Fact]
    public async Task Exact_event_replay_returns_the_original_receipt_and_keeps_one_durable_inbox_row()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();
        var command = Command();

        var accepted = await bridge.IngestAsync("cleaner-a", command);
        var replayed = await bridge.IngestAsync("cleaner-a", command);
        var normalizedReplay = await bridge.IngestAsync(
            "cleaner-a",
            command with
            {
                RecipeSnapshotHash = command.RecipeSnapshotHash.ToUpperInvariant(),
                ProgramHash = command.ProgramHash.ToUpperInvariant(),
                Carriers = command.Carriers.Reverse().ToArray(),
            });

        accepted.IsSuccess.Should().BeTrue();
        accepted.Value.Replay.Should().BeFalse();
        replayed.IsSuccess.Should().BeTrue();
        replayed.Value.Should().Be(accepted.Value with { Replay = true });
        normalizedReplay.IsSuccess.Should().BeTrue();
        normalizedReplay.Value.Should().Be(accepted.Value with { Replay = true });
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CARRIER"))
            .Should().Be(2L);
        (await database.TextAsync("""
            SELECT GROUP_CONCAT(LANE || ':' || CARRIER_ID || ':' || CLEANING_RUN_ID, ',')
              FROM (SELECT LANE, CARRIER_ID, CLEANING_RUN_ID
                      FROM POM_WORK_SCOPE_PROJECTION_CARRIER
                     ORDER BY LANE)
            """)).Should().Be("front:CARRIER-F:RUN-F,rear:CARRIER-R:RUN-R");
        (await database.TextAsync(
            "SELECT STATUS || ':' || VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID = 'WS-1'"))
            .Should().Be("Created:1");
    }

    [Fact]
    public async Task Carrier_constraint_failure_rolls_back_the_entire_new_event_acceptance()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        await database.ExecuteAsync("""
            CREATE TRIGGER TEST_REJECT_REAR_PROJECTION_CARRIER
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_CARRIER
            WHEN NEW.CARRIER_ID = 'CARRIER-R'
            BEGIN
              SELECT RAISE(ABORT, 'test projection carrier constraint');
            END;
            """);

        var ingest = async () => await database.CreateBridge()
            .IngestAsync("cleaner-a", Command("carrier-rollback"));

        await ingest.Should().ThrowAsync<SqliteException>()
            .WithMessage("*carrier constraint*");
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(0L);
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CARRIER"))
            .Should().Be(0L);
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CURRENT"))
            .Should().Be(0L);
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION"))
            .Should().Be(0L);
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT"))
            .Should().Be(0L);
        (await database.TextAsync(
            "SELECT STATUS || ':' || VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID = 'WS-1'"))
            .Should().Be("Created:1");
    }

    [Fact]
    public async Task Same_event_with_a_different_hash_is_a_conflict_and_preserves_the_original_evidence()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();

        var accepted = await bridge.IngestAsync("cleaner-a", Command());
        var conflict = await bridge.IngestAsync(
            "cleaner-a",
            Command() with { ResultCode = "PAIR_CHANGED" });

        accepted.IsSuccess.Should().BeTrue();
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Type.Should().Be(NexaOne.Common.ErrorType.Conflict);
        conflict.Error.Code.Should().Be("Projection.EventHashConflict");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
    }

    [Fact]
    public async Task Event_hash_conflict_precedes_the_scope_lookup_even_when_retry_points_to_an_unknown_scope()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();

        (await bridge.IngestAsync("cleaner-a", Command())).IsSuccess.Should().BeTrue();
        var conflict = await bridge.IngestAsync(
            "cleaner-a",
            Command() with { WorkScopeId = "WS-MISSING", ResultCode = "DIFFERENT" });

        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("Projection.EventHashConflict");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
    }

    [Fact]
    public async Task Event_identity_is_binary_and_case_variants_are_independent_evidence()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();

        var lower = await bridge.IngestAsync("cleaner-a", Command("event-case", 7));
        var upper = await bridge.IngestAsync("cleaner-a", Command("EVENT-CASE", 8));

        lower.IsSuccess.Should().BeTrue();
        upper.IsSuccess.Should().BeTrue();
        upper.Value.Replay.Should().BeFalse();
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(2L);
    }

    [Fact]
    public async Task Unassigned_work_scope_cannot_be_claimed_by_an_authenticated_equipment_client()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var result = await database.CreateBridge().IngestAsync(
            "cleaner-a",
            Command() with { WorkScopeId = "WS-UNASSIGNED" });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Projection.ScopeEquipmentConflict");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task Nonterminal_projection_cannot_claim_terminal_cleanup_completion()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var result = await database.CreateBridge().IngestAsync(
            "cleaner-a",
            Command() with { TerminalCleanupCompleted = true });

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task Cleaner_pair_requires_two_distinct_cleaning_run_identities()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var command = Command() with
        {
            Carriers =
            [
                new WorkScopeProjectionCarrierDto("front", "CARRIER-F", "RUN-SHARED"),
                new WorkScopeProjectionCarrierDto("rear", "CARRIER-R", "RUN-SHARED"),
            ],
        };

        var result = await database.CreateBridge().IngestAsync("cleaner-a", command);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        result.Error.Description.Should().Contain("cleaning-run identities");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(0L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CARRIER"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task Older_revision_is_durable_but_cannot_move_the_current_projection_backwards()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();

        var newer = await bridge.IngestAsync("cleaner-a", Command("event-8", revision: 8));
        var older = await bridge.IngestAsync("cleaner-a", Command("event-7", revision: 7));

        newer.IsSuccess.Should().BeTrue();
        newer.Value.IsCurrent.Should().BeTrue();
        older.IsSuccess.Should().BeTrue();
        older.Value.IsCurrent.Should().BeFalse();
        older.Value.CurrentRevision.Should().Be(8);
        var olderReplay = await bridge.IngestAsync("cleaner-a", Command("event-7", revision: 7));
        olderReplay.IsSuccess.Should().BeTrue();
        olderReplay.Value.Replay.Should().BeTrue();
        olderReplay.Value.IsCurrent.Should().BeFalse();
        olderReplay.Value.CurrentRevision.Should().Be(8);
        (await database.ScalarAsync(
            "SELECT SOURCE_REVISION FROM POM_WORK_SCOPE_PROJECTION_CURRENT"))
            .Should().Be(8L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(2L);
    }

    [Fact]
    public async Task Multiple_events_at_one_recovery_revision_advance_by_acceptance_then_cleanup_ack_advances_revision()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();
        var occurredAt = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

        var completedBeforeCleanup = await bridge.IngestAsync(
            "cleaner-a",
            Command("completed-7", 7, WorkScopeProjectionStatus.Completed, occurredAt)
                with { TerminalCleanupCompleted = false });
        (await database.TextAsync(
            "SELECT PROJECTION_STATUS || ':' || TERMINAL_CLEANUP_COMPLETED FROM POM_WORK_SCOPE_PROJECTION_CURRENT"))
            .Should().Be("Completed:0",
                "terminal status before cleanup is valid evidence but not a mapper-ready terminal transition");
        var recoveryRequired = await bridge.IngestAsync(
            "cleaner-a",
            Command("recovery-7", 7, WorkScopeProjectionStatus.RecoveryRequired, occurredAt.AddSeconds(-1)));
        var cleanupAcknowledged = await bridge.IngestAsync(
            "cleaner-a",
            Command("completed-8", 8, WorkScopeProjectionStatus.Completed, occurredAt.AddSeconds(2))
                with { TerminalCleanupCompleted = true });

        completedBeforeCleanup.IsSuccess.Should().BeTrue();
        recoveryRequired.IsSuccess.Should().BeTrue();
        recoveryRequired.Value.IsCurrent.Should().BeTrue();
        recoveryRequired.Value.CurrentRevision.Should().Be(7);
        cleanupAcknowledged.IsSuccess.Should().BeTrue();
        cleanupAcknowledged.Value.IsCurrent.Should().BeTrue();
        cleanupAcknowledged.Value.CurrentRevision.Should().Be(8);
        (await database.TextAsync(
            "SELECT EVENT_ID || ':' || PROJECTION_STATUS FROM POM_WORK_SCOPE_PROJECTION_CURRENT"))
            .Should().Be("completed-8:Completed");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(3L);
    }

    [Fact]
    public async Task Sequence_identity_cannot_cross_work_scope_operation_or_pair_run()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();

        (await bridge.IngestAsync("cleaner-a", Command("identity-base"))).IsSuccess.Should().BeTrue();
        var crossScope = await bridge.IngestAsync(
            "cleaner-a", Command("cross-scope", 8) with { WorkScopeId = "WS-2" });
        var crossOperation = await bridge.IngestAsync(
            "cleaner-a", Command("cross-operation", 8) with { OperationKey = "other-operation" });
        var crossPair = await bridge.IngestAsync(
            "cleaner-a", Command("cross-pair", 8) with { PairRunId = "other-pair" });

        new[] { crossScope, crossOperation, crossPair }
            .Should().OnlyContain(static result =>
                result.IsFailure && result.Error.Code == "Projection.SequenceIdentityConflict");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
    }

    [Fact]
    public async Task Work_scope_binding_rejects_another_source_equipment_or_sequence_without_partial_evidence()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();
        (await bridge.IngestAsync("cleaner-a", Command("binding-owner", 7)))
            .IsSuccess.Should().BeTrue();

        var anotherSequence = await bridge.IngestAsync(
            "cleaner-a",
            Command("binding-sequence", 8) with { SequenceRunId = "sequence-2" });
        var anotherSource = await bridge.IngestAsync(
            "cleaner-b",
            Command("binding-source", 8) with
            {
                ClientId = "cleaner-b",
                SequenceRunId = "source-sequence",
            });
        var anotherEquipment = await bridge.IngestAsync(
            "cleaner-a",
            Command("binding-equipment", 8) with
            {
                EquipmentId = "EQ-OTHER",
                SequenceRunId = "equipment-sequence",
            });

        new[] { anotherSequence, anotherSource, anotherEquipment }
            .Should().OnlyContain(static result =>
                result.IsFailure && result.Error.Code == "Projection.WorkScopeBindingConflict");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CARRIER"))
            .Should().Be(2L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CURRENT"))
            .Should().Be(1L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION"))
            .Should().Be(1L);
        (await database.TextAsync("""
            SELECT SOURCE_CLIENT_ID || ':' || EQUIPMENT_ID || ':' || SEQUENCE_RUN_ID || ':' || EVENT_ID
              FROM POM_WORK_SCOPE_PROJECTION_CURRENT WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("cleaner-a:EQ-1:sequence-1:binding-owner");
    }

    [Fact]
    public async Task Concurrent_first_streams_leave_exactly_one_work_scope_binding()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var first = database.CreateBridge();
        var second = database.CreateBridge();

        var results = await Task.WhenAll(
            first.IngestAsync(
                "cleaner-a",
                Command("binding-race-a", 7) with { SequenceRunId = "sequence-a" }),
            second.IngestAsync(
                "cleaner-a",
                Command("binding-race-b", 7) with { SequenceRunId = "sequence-b" }));

        results.Count(static result => result.IsSuccess).Should().Be(1);
        results.Count(static result =>
                result.IsFailure && result.Error.Code == "Projection.WorkScopeBindingConflict")
            .Should().Be(1);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CARRIER"))
            .Should().Be(2L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CURRENT"))
            .Should().Be(1L);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION"))
            .Should().Be(1L);
        (await database.ScalarAsync("""
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
             WHERE EVENT_TYPE='Pending'
            """)).Should().Be(1L);
    }

    [Fact]
    public async Task Sequence_identity_cannot_change_carrier_or_recipe_snapshot_binding()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();
        (await bridge.IngestAsync("cleaner-a", Command("binding-base"))).IsSuccess.Should().BeTrue();
        var changedCarriers = Command("binding-carrier", 8) with
        {
            Carriers =
            [
                new WorkScopeProjectionCarrierDto("front", "OTHER-F", "RUN-F"),
                new WorkScopeProjectionCarrierDto("rear", "CARRIER-R", "RUN-R"),
            ],
        };
        var changedRecipe = Command("binding-recipe", 8) with
        {
            RecipeId = "RECIPE-2",
            RecipeSnapshotHash = new string('c', 64),
        };

        var carrierConflict = await bridge.IngestAsync("cleaner-a", changedCarriers);
        var recipeConflict = await bridge.IngestAsync("cleaner-a", changedRecipe);

        new[] { carrierConflict, recipeConflict }.Should().OnlyContain(static result =>
            result.IsFailure && result.Error.Code == "Projection.SequenceIdentityConflict");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
    }

    [Fact]
    public async Task Two_repository_instances_share_one_db_without_duplicate_event_evidence()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var first = database.CreateBridge();
        var second = database.CreateBridge();

        var results = await Task.WhenAll(
            first.IngestAsync("cleaner-a", Command()),
            second.IngestAsync("cleaner-a", Command()));

        results.Should().OnlyContain(static result => result.IsSuccess);
        results.Count(static result => result.Value.Replay).Should().Be(1);
        results.Count(static result => !result.Value.Replay).Should().Be(1);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(1L);
    }

    [Fact]
    public async Task Inbox_evidence_rejects_update_and_delete()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        (await database.CreateBridge().IngestAsync("cleaner-a", Command())).IsSuccess.Should().BeTrue();

        var update = async () => await database.ExecuteAsync(
            "UPDATE POM_WORK_SCOPE_PROJECTION_INBOX SET RESULT_CODE = 'MUTATED'");
        var delete = async () => await database.ExecuteAsync(
            "DELETE FROM POM_WORK_SCOPE_PROJECTION_INBOX");
        var replaceInbox = async () => await database.ExecuteAsync("""
            PRAGMA recursive_triggers=OFF;
            INSERT OR REPLACE INTO POM_WORK_SCOPE_PROJECTION_INBOX
            SELECT * FROM POM_WORK_SCOPE_PROJECTION_INBOX
             WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'event-1';
            """);

        await update.Should().ThrowAsync<SqliteException>()
            .WithMessage("*append-only*");
        await delete.Should().ThrowAsync<SqliteException>()
            .WithMessage("*append-only*");
        await replaceInbox.Should().ThrowAsync<SqliteException>()
            .WithMessage("*replacement is forbidden*");
        var rebind = async () => await database.ExecuteAsync(
            "UPDATE POM_WORK_SCOPE_PROJECTION_CURRENT SET OPERATION_KEY = 'MUTATED'");
        var rekeySequence = async () => await database.ExecuteAsync(
            "UPDATE POM_WORK_SCOPE_PROJECTION_CURRENT SET SEQUENCE_RUN_ID = 'MUTATED'");
        var deleteCurrent = async () => await database.ExecuteAsync(
            "DELETE FROM POM_WORK_SCOPE_PROJECTION_CURRENT");
        var replaceCurrent = async () => await database.ExecuteAsync("""
            PRAGMA recursive_triggers=OFF;
            INSERT OR REPLACE INTO POM_WORK_SCOPE_PROJECTION_CURRENT
            SELECT * FROM POM_WORK_SCOPE_PROJECTION_CURRENT
             WHERE SOURCE_CLIENT_ID = 'cleaner-a'
               AND EQUIPMENT_ID = 'EQ-1'
               AND SEQUENCE_RUN_ID = 'sequence-1';
            """);
        await rebind.Should().ThrowAsync<SqliteException>()
            .WithMessage("*identity is immutable*");
        await rekeySequence.Should().ThrowAsync<SqliteException>()
            .WithMessage("*identity is immutable*");
        await deleteCurrent.Should().ThrowAsync<SqliteException>()
            .WithMessage("*not deletable*");
        await replaceCurrent.Should().ThrowAsync<SqliteException>()
            .WithMessage("*replacement is forbidden*");
    }

    [Fact]
    public async Task Direct_writer_cannot_roll_the_current_cursor_back_to_an_exact_older_event()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();
        (await bridge.IngestAsync("cleaner-a", Command("event-8", 8))).IsSuccess.Should().BeTrue();
        (await bridge.IngestAsync("cleaner-a", Command("event-7", 7))).IsSuccess.Should().BeTrue();

        var rollback = async () => await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_CURRENT
               SET EVENT_ID = 'event-7',
                   SOURCE_REVISION = (SELECT SOURCE_REVISION FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                       WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'event-7'),
                   PROJECTION_STATUS = (SELECT PROJECTION_STATUS FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                         WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'event-7'),
                   TERMINAL_CLEANUP_COMPLETED = (SELECT TERMINAL_CLEANUP_COMPLETED FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                                 WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'event-7'),
                   OCCURRED_AT = (SELECT OCCURRED_AT FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                   WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'event-7'),
                   ACCEPTED_AT = (SELECT ACCEPTED_AT FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                   WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'event-7')
             WHERE SOURCE_CLIENT_ID = 'cleaner-a'
               AND EQUIPMENT_ID = 'EQ-1'
               AND SEQUENCE_RUN_ID = 'sequence-1';
            """);

        await rollback.Should().ThrowAsync<SqliteException>()
            .WithMessage("*advance monotonically*");
        (await database.ScalarAsync(
            "SELECT SOURCE_REVISION FROM POM_WORK_SCOPE_PROJECTION_CURRENT"))
            .Should().Be(8L);
    }

    [Fact]
    public async Task Sqlite_direct_writer_cannot_bypass_cleanup_or_work_scope_ownership_invariants()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        (await database.CreateBridge().IngestAsync("cleaner-a", Command("guard-base")))
            .IsSuccess.Should().BeTrue();
        const string insertColumns = """
            (SOURCE_CLIENT_ID, EVENT_ID, REQUEST_HASH, WORK_SCOPE_ID, EQUIPMENT_ID,
             OPERATION_KEY, PAIR_RUN_ID, SEQUENCE_RUN_ID, SOURCE_REVISION, PROJECTION_STATUS,
             TERMINAL_CLEANUP_COMPLETED, RECIPE_ID, RECIPE_SNAPSHOT_HASH, PROGRAM_HASH,
             CARRIERS_JSON, RESULT_CODE, RESULT_METADATA_JSON, OCCURRED_AT, PAYLOAD_JSON,
             ACCEPTED_AT, CREATED_BY, CREATED_AT)
            """;
        var invalidCleanup = async () => await database.ExecuteAsync($"""
            INSERT INTO POM_WORK_SCOPE_PROJECTION_INBOX {insertColumns}
            SELECT SOURCE_CLIENT_ID, 'direct-invalid-cleanup', '{new string('D', 64)}',
                   WORK_SCOPE_ID, EQUIPMENT_ID, OPERATION_KEY, PAIR_RUN_ID, SEQUENCE_RUN_ID,
                   SOURCE_REVISION + 1, 'Running', 1, RECIPE_ID, RECIPE_SNAPSHOT_HASH,
                   PROGRAM_HASH, CARRIERS_JSON, RESULT_CODE, RESULT_METADATA_JSON,
                   OCCURRED_AT, PAYLOAD_JSON, ACCEPTED_AT, CREATED_BY, CREATED_AT
              FROM POM_WORK_SCOPE_PROJECTION_INBOX
             WHERE EVENT_ID = 'guard-base';
            """);
        var unknownScope = async () => await database.ExecuteAsync($"""
            INSERT INTO POM_WORK_SCOPE_PROJECTION_INBOX {insertColumns}
            SELECT SOURCE_CLIENT_ID, 'direct-unknown-scope', '{new string('E', 64)}',
                   'WS-MISSING', EQUIPMENT_ID, OPERATION_KEY, PAIR_RUN_ID, SEQUENCE_RUN_ID,
                   SOURCE_REVISION + 1, PROJECTION_STATUS, 0, RECIPE_ID, RECIPE_SNAPSHOT_HASH,
                   PROGRAM_HASH, CARRIERS_JSON, RESULT_CODE, RESULT_METADATA_JSON,
                   OCCURRED_AT, PAYLOAD_JSON, ACCEPTED_AT, CREATED_BY, CREATED_AT
              FROM POM_WORK_SCOPE_PROJECTION_INBOX
             WHERE EVENT_ID = 'guard-base';
            """);
        var deleteScope = async () => await database.ExecuteAsync(
            "DELETE FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID = 'WS-1';");
        var rekeyScope = async () => await database.ExecuteAsync(
            "UPDATE POM_WORK_SCOPE SET WORK_SCOPE_ID = 'WS-REKEYED' WHERE WORK_SCOPE_ID = 'WS-1';");
        var replaceScope = async () => await database.ExecuteAsync(
            "INSERT OR REPLACE INTO POM_WORK_SCOPE SELECT * FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID = 'WS-1';");
        var replaceByUpdateCollision = async () => await database.ExecuteAsync(
            "UPDATE OR REPLACE POM_WORK_SCOPE SET WORK_SCOPE_ID = 'WS-1' WHERE WORK_SCOPE_ID = 'WS-UNASSIGNED';");

        await invalidCleanup.Should().ThrowAsync<SqliteException>();
        await unknownScope.Should().ThrowAsync<SqliteException>()
            .WithMessage("*exact equipment ownership*");
        await deleteScope.Should().ThrowAsync<SqliteException>()
            .WithMessage("*referenced by projection evidence*");
        await rekeyScope.Should().ThrowAsync<SqliteException>()
            .WithMessage("*identity is referenced by projection evidence*");
        await replaceScope.Should().ThrowAsync<SqliteException>()
            .WithMessage("*replacement is forbidden*");
        await replaceByUpdateCollision.Should().ThrowAsync<SqliteException>()
            .WithMessage("*identity is referenced by projection evidence*");
    }

    [Fact]
    public async Task Direct_writer_cannot_bind_a_current_sequence_to_another_sequences_event()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var bridge = database.CreateBridge();
        (await bridge.IngestAsync("cleaner-a", Command("sequence-1-event", 7)))
            .IsSuccess.Should().BeTrue();
        (await bridge.IngestAsync(
            "cleaner-a",
            Command("sequence-2-event", 8) with
            {
                WorkScopeId = "WS-2",
                SequenceRunId = "sequence-2",
            }))
            .IsSuccess.Should().BeTrue();

        var crossSequence = async () => await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_CURRENT
               SET EVENT_ID = 'sequence-2-event',
                   SOURCE_REVISION = 8,
                   PROJECTION_STATUS = (SELECT PROJECTION_STATUS FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                         WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'sequence-2-event'),
                   TERMINAL_CLEANUP_COMPLETED = (SELECT TERMINAL_CLEANUP_COMPLETED FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                                 WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'sequence-2-event'),
                   OCCURRED_AT = (SELECT OCCURRED_AT FROM POM_WORK_SCOPE_PROJECTION_INBOX
                                   WHERE SOURCE_CLIENT_ID = 'cleaner-a' AND EVENT_ID = 'sequence-2-event'),
                    ACCEPTED_AT = '2099-01-01T00:00:00.0000000Z',
                    UPDATED_AT = '2099-01-01T00:00:00.0000000Z'
             WHERE SOURCE_CLIENT_ID = 'cleaner-a'
               AND EQUIPMENT_ID = 'EQ-1'
               AND SEQUENCE_RUN_ID = 'sequence-1';
            """);

        await crossSequence.Should().ThrowAsync<SqliteException>()
            .WithMessage("*exact inbox event*");
        (await database.TextAsync("""
            SELECT EVENT_ID FROM POM_WORK_SCOPE_PROJECTION_CURRENT
             WHERE SOURCE_CLIENT_ID = 'cleaner-a'
               AND EQUIPMENT_ID = 'EQ-1'
               AND SEQUENCE_RUN_ID = 'sequence-1'
            """)).Should().Be("sequence-1-event");
    }

    private static WorkScopeProjectionCommand Command(
        string eventId = "event-1",
        long revision = 7,
        WorkScopeProjectionStatus status = WorkScopeProjectionStatus.Running,
        DateTimeOffset? occurredAt = null) => new(
        ClientId: "cleaner-a",
        EventId: eventId,
        WorkScopeId: "WS-1",
        EquipmentId: "EQ-1",
        OperationKey: "clean-pair-1",
        PairRunId: "pair-1",
        SequenceRunId: "sequence-1",
        Status: status,
        TerminalCleanupCompleted: false,
        RecipeId: "RECIPE-1",
        RecipeSnapshotHash: new string('a', 64),
        ProgramHash: new string('b', 64),
        Carriers:
        [
            new WorkScopeProjectionCarrierDto("front", "CARRIER-F", "RUN-F"),
            new WorkScopeProjectionCarrierDto("rear", "CARRIER-R", "RUN-R"),
        ],
        OccurredAt: occurredAt ?? new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
        Revision: revision,
        ResultCode: "PAIR_RUNNING");

    private sealed class ProjectionDatabase : IAsyncDisposable
    {
        private ProjectionDatabase(string path, string connectionString, EesDataSource dataSource)
        {
            Path = path;
            ConnectionString = connectionString;
            DataSource = dataSource;
        }

        private string Path { get; }
        private string ConnectionString { get; }
        private EesDataSource DataSource { get; }

        public static async Task<ProjectionDatabase> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"nexa-workscope-projection-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path};Cache=Shared;Default Timeout=30";
            SqliteSchemaInitializer.Apply(
                connectionString,
                [new PomWorkScopeProjectionSqliteSchemaContribution()]);
            var dataSource = new EesDataSource
            {
                Provider = new SqliteProvider(),
                ConnectionString = connectionString,
            };
            await AddScopeAsync(dataSource, "WS-1");
            await AddScopeAsync(dataSource, "WS-2");
            await AddScopeAsync(dataSource, "WS-UNASSIGNED");
            return new ProjectionDatabase(path, connectionString, dataSource);
        }

        private static async Task AddScopeAsync(EesDataSource dataSource, string workScopeId)
        {
            var isPrimary = workScopeId == "WS-1";
            var equipmentId = workScopeId == "WS-UNASSIGNED" ? null : "EQ-1";
            var targetId = workScopeId switch
            {
                "WS-1" => "EQ-1",
                "WS-2" => "OTHER-2",
                _ => workScopeId,
            };
            var scope = PomWorkScope.Create(
                workScopeId, "PLANT-1",
                isPrimary ? PomWorkScopeType.Equipment : PomWorkScopeType.Other,
                targetId, "Cleaner scope",
                null, equipmentId, null, null, null, null, 1m, null, null, "tester");
            scope.IsSuccess.Should().BeTrue();
            scope.Value.SetCreateIdentity($"test:create:{workScopeId}", new string('C', 64));
            await new WorkScopeRepository(dataSource).AddAsync(scope.Value);
        }

        public IWorkScopeProjectionBridge CreateBridge() => new WorkScopeProjectionBridge(
            new WorkScopeProjectionService(new WorkScopeProjectionRepository(DataSource)));

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<string?> TextAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
            return ValueTask.CompletedTask;
        }
    }
}
