using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Domain;
using NexaOne.POM.Infrastructure;
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class WorkScopeProjectionAuthorityPersistenceTests
{
    [Fact]
    public async Task Trusted_provision_is_replayable_and_atomically_fences_ordinary_commands()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1");
        var command = ProvisionCommand("WS-1", "rms-execution-1", "sequence-1");

        var unavailable = await database.Authority(
                new RejectingWorkScopeProjectionAuthorityValidator())
            .ProvisionAsync(command);
        unavailable.IsFailure.Should().BeTrue();
        unavailable.Error.Code.Should().Be("Projection.Authority.ValidatorUnavailable");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY"))
            .Should().Be(0);

        var bridge = database.Authority(new FixedValidator(Evidence(command)));
        var provisioned = await bridge.ProvisionAsync(command);
        var replayed = await bridge.ProvisionAsync(command);

        provisioned.IsSuccess.Should().BeTrue();
        provisioned.Value.Replay.Should().BeFalse();
        provisioned.Value.BaselineVersionNo.Should().Be(1);
        provisioned.Value.LastAppliedVersionNo.Should().Be(1);
        replayed.IsSuccess.Should().BeTrue();
        replayed.Value.Should().Be(provisioned.Value with { Replay = true });

        var sameKeyConflict = await bridge.ProvisionAsync(command with
        {
            ProgramArtifactId = "different-program",
        });
        sameKeyConflict.IsFailure.Should().BeTrue();
        sameKeyConflict.Error.Code.Should().Be("Projection.Authority.IdempotencyConflict");
        var anotherEvidence = await bridge.ProvisionAsync(command with
        {
            SequenceRunId = "another-sequence",
            IdempotencyKey = "another-authority-key",
        });
        anotherEvidence.IsFailure.Should().BeTrue();
        anotherEvidence.Error.Code.Should().Be("Projection.Authority.EvidenceAlreadyBound");

        var ordinary = await database.WorkScopes.ExecuteAsync(
            "WS-1",
            new WorkScopeOperationCommand(
                WorkScopeAction.Release,
                "manual-release-1",
                1,
                ActorId: "operator"));
        ordinary.IsFailure.Should().BeTrue();
        ordinary.Error.Code.Should().Be("POM.WorkScope.ProjectionOwned");
        (await database.TextAsync(
                "SELECT STATUS || ':' || VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1'"))
            .Should().Be("Created:1");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_EXECUTION"))
            .Should().Be(0);

        var accepted = await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "event-1", "sequence-1"));
        accepted.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Ingress_requires_exact_authority_before_writing_any_transport_row()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1");

        var missing = await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "missing", "sequence-1"));
        missing.IsFailure.Should().BeTrue();
        missing.Error.Code.Should().Be("Projection.AuthorityRequired");

        var provision = ProvisionCommand("WS-1", "rms-execution-1", "sequence-1");
        (await database.Authority(new FixedValidator(Evidence(provision))).ProvisionAsync(provision))
            .IsSuccess.Should().BeTrue();

        var recipeMismatch = await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "bad-recipe", "sequence-1") with
            {
                RecipeSnapshotHash = new string('C', 64),
            });
        recipeMismatch.IsFailure.Should().BeTrue();
        recipeMismatch.Error.Code.Should().Be("Projection.RecipeSnapshotHashMismatch");

        var programMismatch = await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "bad-program", "sequence-1") with
            {
                ProgramHash = new string('D', 64),
            });
        programMismatch.IsFailure.Should().BeTrue();
        programMismatch.Error.Code.Should().Be("Projection.ProgramHashMismatch");

        var identityMismatch = await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "bad-stream", "another-sequence"));
        identityMismatch.IsFailure.Should().BeTrue();
        identityMismatch.Error.Code.Should().Be("Projection.Authority.IdentityMismatch");
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Provision_rejects_non_pristine_scope_and_duplicate_recipe_execution()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1", "WS-2", "WS-3");

        var released = await database.WorkScopes.ExecuteAsync(
            "WS-1",
            new WorkScopeOperationCommand(
                WorkScopeAction.Release,
                "release-before-authority",
                1,
                ActorId: "operator"));
        released.IsSuccess.Should().BeTrue(
            released.IsFailure ? released.Error.Description : null);
        var nonPristineCommand = ProvisionCommand("WS-1", "rms-non-pristine", "sequence-non-pristine");
        var nonPristine = await database.Authority(
                new FixedValidator(Evidence(nonPristineCommand)))
            .ProvisionAsync(nonPristineCommand);
        nonPristine.IsFailure.Should().BeTrue();
        nonPristine.Error.Code.Should().Be("Projection.Authority.ScopeNotPristine");

        var firstCommand = ProvisionCommand("WS-2", "rms-shared", "sequence-2");
        var secondCommand = ProvisionCommand("WS-3", "rms-shared", "sequence-3");
        var first = database.Authority(new FixedValidator(Evidence(firstCommand)))
            .ProvisionAsync(firstCommand);
        var second = database.Authority(new FixedValidator(Evidence(secondCommand)))
            .ProvisionAsync(secondCommand);
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(static result => result.IsSuccess).Should().Be(1);
        outcomes.Count(static result => result.IsFailure
            && result.Error.Code == "Projection.Authority.EvidenceAlreadyBound").Should().Be(1);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_authority_evidence_for_one_scope_has_one_durable_winner()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1");
        var firstCommand = ProvisionCommand("WS-1", "rms-execution-a", "sequence-a");
        var secondCommand = ProvisionCommand("WS-1", "rms-execution-b", "sequence-b") with
        {
            IdempotencyKey = "authority:WS-1:sequence-b",
        };

        var first = database.Authority(new FixedValidator(Evidence(firstCommand)))
            .ProvisionAsync(firstCommand);
        var second = database.Authority(new FixedValidator(Evidence(secondCommand)))
            .ProvisionAsync(secondCommand);
        var results = await Task.WhenAll(first, second);

        results.Count(static result => result.IsSuccess).Should().Be(1);
        results.Count(static result => result.IsFailure
            && result.Error.Code == "Projection.Authority.EvidenceAlreadyBound").Should().Be(1);
        (await database.ScalarAsync("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Sqlite_direct_insert_with_null_scope_identity_is_fail_closed()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1");
        await database.ExecuteAsync(
            "UPDATE POM_WORK_SCOPE SET EQUIPMENT_ID=NULL, RECIPE_ID=NULL, RECIPE_VERSION=NULL WHERE WORK_SCOPE_ID='WS-1'");

        var insert = async () => await database.ExecuteAsync("""
            INSERT INTO POM_WORK_SCOPE_PROJECTION_AUTHORITY
            (WORK_SCOPE_ID, SOURCE_CLIENT_ID, EQUIPMENT_ID, OPERATION_KEY, PAIR_RUN_ID,
             SEQUENCE_RUN_ID, RECIPE_EXECUTION_ID, RECIPE_ID, RECIPE_VERSION,
             RECIPE_SNAPSHOT_SCHEMA, RECIPE_SNAPSHOT_HASH, PROGRAM_ARTIFACT_ID,
             PROGRAM_SCHEMA, PROGRAM_HASH, BASELINE_VERSION_NO, LAST_APPLIED_VERSION_NO,
             PROVISION_IDEMPOTENCY_KEY, PROVISION_REQUEST_HASH, PROVISIONED_AT, PROVISIONED_BY,
             LAST_APPLIED_AT)
            VALUES
            ('WS-1', 'cleaner-a', 'EQ-1', 'operation-1', 'pair-1',
             'sequence-1', 'rms-1', 'RECIPE-1', 1,
             'recipe-v1', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
             'program-1', 'program-v1',
             'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
             1, 1, 'authority-1',
             'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC',
             '2026-08-31T00:00:00Z', 'tester', NULL);
            """);

        await insert.Should().ThrowAsync<SqliteException>()
            .WithMessage("*exact pristine WorkScope*");
    }

    [Fact]
    public async Task Sqlite_replace_cannot_move_recipe_execution_authority_to_another_scope()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1", "WS-2");
        var provision = ProvisionCommand("WS-1", "rms-shared", "sequence-1");
        (await database.Authority(new FixedValidator(Evidence(provision))).ProvisionAsync(provision))
            .IsSuccess.Should().BeTrue();

        var replace = async () => await database.ExecuteAsync("""
            PRAGMA recursive_triggers=OFF;
            INSERT OR REPLACE INTO POM_WORK_SCOPE_PROJECTION_AUTHORITY
            (WORK_SCOPE_ID, SOURCE_CLIENT_ID, EQUIPMENT_ID, OPERATION_KEY, PAIR_RUN_ID,
             SEQUENCE_RUN_ID, RECIPE_EXECUTION_ID, RECIPE_ID, RECIPE_VERSION,
             RECIPE_SNAPSHOT_SCHEMA, RECIPE_SNAPSHOT_HASH, PROGRAM_ARTIFACT_ID,
             PROGRAM_SCHEMA, PROGRAM_HASH, BASELINE_VERSION_NO, LAST_APPLIED_VERSION_NO,
             PROVISION_IDEMPOTENCY_KEY, PROVISION_REQUEST_HASH, PROVISIONED_AT, PROVISIONED_BY,
             LAST_APPLIED_AT)
            SELECT 'WS-2', SOURCE_CLIENT_ID, EQUIPMENT_ID, OPERATION_KEY, 'pair-2',
                   'sequence-2', RECIPE_EXECUTION_ID, RECIPE_ID, RECIPE_VERSION,
                   RECIPE_SNAPSHOT_SCHEMA, RECIPE_SNAPSHOT_HASH, 'program:WS-2',
                   PROGRAM_SCHEMA, PROGRAM_HASH, 1, 1,
                   'authority:WS-2',
                   'DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD',
                   PROVISIONED_AT, PROVISIONED_BY, NULL
              FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY WHERE WORK_SCOPE_ID='WS-1';
            """);

        await replace.Should().ThrowAsync<SqliteException>()
            .WithMessage("*replacement is forbidden*");
        (await database.TextAsync("""
            SELECT WORK_SCOPE_ID || ':' || RECIPE_EXECUTION_ID
              FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY
            """)).Should().Be("WS-1:rms-shared");
    }

    [Fact]
    public async Task Worker_readiness_fails_closed_for_current_runnable_application_without_authority()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1");
        var provision = ProvisionCommand("WS-1", "rms-execution-1", "sequence-1");
        (await database.Authority(new FixedValidator(Evidence(provision))).ProvisionAsync(provision))
            .IsSuccess.Should().BeTrue();
        (await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "event-1", "sequence-1"))).IsSuccess.Should().BeTrue();

        await database.ExecuteAsync("""
            DROP TRIGGER TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_DELETE_GUARD;
            DELETE FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY WHERE WORK_SCOPE_ID='WS-1';
            """);

        var ready = async () => await database.Store.EnsureReadyAsync();
        await ready.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not have exact V158 projection authority*");
        (await database.TextAsync("""
            SELECT APPLICATION_STATUS FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE SOURCE_CLIENT_ID='cleaner-a' AND EVENT_ID='event-1'
            """)).Should().Be("Pending");
    }

    [Fact]
    public async Task Apply_advances_authority_lineage_once_and_restart_detects_a_version_gap()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1");
        var provision = ProvisionCommand("WS-1", "rms-execution-1", "sequence-1");
        (await database.Authority(new FixedValidator(Evidence(provision))).ProvisionAsync(provision))
            .IsSuccess.Should().BeTrue();
        (await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "event-1", "sequence-1"))).IsSuccess.Should().BeTrue();

        var initialApply = database.Processor(WorkScopeProjectionDecision.Apply(
            "start-cleaning",
            [
                new WorkScopeProjectionEffect(WorkScopeAction.Release),
                new WorkScopeProjectionEffect(WorkScopeAction.Start),
            ]));
        var applied = await initialApply.ProcessNextAsync("worker-before-restart");
        var replay = await initialApply.ProcessNextAsync("worker-before-restart");

        applied!.Kind.Should().Be(WorkScopeProjectionCommitKind.Applied);
        replay.Should().BeNull();
        (await database.TextAsync("""
            SELECT S.STATUS || ':' || S.VERSION_NO || ':' || A.LAST_APPLIED_VERSION_NO
              FROM POM_WORK_SCOPE S
              JOIN POM_WORK_SCOPE_PROJECTION_AUTHORITY A
                ON A.WORK_SCOPE_ID=S.WORK_SCOPE_ID
             WHERE S.WORK_SCOPE_ID='WS-1'
            """)).Should().Be("Started:3:3");

        var regression = async () => await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_AUTHORITY
               SET LAST_APPLIED_VERSION_NO=2
             WHERE WORK_SCOPE_ID='WS-1';
            """);
        await regression.Should().ThrowAsync<SqliteException>()
            .WithMessage("*lineage is monotonic*");

        // Simulate an unsupported out-of-band mutation discovered after process restart. The new
        // worker must quarantine instead of skipping from authority version 3 to WorkScope 4.
        await database.ExecuteAsync(
            "UPDATE POM_WORK_SCOPE SET VERSION_NO=4 WHERE WORK_SCOPE_ID='WS-1';");
        (await database.Projections.IngestAsync(
            "cleaner-a",
            ProjectionCommand("WS-1", "event-2", "sequence-1") with { Revision = 2 }))
            .IsSuccess.Should().BeTrue();
        var restarted = database.Processor(WorkScopeProjectionDecision.Apply(
            "report-after-restart",
            [new WorkScopeProjectionEffect(WorkScopeAction.Report, 1m, 0m)]));
        var quarantined = await restarted.ProcessNextAsync("worker-after-restart");

        quarantined!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        (await database.TextAsync("""
            SELECT LAST_ERROR_CODE FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE SOURCE_CLIENT_ID='cleaner-a' AND EVENT_ID='event-2'
            """)).Should().Be("Projection.LineageGap");
        (await database.TextAsync("""
            SELECT LAST_APPLIED_VERSION_NO || ':' ||
                   (SELECT VERSION_NO FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID='WS-1')
              FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY WHERE WORK_SCOPE_ID='WS-1'
            """)).Should().Be("3:4");
    }

    [Fact]
    public async Task Sqlite_direct_lineage_updates_must_match_scope_and_keep_a_timestamp()
    {
        await using var database = await AuthorityDatabase.CreateAsync("WS-1");
        var provision = ProvisionCommand("WS-1", "rms-execution-1", "sequence-1");
        (await database.Authority(new FixedValidator(Evidence(provision))).ProvisionAsync(provision))
            .IsSuccess.Should().BeTrue();

        var aheadOfScope = async () => await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_AUTHORITY
               SET LAST_APPLIED_VERSION_NO=2,
                   LAST_APPLIED_AT='2026-08-31 01:00:00'
             WHERE WORK_SCOPE_ID='WS-1';
            """);
        await aheadOfScope.Should().ThrowAsync<SqliteException>()
            .WithMessage("*scope-aligned*");

        await database.ExecuteAsync(
            "UPDATE POM_WORK_SCOPE SET VERSION_NO=2 WHERE WORK_SCOPE_ID='WS-1';");
        var missingTimestamp = async () => await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_AUTHORITY
               SET LAST_APPLIED_VERSION_NO=2,
                   LAST_APPLIED_AT=NULL
             WHERE WORK_SCOPE_ID='WS-1';
            """);
        await missingTimestamp.Should().ThrowAsync<SqliteException>()
            .WithMessage("*scope-aligned*");

        await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_AUTHORITY
               SET LAST_APPLIED_VERSION_NO=2,
                   LAST_APPLIED_AT='2026-08-31 01:00:00'
             WHERE WORK_SCOPE_ID='WS-1';
            UPDATE POM_WORK_SCOPE SET VERSION_NO=3 WHERE WORK_SCOPE_ID='WS-1';
            """);
        var timestampRegression = async () => await database.ExecuteAsync("""
            UPDATE POM_WORK_SCOPE_PROJECTION_AUTHORITY
               SET LAST_APPLIED_VERSION_NO=3,
                   LAST_APPLIED_AT='2026-08-31 00:00:00'
             WHERE WORK_SCOPE_ID='WS-1';
            """);
        await timestampRegression.Should().ThrowAsync<SqliteException>()
            .WithMessage("*scope-aligned*");
    }

    [Fact]
    public void Repository_sources_keep_scope_authority_lock_order_and_serializable_fences()
    {
        var workScopeSource = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.POM", "Infrastructure", "WorkScopeRepository.cs"));
        workScopeSource.Should().Contain("IsolationLevel.Serializable")
            .And.Contain("WITH (UPDLOCK, HOLDLOCK)")
            .And.Contain("POM_WORK_SCOPE_PROJECTION_AUTHORITY")
            .And.Contain("WorkScopeWriteResult.ProjectionOwned");

        var authoritySource = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.POM", "Infrastructure",
            "WorkScopeProjectionAuthorityRepository.cs"));
        authoritySource.Should().Contain("IsolationLevel.Serializable")
            .And.Contain("SelectScopeForAuthoritySqlSqlServer")
            .And.Contain("WITH (UPDLOCK, HOLDLOCK)")
            .And.Contain("scope.WorkScopeId, evidence.WorkScopeId, StringComparison.Ordinal")
            .And.Contain("ScopeNotPristine");

        var storeSource = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.POM", "Infrastructure", "WorkScopeProjectionStore.cs"));
        var candidateStart = storeSource.IndexOf(
            "private const string CandidateSqlSqlServer", StringComparison.Ordinal);
        var candidateEnd = storeSource.IndexOf(
            "private const string ClaimSql", candidateStart, StringComparison.Ordinal);
        candidateStart.Should().BeGreaterThanOrEqualTo(0);
        candidateEnd.Should().BeGreaterThan(candidateStart);
        var candidateSql = storeSource[candidateStart..candidateEnd];
        candidateSql.Should().NotContain("PROJECTION_CURRENT")
            .And.NotContain(" JOIN ")
            .And.Contain("APPLICATION A WITH (UPDLOCK, READPAST, ROWLOCK)");
        storeSource.Should().Contain("U.RECIPE_ID COLLATE Latin1_General_100_BIN2")
            .And.Contain("S.RECIPE_ID COLLATE Latin1_General_100_BIN2");

        var migration = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
            "V158__POM_WORK_SCOPE_PROJECTION_AUTHORITY.sql"));
        var columnLines = migration.Split('\n', StringSplitOptions.TrimEntries);
        columnLines.Single(static line => line.StartsWith("WORK_SCOPE_ID ", StringComparison.Ordinal))
            .Should().NotContain("COLLATE",
                "the authority FK must use the V152 parent key database collation");
        columnLines.Single(static line => line.StartsWith("RECIPE_ID ", StringComparison.Ordinal))
            .Should().NotContain("COLLATE",
                "the shared V152/V156 recipe identity stays schema-compatible");
        columnLines.Single(static line => line.StartsWith("PROVISIONED_BY ", StringComparison.Ordinal))
            .Should().Contain("Latin1_General_100_BIN2");
        migration.Should().Contain("UNIQUE INDEX UX_POM_WORK_SCOPE_PROJECTION_AUTHORITY_RECIPE_EXECUTION")
            .And.Contain("I.LAST_APPLIED_VERSION_NO <> S.VERSION_NO")
            .And.Contain("I.LAST_APPLIED_AT IS NULL")
            .And.Contain("S.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2")
            .And.Contain("I.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2")
            .And.Contain("D.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2")
            .And.Contain("D.RECIPE_ID COLLATE Latin1_General_100_BIN2");
    }

    private static WorkScopeProjectionAuthorityProvisionCommand ProvisionCommand(
        string workScopeId,
        string recipeExecutionId,
        string sequenceRunId) => new(
        workScopeId,
        "cleaner-a",
        "EQ-1",
        "operation-1",
        PairRunId(workScopeId),
        sequenceRunId,
        recipeExecutionId,
        $"cleaner-program:{workScopeId}",
        $"projection-authority:{workScopeId}",
        "commissioning");

    private static string PairRunId(string workScopeId) => workScopeId switch
    {
        "WS-1" => "pair-1",
        "WS-2" => "pair-2",
        "WS-3" => "pair-3",
        _ => $"pair:{workScopeId}",
    };

    private static WorkScopeProjectionAuthorityEvidence Evidence(
        WorkScopeProjectionAuthorityProvisionCommand command) => new(
        command.WorkScopeId,
        command.SourceClientId,
        command.EquipmentId,
        command.OperationKey,
        command.PairRunId,
        command.SequenceRunId,
        command.RecipeExecutionId,
        "RECIPE-1",
        1,
        "cleaner-recipe-v1",
        new string('A', 64),
        command.ProgramArtifactId,
        "cleaner-program-v1",
        new string('B', 64));

    private static WorkScopeProjectionCommand ProjectionCommand(
        string workScopeId,
        string eventId,
        string sequenceRunId) => new(
        "cleaner-a",
        eventId,
        workScopeId,
        "EQ-1",
        "operation-1",
        "pair-1",
        sequenceRunId,
        WorkScopeProjectionStatus.Running,
        false,
        "RECIPE-1",
        new string('A', 64),
        new string('B', 64),
        [
            new WorkScopeProjectionCarrierDto("front", "CARRIER-F", "RUN-F"),
            new WorkScopeProjectionCarrierDto("rear", "CARRIER-R", "RUN-R"),
        ],
        DateTimeOffset.UtcNow,
        1,
        "RUNNING");

    private sealed class FixedValidator(WorkScopeProjectionAuthorityEvidence evidence)
        : IWorkScopeProjectionAuthorityValidator
    {
        public Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => Task.FromResult(Result.Success(evidence));
    }

    private sealed class FixedPolicy(WorkScopeProjectionDecision decision)
        : IWorkScopeProjectionPolicy
    {
        public WorkScopeProjectionPolicyIdentity Identity { get; } =
            new("authority-lineage-test", "1");

        public WorkScopeProjectionDecision Decide(WorkScopeProjectionContext context) => decision;
    }

    private sealed class AuthorityDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly string _connectionString;
        private readonly EesDataSource _dataSource;

        private AuthorityDatabase(string path, string connectionString, EesDataSource dataSource)
        {
            _path = path;
            _connectionString = connectionString;
            _dataSource = dataSource;
            Store = new WorkScopeProjectionStore(dataSource);
            WorkScopes = new WorkScopeBridge(new WorkScopeService(new WorkScopeRepository(dataSource)));
            Projections = new WorkScopeProjectionBridge(
                new WorkScopeProjectionService(new WorkScopeProjectionRepository(dataSource)));
        }

        public IWorkScopeBridge WorkScopes { get; }
        public IWorkScopeProjectionBridge Projections { get; }
        public WorkScopeProjectionStore Store { get; }

        public WorkScopeProjectionProcessor Processor(WorkScopeProjectionDecision decision) =>
            new(Store, new FixedPolicy(decision), TimeSpan.FromMinutes(2));

        public static async Task<AuthorityDatabase> CreateAsync(params string[] workScopeIds)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"nexa-projection-authority-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path};Cache=Shared;Default Timeout=30";
            SqliteSchemaInitializer.Apply(
                connectionString,
                [new PomWorkScopeProjectionSqliteSchemaContribution()]);
            var dataSource = new EesDataSource
            {
                Provider = new SqliteProvider(),
                ConnectionString = connectionString,
            };
            foreach (var workScopeId in workScopeIds)
            {
                var scope = PomWorkScope.Create(
                    workScopeId,
                    "PLANT-1",
                    PomWorkScopeType.Other,
                    PairRunId(workScopeId),
                    "Cleaner",
                    null,
                    "EQ-1",
                    null,
                    "CLEANING",
                    "RECIPE-1",
                    1,
                    1m,
                    null,
                    null,
                    "tester");
                scope.IsSuccess.Should().BeTrue();
                scope.Value.SetCreateIdentity($"create:{workScopeId}", new string('C', 64));
                await new WorkScopeRepository(dataSource).AddAsync(scope.Value);
            }
            return new AuthorityDatabase(path, connectionString, dataSource);
        }

        public IWorkScopeProjectionAuthorityBridge Authority(
            IWorkScopeProjectionAuthorityValidator validator) =>
            new WorkScopeProjectionAuthorityBridge(
                new WorkScopeProjectionAuthorityService(
                    new WorkScopeProjectionAuthorityRepository(_dataSource),
                    validator));

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
            try { File.Delete(_path); }
            catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
