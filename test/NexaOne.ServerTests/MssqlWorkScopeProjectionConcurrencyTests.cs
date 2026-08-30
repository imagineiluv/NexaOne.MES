using System.Collections.Concurrent;
using FluentAssertions;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Infrastructure;
using NexaOne.ServiceContracts.Pom;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>
/// Exercises the durable projection fences on the real SQL Server provider. A missing local
/// connection remains a soft skip, while the dedicated MSSQL gate makes it mandatory through
/// <see cref="MssqlContractDatabase.RequiredEnvironmentVariable"/>.
/// </summary>
[Trait("Category", "MssqlContract")]
public sealed class MssqlWorkScopeProjectionConcurrencyTests
{
    private static readonly TimeSpan ConcurrentOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private readonly ITestOutputHelper _output;

    public MssqlWorkScopeProjectionConcurrencyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Enabled_worker_readiness_preflight_accepts_the_mssql_v158_contract()
    {
        var harness = await ProjectionHarness.TryCreateAsync(_output);
        if (harness is null)
            return;

        await harness.Store.EnsureReadyAsync();
    }

    [Fact]
    public async Task Authority_provision_rejects_a_case_variant_of_the_persisted_work_scope_id()
    {
        var harness = await ProjectionHarness.TryCreateAsync(_output);
        if (harness is null)
            return;

        var ids = await harness.CreateScopeAsync(provisionAuthority: false);
        var caseVariant = ids with { WorkScopeId = ids.WorkScopeId.ToLowerInvariant() };

        var provisioned = await harness.ProvisionAuthorityAsync(caseVariant);

        provisioned.IsFailure.Should().BeTrue();
        provisioned.Error.Code.Should().Be("Projection.Authority.ScopeIdentityMismatch");
        (await harness.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY WHERE WORK_SCOPE_ID=@workScopeId;",
            new { workScopeId = ids.WorkScopeId })).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_claim_and_new_ingestion_finish_without_deadlock_and_only_current_event_applies()
    {
        var harness = await ProjectionHarness.TryCreateAsync(_output);
        if (harness is null)
            return;

        var ids = await harness.CreateScopeAsync();
        var oldEvent = Command(ids, $"old-{ids.Suffix}", revision: 1);
        var newEvent = Command(ids, $"new-{ids.Suffix}", revision: 2);
        var accepted = await harness.Projections.IngestAsync(ids.SourceClientId, oldEvent);
        accepted.IsSuccess.Should().BeTrue(
            accepted.IsFailure ? accepted.Error.Description : string.Empty);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimTask = AfterAsync(
            start.Task,
            () => harness.Store.TryClaimNextAsync("mssql-concurrent-worker", LeaseDuration));
        var ingestTask = AfterAsync(
            start.Task,
            () => harness.Projections.IngestAsync(ids.SourceClientId, newEvent));

        start.SetResult();
        await Task.WhenAll((Task)claimTask, ingestTask).WaitAsync(ConcurrentOperationTimeout);

        var concurrentClaim = await claimTask;
        var newerReceipt = await ingestTask;
        newerReceipt.IsSuccess.Should().BeTrue(
            newerReceipt.IsFailure ? newerReceipt.Error.Description : string.Empty);
        newerReceipt.Value.IsCurrent.Should().BeTrue();

        WorkScopeProjectionClaim? currentClaim = null;
        if (concurrentClaim is not null)
        {
            concurrentClaim.Event.EventId.Should().BeOneOf(oldEvent.EventId, newEvent.EventId);
            if (concurrentClaim.Event.EventId == oldEvent.EventId)
            {
                var staleCommit = await harness.Store.CommitDecisionAsync(
                    concurrentClaim,
                    Prepared(
                        new WorkScopeProjectionPolicyIdentity("mssql-old-event", "1"),
                        concurrentClaim.Event,
                        WorkScopeProjectionDecision.Apply(
                            "mssql.old.release",
                            [new WorkScopeProjectionEffect(WorkScopeAction.Release)])));
                staleCommit.Kind.Should().Be(WorkScopeProjectionCommitKind.LeaseLost,
                    "ingestion must fence the in-flight event when the newer event becomes current");
            }
            else
            {
                currentClaim = concurrentClaim;
            }
        }

        currentClaim ??= await harness.Store.TryClaimNextAsync(
            "mssql-current-worker", LeaseDuration);
        currentClaim.Should().NotBeNull();
        currentClaim!.Event.EventId.Should().Be(newEvent.EventId);

        var applied = await harness.Store.CommitDecisionAsync(
            currentClaim,
            Prepared(
                new WorkScopeProjectionPolicyIdentity("mssql-current-event", "1"),
                currentClaim.Event,
                WorkScopeProjectionDecision.Apply(
                    "mssql.current.release",
                    [new WorkScopeProjectionEffect(WorkScopeAction.Release)])));

        applied.Kind.Should().Be(
            WorkScopeProjectionCommitKind.Applied,
            $"the current event should apply after the WorkScope creation time; detail={applied.Detail}");
        (await harness.Database.ScalarAsync<string>(
            """
            SELECT APPLICATION_STATUS
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE SOURCE_CLIENT_ID=@sourceClientId AND EVENT_ID=@eventId;
            """,
            new { sourceClientId = ids.SourceClientId, eventId = oldEvent.EventId }))
            .Should().Be("Superseded");
        (await harness.Database.ScalarAsync<string>(
            """
            SELECT APPLICATION_STATUS
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE SOURCE_CLIENT_ID=@sourceClientId AND EVENT_ID=@eventId;
            """,
            new { sourceClientId = ids.SourceClientId, eventId = newEvent.EventId }))
            .Should().Be("Applied");

        var scopes = await harness.WorkScopes.ListAsync(targetId: ids.PairRunId);
        scopes.IsSuccess.Should().BeTrue();
        scopes.Value.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Status = "Released",
            VersionNo = 2,
        });
        var executions = await harness.WorkScopes.ListExecutionsAsync(ids.WorkScopeId);
        executions.IsSuccess.Should().BeTrue();
        executions.Value.Should().ContainSingle().Which.Action.Should().Be("Release");
    }

    [Fact]
    public async Task Concurrent_authority_provisions_bind_one_work_scope_exactly_once()
    {
        var harness = await ProjectionHarness.TryCreateAsync(_output);
        if (harness is null)
            return;

        var ids = await harness.CreateScopeAsync(provisionAuthority: false);
        var firstIds = ids with { SequenceRunId = $"{ids.SequenceRunId}-A" };
        var secondIds = ids with { SequenceRunId = $"{ids.SequenceRunId}-B" };
        var firstCommand = Command(firstIds, $"bind-a-{ids.Suffix}", revision: 1);
        var secondCommand = Command(secondIds, $"bind-b-{ids.Suffix}", revision: 1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = AfterAsync(
            start.Task,
            () => harness.ProvisionAuthorityAsync(firstIds));
        var second = AfterAsync(
            start.Task,
            () => harness.ProvisionAuthorityAsync(secondIds));

        start.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(ConcurrentOperationTimeout);

        results.Count(static result => result.IsSuccess).Should().Be(1);
        results.Count(static result =>
                result.IsFailure && result.Error.Code == "Projection.Authority.EvidenceAlreadyBound")
            .Should().Be(1);
        var winner = results[0].IsSuccess ? firstIds : secondIds;
        var loser = results[0].IsSuccess ? secondIds : firstIds;
        var winnerCommand = results[0].IsSuccess ? firstCommand : secondCommand;
        var loserCommand = results[0].IsSuccess ? secondCommand : firstCommand;
        var winnerReceipt = await harness.Projections.IngestAsync(
            winner.SourceClientId,
            winnerCommand);
        var loserReceipt = await harness.Projections.IngestAsync(
            loser.SourceClientId,
            loserCommand);
        winnerReceipt.IsSuccess.Should().BeTrue(
            winnerReceipt.IsFailure ? winnerReceipt.Error.Description : string.Empty);
        loserReceipt.IsFailure.Should().BeTrue();
        loserReceipt.Error.Code.Should().Be("Projection.Authority.IdentityMismatch");
        (await harness.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY WHERE WORK_SCOPE_ID=@workScopeId;",
            new { workScopeId = ids.WorkScopeId })).Should().Be(1);
        (await harness.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CURRENT
             WHERE WORK_SCOPE_ID=@workScopeId;
            """,
            new { workScopeId = ids.WorkScopeId })).Should().Be(1);
        (await harness.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_INBOX
             WHERE WORK_SCOPE_ID=@workScopeId;
            """,
            new { workScopeId = ids.WorkScopeId })).Should().Be(1);
        (await harness.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE WORK_SCOPE_ID=@workScopeId;
            """,
            new { workScopeId = ids.WorkScopeId })).Should().Be(1);
        (await harness.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
              FROM POM_WORK_SCOPE_PROJECTION_CARRIER C
              JOIN POM_WORK_SCOPE_PROJECTION_INBOX E
                ON E.SOURCE_CLIENT_ID=C.SOURCE_CLIENT_ID AND E.EVENT_ID=C.EVENT_ID
             WHERE E.WORK_SCOPE_ID=@workScopeId;
            """,
            new { workScopeId = ids.WorkScopeId })).Should().Be(2);
    }

    [Fact]
    public async Task Projection_authority_fences_an_ordinary_command_during_policy_evaluation()
    {
        var harness = await ProjectionHarness.TryCreateAsync(_output);
        if (harness is null)
            return;

        var ids = await harness.CreateScopeAsync();
        var command = Command(ids, $"stale-{ids.Suffix}", revision: 1);
        var accepted = await harness.Projections.IngestAsync(ids.SourceClientId, command);
        accepted.IsSuccess.Should().BeTrue(
            accepted.IsFailure ? accepted.Error.Description : string.Empty);

        var policy = new RecordingObservePolicy();
        var barrierStore = new FirstCommitBarrierStore(harness.Store);
        var processor = new WorkScopeProjectionProcessor(barrierStore, policy, LeaseDuration);
        var processing = processor.ProcessNextAsync("mssql-stale-observer");

        await barrierStore.FirstCommitReached.WaitAsync(ConcurrentOperationTimeout);
        try
        {
            var held = await harness.WorkScopes.ExecuteAsync(
                ids.WorkScopeId,
                new WorkScopeOperationCommand(
                    WorkScopeAction.Hold,
                    $"hold:{ids.Suffix}",
                    ExpectedVersion: 1,
                    ActorId: "mssql-contract"));
            held.IsFailure.Should().BeTrue();
            held.Error.Code.Should().Be("POM.WorkScope.ProjectionOwned");
        }
        finally
        {
            barrierStore.ReleaseFirstCommit();
        }

        var staleResult = await processing.WaitAsync(ConcurrentOperationTimeout);
        staleResult.Should().NotBeNull();
        staleResult!.Kind.Should().Be(WorkScopeProjectionCommitKind.Observed);
        (await harness.Database.ScalarAsync<string>(
            """
            SELECT APPLICATION_STATUS
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE SOURCE_CLIENT_ID=@sourceClientId AND EVENT_ID=@eventId;
            """,
            new { sourceClientId = ids.SourceClientId, eventId = command.EventId }))
            .Should().Be("Observed",
                "the rejected ordinary command must not invalidate the projection decision");
        policy.Snapshots.Should().Equal(new PolicySnapshot(1, false));
        (await harness.Database.ScalarAsync<string>(
            """
            SELECT APPLICATION_STATUS + ':'
                   + CONVERT(varchar(20), ATTEMPT_COUNT) + ':'
                   + CONVERT(varchar(20), LEASE_FENCE)
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE SOURCE_CLIENT_ID=@sourceClientId AND EVENT_ID=@eventId;
            """,
            new { sourceClientId = ids.SourceClientId, eventId = command.EventId }))
            .Should().Be("Observed:1:1");
        (await harness.Database.ScalarAsync<string>(
            """
            SELECT STATUS + ':' + IS_HOLD + ':' + CONVERT(varchar(20), VERSION_NO)
              FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID=@workScopeId;
            """,
            new { workScopeId = ids.WorkScopeId })).Should().Be("Created:N:1");
    }

    [Fact]
    public async Task Lease_rollover_fences_the_expired_owner_commit()
    {
        var harness = await ProjectionHarness.TryCreateAsync(_output);
        if (harness is null)
            return;

        var ids = await harness.CreateScopeAsync();
        var command = Command(ids, $"lease-{ids.Suffix}", revision: 1);
        var accepted = await harness.Projections.IngestAsync(ids.SourceClientId, command);
        accepted.IsSuccess.Should().BeTrue(
            accepted.IsFailure ? accepted.Error.Description : string.Empty);

        var expiredClaim = await harness.Store.TryClaimNextAsync("mssql-expired-owner", LeaseDuration);
        expiredClaim.Should().NotBeNull();
        expiredClaim!.Event.EventId.Should().Be(command.EventId);

        await harness.Database.ExecuteAsync(
            """
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET LEASE_EXPIRES_AT=DATEADD(second, -1, SYSUTCDATETIME())
             WHERE SOURCE_CLIENT_ID=@sourceClientId AND EVENT_ID=@eventId
               AND APPLICATION_STATUS='Processing' AND LEASE_OWNER=@leaseOwner;
            """,
            new
            {
                sourceClientId = ids.SourceClientId,
                eventId = command.EventId,
                leaseOwner = expiredClaim.LeaseOwner,
            });

        var replacementClaim = await harness.Store.TryClaimNextAsync(
            "mssql-replacement-owner", LeaseDuration);
        replacementClaim.Should().NotBeNull();
        replacementClaim!.AttemptCount.Should().Be(2);
        replacementClaim.LeaseFence.Should().Be(expiredClaim.LeaseFence + 1);

        var expiredCommit = await harness.Store.CommitDecisionAsync(
            expiredClaim,
            Prepared(
                new WorkScopeProjectionPolicyIdentity("mssql-expired-policy", "1"),
                expiredClaim.Event,
                WorkScopeProjectionDecision.Observe("mssql.expired.observe")));
        var replacementCommit = await harness.Store.CommitDecisionAsync(
            replacementClaim,
            Prepared(
                new WorkScopeProjectionPolicyIdentity("mssql-replacement-policy", "1"),
                replacementClaim.Event,
                WorkScopeProjectionDecision.Observe("mssql.replacement.observe")));

        expiredCommit.Kind.Should().Be(WorkScopeProjectionCommitKind.LeaseLost);
        replacementCommit.Kind.Should().Be(WorkScopeProjectionCommitKind.Observed);
        (await harness.Database.ScalarAsync<string>(
            """
            SELECT APPLICATION_STATUS + ':'
                   + CONVERT(varchar(20), ATTEMPT_COUNT) + ':'
                   + CONVERT(varchar(20), LEASE_FENCE) + ':'
                   + COALESCE(POLICY_ID, '')
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE SOURCE_CLIENT_ID=@sourceClientId AND EVENT_ID=@eventId;
            """,
            new { sourceClientId = ids.SourceClientId, eventId = command.EventId }))
            .Should().Be("Observed:2:2:mssql-replacement-policy");
    }

    private static PreparedWorkScopeProjectionDecision Prepared(
        WorkScopeProjectionPolicyIdentity identity,
        WorkScopeProjectionEventDto evidence,
        WorkScopeProjectionDecision decision) =>
        ProjectionDecisionCodec.Prepare(identity, evidence, decision);

    private static async Task<T> AfterAsync<T>(Task start, Func<Task<T>> action)
    {
        await start.ConfigureAwait(false);
        return await action().ConfigureAwait(false);
    }

    private static WorkScopeProjectionCommand Command(
        ProjectionIds ids,
        string eventId,
        long revision) => new(
        ids.SourceClientId,
        eventId,
        ids.WorkScopeId,
        ids.EquipmentId,
        $"operation-{ids.Suffix}",
        ids.PairRunId,
        ids.SequenceRunId,
        WorkScopeProjectionStatus.Running,
        TerminalCleanupCompleted: false,
        "RECIPE-CONTRACT",
        new string('A', 64),
        new string('B', 64),
        [
            new WorkScopeProjectionCarrierDto("front", $"CF-{ids.Suffix}", $"RF-{ids.Suffix}"),
            new WorkScopeProjectionCarrierDto("rear", $"CR-{ids.Suffix}", $"RR-{ids.Suffix}"),
        ],
        // This suite expects the Apply path to succeed. Keep event time safely after the
        // just-created WorkScope while remaining well inside the five-minute future-skew fence.
        DateTimeOffset.UtcNow.AddSeconds(1).AddMilliseconds(revision),
        revision,
        "RUNNING");

    private sealed record ProjectionIds(
        string Suffix,
        string SourceClientId,
        string WorkScopeId,
        string EquipmentId,
        string PairRunId,
        string SequenceRunId);

    private sealed class ProjectionHarness
    {
        private ProjectionHarness(MssqlContractDatabase database)
        {
            Database = database;
            var workScopes = new WorkScopeRepository(database.DataSource);
            WorkScopes = new WorkScopeBridge(new WorkScopeService(workScopes));
            Projections = new WorkScopeProjectionBridge(
                new WorkScopeProjectionService(
                    new WorkScopeProjectionRepository(database.DataSource)));
            Store = new WorkScopeProjectionStore(database.DataSource);
        }

        public MssqlContractDatabase Database { get; }
        public IWorkScopeBridge WorkScopes { get; }
        public IWorkScopeProjectionBridge Projections { get; }
        public WorkScopeProjectionStore Store { get; }

        public static async Task<ProjectionHarness?> TryCreateAsync(ITestOutputHelper output)
        {
            var database = await MssqlContractDatabase.TryCreateAsync(output);
            if (database is null)
                return null;

            // The contract database is shared by all methods in this class. Some concurrency
            // cases deliberately leave a Pending application, so fence prior test-owned queue
            // rows before constructing the next global worker claim.
            await database.ExecuteAsync(
                """
                UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
                   SET APPLICATION_STATUS='Quarantined',
                       NEXT_ATTEMPT_AT=NULL,
                       LEASE_OWNER=NULL,
                       LEASE_FENCE=LEASE_FENCE+1,
                       LEASE_EXPIRES_AT=NULL,
                       LAST_ERROR_CODE='Test.IsolationCleanup',
                       LAST_ERROR_MESSAGE='Fenced by the next MSSQL projection contract test.',
                       COMPLETED_AT=SYSUTCDATETIME(),
                       UPDATED_BY='MSSQL-CONTRACT',
                       UPDATED_AT=SYSUTCDATETIME()
                 WHERE SOURCE_CLIENT_ID LIKE 'mssql-projection-%'
                   AND APPLICATION_STATUS IN ('Pending', 'Retry', 'Processing');
                """);
            return new ProjectionHarness(database);
        }

        public async Task<ProjectionIds> CreateScopeAsync(bool provisionAuthority = true)
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var ids = new ProjectionIds(
                suffix,
                $"mssql-projection-{suffix}",
                $"WS-PX-{suffix}",
                $"EQ-PX-{suffix}",
                $"PAIR-PX-{suffix}",
                $"SEQ-PX-{suffix}");
            var created = await WorkScopes.CreateAsync(new WorkScopeCreateCommand(
                ids.WorkScopeId,
                $"PLANT-{suffix}",
                WorkScopeType.Other,
                ids.PairRunId,
                "MSSQL projection concurrency contract",
                EquipmentId: ids.EquipmentId,
                RecipeId: "RECIPE-CONTRACT",
                RecipeVersion: 1,
                PlanQty: 2m,
                ActorId: "mssql-contract",
                IdempotencyKey: $"create:{ids.WorkScopeId}"));
            created.IsSuccess.Should().BeTrue(
                created.IsFailure ? created.Error.Description : string.Empty);
            created.Value.Should().BeEquivalentTo(new
            {
                ScopeType = "Other",
                TargetId = ids.PairRunId,
                EquipmentId = ids.EquipmentId,
                PlanQty = (decimal?)2m,
                VersionNo = 1,
            });
            if (provisionAuthority)
            {
                var provisioned = await ProvisionAuthorityAsync(ids);
                provisioned.IsSuccess.Should().BeTrue(
                    provisioned.IsFailure ? provisioned.Error.Description : string.Empty);
            }
            return ids;
        }

        public Task<NexaOne.Common.Result<WorkScopeProjectionAuthorityDto>> ProvisionAuthorityAsync(
            ProjectionIds ids)
        {
            var command = new WorkScopeProjectionAuthorityProvisionCommand(
                ids.WorkScopeId,
                ids.SourceClientId,
                ids.EquipmentId,
                $"operation-{ids.Suffix}",
                ids.PairRunId,
                ids.SequenceRunId,
                $"rms-execution-{ids.Suffix}-{ids.SequenceRunId}",
                $"program-{ids.Suffix}",
                $"authority-{ids.Suffix}-{ids.SequenceRunId}",
                "mssql-contract");
            var evidence = new WorkScopeProjectionAuthorityEvidence(
                command.WorkScopeId,
                command.SourceClientId,
                command.EquipmentId,
                command.OperationKey,
                command.PairRunId,
                command.SequenceRunId,
                command.RecipeExecutionId,
                "RECIPE-CONTRACT",
                1,
                "mssql-contract-recipe-v1",
                new string('A', 64),
                command.ProgramArtifactId,
                "mssql-contract-program-v1",
                new string('B', 64));
            IWorkScopeProjectionAuthorityBridge bridge = new WorkScopeProjectionAuthorityBridge(
                new WorkScopeProjectionAuthorityService(
                    new WorkScopeProjectionAuthorityRepository(Database.DataSource),
                    new FixedAuthorityValidator(evidence)));
            return bridge.ProvisionAsync(command);
        }

        private sealed class FixedAuthorityValidator(WorkScopeProjectionAuthorityEvidence evidence)
            : IWorkScopeProjectionAuthorityValidator
        {
            public Task<NexaOne.Common.Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
                WorkScopeProjectionAuthorityProvisionCommand command,
                CancellationToken ct = default) => Task.FromResult(
                    NexaOne.Common.Result.Success(evidence));
        }
    }

    private sealed class RecordingObservePolicy : IWorkScopeProjectionPolicy
    {
        public WorkScopeProjectionPolicyIdentity Identity { get; } =
            new("mssql-recording-observe", "1");

        public ConcurrentQueue<PolicySnapshot> Snapshots { get; } = new();

        public WorkScopeProjectionDecision Decide(WorkScopeProjectionContext context)
        {
            Snapshots.Enqueue(new PolicySnapshot(
                context.WorkScope.VersionNo,
                context.WorkScope.IsHold));
            return WorkScopeProjectionDecision.Observe("mssql.observe");
        }
    }

    private sealed record PolicySnapshot(int VersionNo, bool IsHold);

    /// <summary>
    /// A synchronization probe around the real store. It does not emulate persistence; it only
    /// opens the exact window after policy evaluation and before the first durable commit.
    /// </summary>
    private sealed class FirstCommitBarrierStore(IWorkScopeProjectionStore inner)
        : IWorkScopeProjectionStore
    {
        private readonly TaskCompletionSource _firstCommitReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstCommit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _commitCount;

        public Task FirstCommitReached => _firstCommitReached.Task;

        public void ReleaseFirstCommit() => _releaseFirstCommit.TrySetResult();

        public Task EnsureReadyAsync(CancellationToken ct = default) =>
            inner.EnsureReadyAsync(ct);

        public Task<WorkScopeProjectionClaim?> TryClaimNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            CancellationToken ct = default) =>
            inner.TryClaimNextAsync(leaseOwner, leaseDuration, ct);

        public async Task<WorkScopeProjectionCommitResult> CommitDecisionAsync(
            WorkScopeProjectionClaim claim,
            PreparedWorkScopeProjectionDecision decision,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _commitCount) == 1)
            {
                _firstCommitReached.TrySetResult();
                await _releaseFirstCommit.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return await inner.CommitDecisionAsync(claim, decision, ct).ConfigureAwait(false);
        }

        public Task<WorkScopeProjectionCommitResult> RecordFailureAsync(
            WorkScopeProjectionClaim claim,
            WorkScopeProjectionPolicyIdentity policy,
            string errorCode,
            string errorMessage,
            bool quarantine,
            TimeSpan retryAfter,
            CancellationToken ct = default) =>
            inner.RecordFailureAsync(
                claim,
                policy,
                errorCode,
                errorMessage,
                quarantine,
                retryAfter,
                ct);
    }
}
