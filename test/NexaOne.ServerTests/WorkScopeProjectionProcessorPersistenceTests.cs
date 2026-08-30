using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Domain;
using NexaOne.POM.Infrastructure;
using NexaOne.RMS.Infrastructure;
using NexaOne.ServiceContracts.Pom;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class WorkScopeProjectionProcessorPersistenceTests
{
    [Fact]
    public void Readiness_update_probes_cover_exactly_every_worker_mutation_column()
    {
        UpdatedColumns(StoreSql("ReadinessApplicationUpdateSql"))
            .Should().BeEquivalentTo(
                UpdatedColumns(StoreSql("ClaimSql"))
                    .Union(UpdatedColumns(StoreSql("TransitionSql")),
                        StringComparer.OrdinalIgnoreCase));
        UpdatedColumns(StoreSql("ReadinessScopeUpdateSql"))
            .Should().BeEquivalentTo(UpdatedColumns(StoreSql("UpdateScopeSql")));
        UpdatedColumns(StoreSql("ReadinessAuthorityUpdateSql"))
            .Should().BeEquivalentTo(UpdatedColumns(StoreSql("AdvanceProjectionAuthoritySql")));
    }

    [Fact]
    public async Task Readiness_preflight_validates_the_complete_schema_without_mutating_data()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        (await database.Bridge.IngestAsync("cleaner-a", Command("preflight-ready", 7)))
            .IsSuccess.Should().BeTrue();
        var before = await database.TextAsync("""
            SELECT (SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION) || ':' ||
                   (SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT) || ':' ||
                   (SELECT COUNT(*) FROM POM_WORK_SCOPE_EXECUTION) || ':' ||
                   (SELECT APPLICATION_STATUS || ':' || ATTEMPT_COUNT || ':' || LEASE_FENCE
                      FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
                     WHERE EVENT_ID='preflight-ready') || ':' ||
                   (SELECT STATUS || ':' || VERSION_NO
                      FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1')
            """);

        await database.Store.EnsureReadyAsync();

        (await database.TextAsync("""
            SELECT (SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION) || ':' ||
                   (SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT) || ':' ||
                   (SELECT COUNT(*) FROM POM_WORK_SCOPE_EXECUTION) || ':' ||
                   (SELECT APPLICATION_STATUS || ':' || ATTEMPT_COUNT || ':' || LEASE_FENCE
                      FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
                     WHERE EVENT_ID='preflight-ready') || ':' ||
                   (SELECT STATUS || ':' || VERSION_NO
                      FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1')
            """)).Should().Be(before);
    }

    [Fact]
    public async Task Enabled_worker_fails_startup_before_claim_when_v157_schema_is_missing()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        (await database.Bridge.IngestAsync("cleaner-a", Command("preflight-missing", 7)))
            .IsSuccess.Should().BeTrue();
        await database.ExecuteAsync(
            "DROP TABLE POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT;");
        var worker = new WorkScopeProjectionWorker(
            database.Processor(new FixedPolicy(WorkScopeProjectionDecision.Observe("unused"))),
            "worker-preflight-missing",
            enabled: true);

        var start = () => worker.StartAsync(CancellationToken.None);

        await start.Should().ThrowAsync<SqliteException>()
            .WithMessage("*POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT*");
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS || ':' || ATTEMPT_COUNT || ':' || LEASE_FENCE
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='preflight-missing'
            """)).Should().Be("Pending:0:0",
                "a failed startup preflight must not enter the claim loop");
    }

    [Fact]
    public async Task Readiness_preflight_rejects_a_missing_v157_single_scope_binding_fence()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        await database.ExecuteAsync(
            "DROP INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE;");

        var ready = () => database.Store.EnsureReadyAsync();

        await ready.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unique current WorkScope binding index*missing*");
    }

    [Fact]
    public async Task Readiness_preflight_rejects_a_same_named_index_on_the_wrong_key()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        await database.ExecuteAsync("""
            DROP INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE;
            CREATE UNIQUE INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE
                ON POM_WORK_SCOPE_PROJECTION_CURRENT (EVENT_ID);
            """);

        var ready = () => database.Store.EnsureReadyAsync();

        await ready.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unique current WorkScope binding index*missing*");
    }

    [Fact]
    public async Task Readiness_preflight_rejects_a_work_scope_index_with_case_folding_collation()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        await database.ExecuteAsync("""
            DROP INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE;
            CREATE UNIQUE INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE
                ON POM_WORK_SCOPE_PROJECTION_CURRENT (WORK_SCOPE_ID COLLATE NOCASE);
            """);

        var ready = () => database.Store.EnsureReadyAsync();

        await ready.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unique current WorkScope binding index*missing*");
    }

    [Fact]
    public async Task Readiness_preflight_rejects_a_partial_same_named_work_scope_index()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        await database.ExecuteAsync("""
            DROP INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE;
            CREATE UNIQUE INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE
                ON POM_WORK_SCOPE_PROJECTION_CURRENT (WORK_SCOPE_ID COLLATE BINARY)
             WHERE WORK_SCOPE_ID <> '';
            """);

        var ready = () => database.Store.EnsureReadyAsync();

        await ready.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unique current WorkScope binding index*missing*");
    }

    [Fact]
    public async Task Ingest_is_durable_acceptance_and_worker_applies_all_effects_atomically_once()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var command = Command(
            "terminal-1", 7, WorkScopeProjectionStatus.Completed,
            terminalCleanupCompleted: true);
        var receipt = await database.Bridge.IngestAsync("cleaner-a", command);

        receipt.IsSuccess.Should().BeTrue();
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS || ':' || ATTEMPT_COUNT
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
            """)).Should().Be("Pending:0");
        (await database.TextAsync("""
            SELECT STATUS || ':' || VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Created:1", "transport acceptance must not run project policy inline");

        var decision = WorkScopeProjectionDecision.Apply(
            "CleanerCompleted",
            [
                new WorkScopeProjectionEffect(WorkScopeAction.Release),
                new WorkScopeProjectionEffect(WorkScopeAction.Start),
                new WorkScopeProjectionEffect(
                    WorkScopeAction.Complete, 1m, 0m, "CARRIER-F", "CLEANED"),
            ]);
        var processor = database.Processor(new FixedPolicy(decision));
        var applied = await processor.ProcessNextAsync("worker-a");
        var noReplay = await processor.ProcessNextAsync("worker-a");

        applied!.Kind.Should().Be(WorkScopeProjectionCommitKind.Applied);
        noReplay.Should().BeNull();
        (await database.TextAsync("""
            SELECT STATUS || ':' || VERSION_NO || ':' || COMPLETE_QTY
              FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Completed:4:1");
        (await database.TextAsync("""
            SELECT LAST_APPLIED_VERSION_NO || ':' ||
                   CASE WHEN LAST_APPLIED_AT IS NULL THEN 'NULL' ELSE 'SET' END
              FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("4:SET");
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_EXECUTION WHERE WORK_SCOPE_ID='WS-1'"))
            .Should().Be(3);
        (await database.ScalarAsync("""
            SELECT COUNT(DISTINCT EXECUTION_ID) FROM POM_WORK_SCOPE_EXECUTION
            """)).Should().Be(3);
        (await database.ScalarAsync("""
            SELECT COUNT(DISTINCT IDEMPOTENCY_KEY) FROM POM_WORK_SCOPE_EXECUTION
            """)).Should().Be(3);
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS || ':' || ATTEMPT_COUNT || ':' || LEASE_FENCE
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
            """)).Should().Be("Applied:1:1");
        (await database.TextAsync("""
            SELECT GROUP_CONCAT(EVENT_TYPE, ',')
              FROM (SELECT EVENT_TYPE FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                    ORDER BY OCCURRED_AT, APPLICATION_EVENT_ID)
            """)).Should().Contain("Pending").And.Contain("Processing").And.Contain("Applied");

        var replay = await database.Bridge.IngestAsync("cleaner-a", command);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Replay.Should().BeTrue();
        (await database.ScalarAsync(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION"))
            .Should().Be(1);
        (await processor.ProcessNextAsync("worker-a")).Should().BeNull(
            "a transport replay cannot reopen terminal application state");
    }

    [Fact]
    public async Task Rejected_later_effect_leaves_scope_and_execution_ledger_unchanged()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        (await database.Bridge.IngestAsync("cleaner-a", Command("bad-effects", 7)))
            .IsSuccess.Should().BeTrue();
        var processor = database.Processor(new FixedPolicy(WorkScopeProjectionDecision.Apply(
            "InvalidTransitionSet",
            [
                new WorkScopeProjectionEffect(WorkScopeAction.Release),
                new WorkScopeProjectionEffect(WorkScopeAction.Release),
            ])));

        var result = await processor.ProcessNextAsync("worker-a");

        result!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        (await database.TextAsync("""
            SELECT STATUS || ':' || VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Created:1");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_EXECUTION"))
            .Should().Be(0);
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS || ':' || LAST_ERROR_CODE
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
            """)).Should().Be("Quarantined:Projection.DomainRejected");
    }

    [Fact]
    public async Task New_current_event_supersedes_an_inflight_old_claim_and_only_new_event_is_claimable()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        (await database.Bridge.IngestAsync("cleaner-a", Command("event-7", 7)))
            .IsSuccess.Should().BeTrue();
        var store = database.Store;
        var oldClaim = await store.TryClaimNextAsync("worker-old", TimeSpan.FromMinutes(2));
        oldClaim.Should().NotBeNull();

        (await database.Bridge.IngestAsync("cleaner-a", Command("event-8", 8)))
            .IsSuccess.Should().BeTrue();
        var oldDecision = ProjectionDecisionCodec.Prepare(
            new WorkScopeProjectionPolicyIdentity("test", "1"),
            oldClaim!.Event,
            WorkScopeProjectionDecision.Observe("OldEvidence"));
        var oldCommit = await store.CommitDecisionAsync(oldClaim, oldDecision);
        var newClaim = await store.TryClaimNextAsync("worker-new", TimeSpan.FromMinutes(2));

        oldCommit.Kind.Should().Be(WorkScopeProjectionCommitKind.LeaseLost);
        newClaim!.Event.EventId.Should().Be("event-8");
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='event-7'
            """)).Should().Be("Superseded");
        (await database.ScalarAsync("""
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE APPLICATION_STATUS='Processing'
            """)).Should().Be(1);
    }

    [Fact]
    public async Task Retry_keeps_the_current_event_pending_and_reclaim_advances_attempt_and_fence()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var command = Command("retry-1", 7);
        (await database.Bridge.IngestAsync("cleaner-a", command))
            .IsSuccess.Should().BeTrue();
        var processor = database.Processor(new FixedPolicy(
            WorkScopeProjectionDecision.Retry("AwaitDependency", TimeSpan.FromMilliseconds(10))));

        var retried = await processor.ProcessNextAsync("worker-a");
        var scheduledAt = await database.TextAsync("""
            SELECT NEXT_ATTEMPT_AT FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='retry-1'
            """);
        var auditCount = await database.ScalarAsync("""
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
             WHERE EVENT_ID='retry-1'
            """);
        var replay = await database.Bridge.IngestAsync("cleaner-a", command);
        var tooEarly = await database.Store.TryClaimNextAsync("worker-b", TimeSpan.FromMinutes(2));

        replay.IsSuccess.Should().BeTrue();
        replay.Value.Replay.Should().BeTrue();
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='retry-1'
            """)).Should().Be("Retry");
        (await database.TextAsync("""
            SELECT NEXT_ATTEMPT_AT FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='retry-1'
            """)).Should().Be(scheduledAt,
                "an ordinary transport replay must preserve the scheduled retry window");
        (await database.ScalarAsync("""
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
             WHERE EVENT_ID='retry-1'
            """)).Should().Be(auditCount);

        await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET NEXT_ATTEMPT_AT='2000-01-01 00:00:00'
             WHERE EVENT_ID='retry-1' AND APPLICATION_STATUS='Retry'
            """);
        var reclaimed = await database.Store.TryClaimNextAsync("worker-b", TimeSpan.FromMinutes(2));

        retried!.Kind.Should().Be(WorkScopeProjectionCommitKind.RetryScheduled);
        tooEarly.Should().BeNull();
        reclaimed.Should().NotBeNull();
        reclaimed!.AttemptCount.Should().Be(2);
        reclaimed.LeaseFence.Should().Be(2);
    }

    [Fact]
    public async Task Ordinary_hold_is_fenced_after_claim_and_observation_commits_without_scope_change()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        await ApplyProjectionAsync(
            database,
            Command("setup-running", 1),
            new WorkScopeProjectionEffect(WorkScopeAction.Release),
            new WorkScopeProjectionEffect(WorkScopeAction.Start));
        (await database.Bridge.IngestAsync("cleaner-a", Command("running-observe", 2)))
            .IsSuccess.Should().BeTrue();
        var claim = await database.Store.TryClaimNextAsync("worker-a", TimeSpan.FromMinutes(2));
        var decision = ProjectionDecisionCodec.Prepare(
            new WorkScopeProjectionPolicyIdentity("test-policy", "1"),
            claim!.Event,
            WorkScopeProjectionDecision.Observe("AlreadyRunning"));

        var manualHold = await database.WorkScopes.ExecuteAsync(
            "WS-1",
            new WorkScopeOperationCommand(
                WorkScopeAction.Hold,
                "manual-hold",
                3,
                ActorId: "operator"));
        var committed = await database.Store.CommitDecisionAsync(claim, decision);

        manualHold.IsFailure.Should().BeTrue();
        manualHold.Error.Code.Should().Be("POM.WorkScope.ProjectionOwned");
        committed.Kind.Should().Be(WorkScopeProjectionCommitKind.Observed);
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='running-observe'
            """)).Should().Be("Observed");
        (await database.TextAsync("""
            SELECT STATUS || ':' || IS_HOLD || ':' || VERSION_NO
              FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Started:N:3");
        (await database.ScalarAsync("""
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
             WHERE EVENT_ID='running-observe' AND EVENT_TYPE='Observed'
            """)).Should().Be(1);
    }

    [Fact]
    public async Task Recovery_observation_quarantines_an_out_of_band_lineage_gap()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        await ApplyProjectionAsync(
            database,
            Command("setup-recovery", 1),
            new WorkScopeProjectionEffect(WorkScopeAction.Release),
            new WorkScopeProjectionEffect(WorkScopeAction.Start),
            new WorkScopeProjectionEffect(WorkScopeAction.Hold));
        (await database.Bridge.IngestAsync(
            "cleaner-a",
            Command("recovery-observe", 2, WorkScopeProjectionStatus.RecoveryRequired)))
            .IsSuccess.Should().BeTrue();
        var claim = await database.Store.TryClaimNextAsync("worker-a", TimeSpan.FromMinutes(2));
        var decision = ProjectionDecisionCodec.Prepare(
            new WorkScopeProjectionPolicyIdentity("test-policy", "1"),
            claim!.Event,
            WorkScopeProjectionDecision.Observe("RecoveryHeld"));

        await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE
               SET IS_HOLD='N', VERSION_NO=VERSION_NO+1
             WHERE WORK_SCOPE_ID='WS-1';
            """);
        var committed = await database.Store.CommitDecisionAsync(claim, decision);

        committed.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS || ':' || LAST_ERROR_CODE
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='recovery-observe'
            """)).Should().Be("Quarantined:Projection.LineageGap");
        (await database.TextAsync("""
            SELECT STATUS || ':' || IS_HOLD || ':' || VERSION_NO
              FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Started:N:5");
        (await database.ScalarAsync("""
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
             WHERE EVENT_ID='recovery-observe' AND EVENT_TYPE='Observed'
            """)).Should().Be(0);
    }

    [Fact]
    public async Task Apply_before_scope_creation_is_quarantined_without_domain_effects()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var occurredBeforeCreation = DateTimeOffset.UtcNow.AddDays(-1);
        (await database.Bridge.IngestAsync(
            "cleaner-a",
            Command("before-scope", 7, occurredAt: occurredBeforeCreation)))
            .IsSuccess.Should().BeTrue();
        var processor = database.Processor(new FixedPolicy(WorkScopeProjectionDecision.Apply(
            "ReleaseFromEvidence",
            [new WorkScopeProjectionEffect(WorkScopeAction.Release)])));

        var committed = await processor.ProcessNextAsync("worker-a");

        committed!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS || ':' || LAST_ERROR_CODE
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='before-scope'
            """)).Should().Be("Quarantined:Projection.OccurredBeforeScopeCreated");
        (await database.TextAsync("""
            SELECT STATUS || ':' || VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Created:1");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_EXECUTION"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Complete_before_existing_start_time_is_quarantined_without_projection_effects()
    {
        await using var database = await ProjectionDatabase.CreateAsync();
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(5);
        await ApplyProjectionAsync(
            database,
            Command("setup-started", 1, occurredAt: startedAt),
            new WorkScopeProjectionEffect(WorkScopeAction.Release),
            new WorkScopeProjectionEffect(WorkScopeAction.Start));
        var occurredBeforeStart = startedAt.AddTicks(-1);
        (await database.Bridge.IngestAsync(
            "cleaner-a",
            Command(
                "complete-before-start",
                2,
                WorkScopeProjectionStatus.Completed,
                terminalCleanupCompleted: true,
                occurredAt: occurredBeforeStart)))
            .IsSuccess.Should().BeTrue();
        var processor = database.Processor(new FixedPolicy(WorkScopeProjectionDecision.Apply(
            "CompleteFromEvidence",
            [new WorkScopeProjectionEffect(
                WorkScopeAction.Complete, 1m, 0m, "CARRIER-F", "CLEANED")])));

        var committed = await processor.ProcessNextAsync("worker-a");

        committed!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS || ':' || LAST_ERROR_CODE
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='complete-before-start'
            """)).Should().Be("Quarantined:Projection.CompletedBeforeScopeStarted");
        (await database.TextAsync("""
            SELECT STATUS || ':' || VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Started:3");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_EXECUTION"))
            .Should().Be(2, "only the two projection-owned setup actions should remain");
    }

    private static WorkScopeProjectionCommand Command(
        string eventId,
        long revision,
        WorkScopeProjectionStatus status = WorkScopeProjectionStatus.Running,
        bool terminalCleanupCompleted = false,
        DateTimeOffset? occurredAt = null) => new(
        "cleaner-a",
        eventId,
        "WS-1",
        "EQ-1",
        "operation-1",
        "pair-1",
        "sequence-1",
        status,
        terminalCleanupCompleted,
        "RECIPE-1",
        new string('A', 64),
        new string('B', 64),
        [
            new WorkScopeProjectionCarrierDto("front", "CARRIER-F", "RUN-F"),
            new WorkScopeProjectionCarrierDto("rear", "CARRIER-R", "RUN-R"),
        ],
        occurredAt ?? DateTimeOffset.UtcNow,
        revision,
        status.ToString().ToUpperInvariant());

    private static async Task ApplyProjectionAsync(
        ProjectionDatabase database,
        WorkScopeProjectionCommand command,
        params WorkScopeProjectionEffect[] effects)
    {
        var accepted = await database.Bridge.IngestAsync(command.ClientId, command);
        accepted.IsSuccess.Should().BeTrue(
            accepted.IsFailure ? accepted.Error.Description : null);
        var committed = await database.Processor(new FixedPolicy(
                WorkScopeProjectionDecision.Apply($"setup:{command.EventId}", effects)))
            .ProcessNextAsync($"worker:{command.EventId}");
        committed.Should().NotBeNull();
        committed!.Kind.Should().Be(WorkScopeProjectionCommitKind.Applied);
    }

    private static string StoreSql(string fieldName)
    {
        var field = typeof(WorkScopeProjectionStore).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull($"{fieldName} is part of the worker readiness contract");
        return (string)field!.GetRawConstantValue()!;
    }

    private static HashSet<string> UpdatedColumns(string sql)
    {
        var setStart = sql.IndexOf("SET", StringComparison.OrdinalIgnoreCase);
        var whereStart = sql.IndexOf("WHERE", setStart, StringComparison.OrdinalIgnoreCase);
        setStart.Should().BeGreaterThanOrEqualTo(0);
        whereStart.Should().BeGreaterThan(setStart);
        var setClause = sql[(setStart + "SET".Length)..whereStart];
        return Regex.Matches(
                setClause,
                @"(?:^|,)\s*([A-Z][A-Z0-9_]*)\s*=",
                RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FixedPolicy(WorkScopeProjectionDecision decision)
        : IWorkScopeProjectionPolicy
    {
        public WorkScopeProjectionPolicyIdentity Identity { get; } = new("test-policy", "1");
        public WorkScopeProjectionDecision Decide(WorkScopeProjectionContext context) => decision;
    }

    private sealed class ProjectionDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly string _connectionString;

        private ProjectionDatabase(string path, string connectionString, EesDataSource dataSource)
        {
            _path = path;
            _connectionString = connectionString;
            Store = new WorkScopeProjectionStore(dataSource);
            Bridge = new WorkScopeProjectionBridge(
                new WorkScopeProjectionService(new WorkScopeProjectionRepository(dataSource)));
            WorkScopes = new WorkScopeBridge(
                new WorkScopeService(new WorkScopeRepository(dataSource), null));
        }

        public WorkScopeProjectionStore Store { get; }
        public IWorkScopeProjectionBridge Bridge { get; }
        public IWorkScopeBridge WorkScopes { get; }

        public static async Task<ProjectionDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"nexa-projection-processor-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path};Cache=Shared;Default Timeout=30";
            SqliteSchemaInitializer.Apply(
                connectionString,
                [
                    new PomWorkScopeProjectionSqliteSchemaContribution(),
                    new RmsTrustedAuthoritySqliteSchemaContribution(),
                    new SysTrustedAuthoritySqliteSchemaContribution(),
                ]);
            var dataSource = new EesDataSource
            {
                Provider = new SqliteProvider(),
                ConnectionString = connectionString,
            };
            var scope = PomWorkScope.Create(
                "WS-1", "PLANT-1", PomWorkScopeType.Other, "pair-1", "Cleaner",
                null, "EQ-1", null, "operation-1", "RECIPE-1", 1, 1m, null, null, "tester");
            scope.IsSuccess.Should().BeTrue();
            scope.Value.SetCreateIdentity("test:create:WS-1", new string('C', 64));
            await new WorkScopeRepository(dataSource).AddAsync(scope.Value);
            await ProvisionAuthorityAsync(dataSource);
            return new ProjectionDatabase(path, connectionString, dataSource);
        }

        private static async Task ProvisionAuthorityAsync(EesDataSource dataSource)
        {
            var command = new WorkScopeProjectionAuthorityProvisionCommand(
                "WS-1", "cleaner-a", "EQ-1", "operation-1", "pair-1", "sequence-1",
                "rms-execution-WS-1", "cleaner-program-WS-1", "authority-WS-1", "tester");
            var evidence = new WorkScopeProjectionAuthorityEvidence(
                command.WorkScopeId, command.SourceClientId, command.EquipmentId,
                command.OperationKey, command.PairRunId, command.SequenceRunId,
                command.RecipeExecutionId, "RECIPE-1", 1, "test-recipe-v1",
                new string('A', 64), command.ProgramArtifactId, "test-program-v1",
                new string('B', 64));
            await TrustedAuthorityTestData.SeedSqliteAsync(dataSource.ConnectionString, evidence);
            var bridge = new WorkScopeProjectionAuthorityBridge(
                new WorkScopeProjectionAuthorityService(
                    new WorkScopeProjectionAuthorityRepository(dataSource),
                    new FixedAuthorityValidator(evidence)));
            var result = await bridge.ProvisionAsync(command);
            result.IsSuccess.Should().BeTrue(
                result.IsFailure ? result.Error.Description : null);
        }

        public WorkScopeProjectionProcessor Processor(IWorkScopeProjectionPolicy policy) =>
            new(Store, policy, TimeSpan.FromMinutes(2));

        private sealed class FixedAuthorityValidator(WorkScopeProjectionAuthorityEvidence evidence)
            : IWorkScopeProjectionAuthorityValidatorV2
        {
            public Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
                WorkScopeProjectionAuthorityProvisionCommand command,
                CancellationToken ct = default) => Task.FromResult(
                    WorkScopeProjectionAuthorityValidationDecision.Accepted(evidence));
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<string?> TextAsync(string sql)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }
}
