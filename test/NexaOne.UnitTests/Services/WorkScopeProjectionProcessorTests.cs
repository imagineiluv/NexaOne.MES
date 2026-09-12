using FluentAssertions;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.UnitTests.Services;

public sealed class WorkScopeProjectionProcessorTests
{
    [Theory]
    [InlineData("null", "null")]
    [InlineData("{}", "{}")]
    [InlineData("[]", "[]")]
    [InlineData("[3,2,1]", "[3,2,1]")]
    [InlineData("[true,false,null,-0,1.00,1e+2,9007199254740993]", "[true,false,null,-0,1.00,1e+2,9007199254740993]")]
    [InlineData(" { \"z\" : [ { \"b\":2,\"a\":1 } ], \"a\":{} } ", "{\"a\":{},\"z\":[{\"a\":1,\"b\":2}]}")]
    [InlineData("{\"text\":\"한글<&😀\"}", """{"text":"\uD55C\uAE00\u003C\u0026\uD83D\uDE00"}""")]
    [InlineData("""{"text":"\uD55C\uAE00\u003C\u0026\uD83D\uDE00"}""", """{"text":"\uD55C\uAE00\u003C\u0026\uD83D\uDE00"}""")]
    [InlineData("{\"a\":1,\"A\":2}", "{\"A\":2,\"a\":1}")]
    public void Metadata_corpus_preserves_envelope_bytes_and_hash_for_both_fields(string input, string canonical)
    {
        var prepared = ProjectionDecisionCodec.Prepare(
            new WorkScopeProjectionPolicyIdentity("policy", "1"), Claim(true).Event,
            WorkScopeProjectionDecision.Apply("Corpus",
                [new WorkScopeProjectionEffect(WorkScopeAction.Report, resultMetadataJson: input)],
                auditMetadataJson: input));
        var expected = """{"policyId":"policy","policyRevision":"1","disposition":"Apply","reasonCode":"Corpus","effects":[{"action":"Report","goodQty":null,"defectQty":null,"carrierId":null,"resultCode":null,"resultMetadata":REPLACE,"remark":null}],"retryAfterMilliseconds":null,"auditMetadata":REPLACE}"""
            .Replace("REPLACE", canonical, StringComparison.Ordinal);
        prepared.DecisionJson.Should().Be(expected);
        prepared.DecisionHash.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(expected))));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}", false)]
    [InlineData("{\"a\":1,\"a\":2}", true)]
    [InlineData("""{"nested":[{"a":1,"\u0061":2}]}""", false)]
    [InlineData("""{"nested":[{"a":1,"\u0061":2}]}""", true)]
    [InlineData("""{"text":"\uD800"}""", false)]
    [InlineData("""{"text":"\uD800"}""", true)]
    [InlineData("""{"\uD800":1}""", false)]
    [InlineData("""{"\uD800":1}""", true)]
    [InlineData("{", false)]
    [InlineData("{", true)]
    public async Task Ambiguous_or_invalid_policy_metadata_is_quarantined_without_commit(string json, bool resultMetadata)
    {
        var store = new StubStore(Claim(true));
        var decision = resultMetadata
            ? WorkScopeProjectionDecision.Apply("Invalid", [new WorkScopeProjectionEffect(WorkScopeAction.Report, resultMetadataJson: json)])
            : WorkScopeProjectionDecision.Observe("Invalid", json);
        var result = await new WorkScopeProjectionProcessor(store, new StubPolicy(decision)).ProcessNextAsync("worker");
        result!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        store.Committed.Should().BeNull();
        store.Failure!.Value.ErrorCode.Should().Be(resultMetadata
            ? "Projection.InvalidResultMetadata" : "Projection.InvalidAuditMetadata");
    }

    [Fact]
    public async Task Valid_decision_is_canonicalized_and_committed_under_the_claim()
    {
        var store = new StubStore(Claim(terminalCleanupCompleted: true));
        var policy = new StubPolicy(WorkScopeProjectionDecision.Apply(
            "Completed",
            [new WorkScopeProjectionEffect(
                WorkScopeAction.Complete,
                goodQty: 1m,
                defectQty: 0m,
                carrierId: "CARRIER-F",
                resultCode: "CLEANED",
                resultMetadataJson: "{\"z\":2,\"a\":1}")]));
        var processor = new WorkScopeProjectionProcessor(store, policy);

        var result = await processor.ProcessNextAsync("worker-1");

        result!.Kind.Should().Be(WorkScopeProjectionCommitKind.Applied);
        store.Committed.Should().NotBeNull();
        // This is a persisted wire-format fixture. A shared canonicalizer may sort metadata,
        // but must not reorder the envelope, omit nulls, or change existing decision hashes.
        store.Committed!.DecisionJson.Should().Be(
            """{"policyId":"test-policy","policyRevision":"1","disposition":"Apply","reasonCode":"Completed","effects":[{"action":"Complete","goodQty":1,"defectQty":0,"carrierId":"CARRIER-F","resultCode":"CLEANED","resultMetadata":{"a":1,"z":2},"remark":null}],"retryAfterMilliseconds":null,"auditMetadata":null}""");
        store.Committed.DecisionHash.Should().Be(
            "E5F3C8D85B3F101D595862A54D859F8D142374879B7AEFB51AC6C0383C7B9391");
        policy.LastContext!.Event.EventId.Should().Be("event-1");
        policy.LastContext.WorkScope.VersionNo.Should().Be(1);
    }

    [Fact]
    public async Task Complete_before_terminal_cleanup_is_quarantined_without_commit_attempt()
    {
        var store = new StubStore(Claim(terminalCleanupCompleted: false));
        var policy = new StubPolicy(WorkScopeProjectionDecision.Apply(
            "TooEarly",
            [new WorkScopeProjectionEffect(
                WorkScopeAction.Complete, 1m, 0m, "CARRIER-F")]));
        var processor = new WorkScopeProjectionProcessor(store, policy);

        var result = await processor.ProcessNextAsync("worker-1");

        result!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        store.Committed.Should().BeNull();
        store.Failure.Should().NotBeNull();
        store.Failure!.Value.ErrorCode.Should().Be("Projection.TerminalCleanupRequired");
        store.Failure.Value.Quarantine.Should().BeTrue();
    }

    [Fact]
    public async Task Carrier_effect_not_present_in_evidence_is_quarantined()
    {
        var store = new StubStore(Claim(terminalCleanupCompleted: true));
        var policy = new StubPolicy(WorkScopeProjectionDecision.Apply(
            "WrongCarrier",
            [new WorkScopeProjectionEffect(WorkScopeAction.Report, 1m, 0m, "OTHER")]));
        var processor = new WorkScopeProjectionProcessor(store, policy);

        var result = await processor.ProcessNextAsync("worker-1");

        result!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        store.Failure!.Value.ErrorCode.Should().Be("Projection.CarrierEvidenceMismatch");
    }

    [Fact]
    public async Task Evidence_beyond_the_accepted_future_clock_skew_is_quarantined()
    {
        var original = Claim(terminalCleanupCompleted: false);
        var store = new StubStore(original with
        {
            Event = original.Event with
            {
                OccurredAt = original.Event.AcceptedAt.AddMinutes(5).AddTicks(1),
            },
        });
        var processor = new WorkScopeProjectionProcessor(
            store,
            new StubPolicy(WorkScopeProjectionDecision.Observe("FutureEvidence")));

        var result = await processor.ProcessNextAsync("worker-1");

        result!.Kind.Should().Be(WorkScopeProjectionCommitKind.Quarantined);
        store.Committed.Should().BeNull();
        store.Failure!.Value.ErrorCode.Should().Be("Projection.OccurredAtFutureSkew");
        store.Failure.Value.Quarantine.Should().BeTrue();
    }

    [Fact]
    public async Task Policy_exception_preserves_the_work_item_for_bounded_retry()
    {
        var store = new StubStore(Claim(terminalCleanupCompleted: false));
        var processor = new WorkScopeProjectionProcessor(
            store,
            new StubPolicy(new InvalidOperationException("transient policy dependency")));

        var result = await processor.ProcessNextAsync("worker-1");

        result!.Kind.Should().Be(WorkScopeProjectionCommitKind.RetryScheduled);
        store.Failure.Should().NotBeNull();
        store.Failure!.Value.ErrorCode.Should().Be("Projection.PolicyException");
        store.Failure.Value.Quarantine.Should().BeFalse();
        store.Failure.Value.RetryAfter.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Worker_stops_a_batch_at_the_first_empty_claim()
    {
        var store = new StubStore(
            Claim(terminalCleanupCompleted: false),
            Claim(terminalCleanupCompleted: false) with
            {
                Event = Claim(terminalCleanupCompleted: false).Event with { EventId = "event-2" },
            });
        var policy = new StubPolicy(WorkScopeProjectionDecision.Observe("EvidenceOnly"));
        var processor = new WorkScopeProjectionProcessor(store, policy);
        var worker = new WorkScopeProjectionWorker(
            processor, "worker-1", TimeSpan.FromMilliseconds(1), batchSize: 10);

        var count = await worker.ProcessBatchAsync();

        count.Should().Be(2);
        store.ClaimCalls.Should().Be(3);
    }

    [Fact]
    public async Task Worker_is_disabled_by_default_and_start_does_not_touch_the_projection_store()
    {
        var store = new StubStore();
        var worker = new WorkScopeProjectionWorker(
            new WorkScopeProjectionProcessor(
                store,
                new StubPolicy(WorkScopeProjectionDecision.Observe("unused"))),
            "worker-disabled");

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        store.ReadyCalls.Should().Be(0);
        store.ClaimCalls.Should().Be(0);
        store.CallOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_worker_propagates_readiness_failure_before_polling()
    {
        var store = new StubStore
        {
            ReadinessFailure = new InvalidOperationException("V157 is missing"),
        };
        var worker = new WorkScopeProjectionWorker(
            new WorkScopeProjectionProcessor(
                store,
                new StubPolicy(WorkScopeProjectionDecision.Observe("unused"))),
            "worker-fail-fast",
            enabled: true);

        var start = () => worker.StartAsync(CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("V157 is missing");
        store.ReadyCalls.Should().Be(1);
        store.ClaimCalls.Should().Be(0);
        store.CallOrder.Should().Equal("ready");
    }

    [Fact]
    public async Task Enabled_worker_awaits_readiness_before_starting_the_poll_loop()
    {
        var store = new StubStore();
        var worker = new WorkScopeProjectionWorker(
            new WorkScopeProjectionProcessor(
                store,
                new StubPolicy(WorkScopeProjectionDecision.Observe("unused"))),
            "worker-ready",
            pollInterval: TimeSpan.FromMinutes(1),
            enabled: true);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        store.ReadyCalls.Should().Be(1);
        store.ClaimCalls.Should().Be(1);
        store.CallOrder.Should().Equal("ready", "claim");
    }

    [Fact]
    public void Canonical_hash_is_independent_of_object_property_order_in_embedded_json()
    {
        var evidence = Claim(terminalCleanupCompleted: true).Event;
        var policy = new WorkScopeProjectionPolicyIdentity("policy", "1");
        var left = ProjectionDecisionCodec.Prepare(
            policy,
            evidence,
            WorkScopeProjectionDecision.Observe("Same", "{\"z\":2,\"a\":1}"));
        var right = ProjectionDecisionCodec.Prepare(
            policy,
            evidence,
            WorkScopeProjectionDecision.Observe("Same", "{\"a\":1,\"z\":2}"));

        left.DecisionHash.Should().Be(right.DecisionHash);
        left.DecisionJson.Should().Be(right.DecisionJson);
    }

    private static WorkScopeProjectionClaim Claim(bool terminalCleanupCompleted)
    {
        var occurredAt = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        var evidence = new WorkScopeProjectionEventDto(
            "cleaner-a",
            "event-1",
            new string('A', 64),
            "WS-1",
            "EQ-1",
            "operation-1",
            "pair-1",
            "sequence-1",
            terminalCleanupCompleted
                ? WorkScopeProjectionStatus.Completed
                : WorkScopeProjectionStatus.Running,
            terminalCleanupCompleted,
            "RECIPE-1",
            new string('B', 64),
            new string('C', 64),
            [
                new WorkScopeProjectionCarrierDto("front", "CARRIER-F", "RUN-F"),
                new WorkScopeProjectionCarrierDto("rear", "CARRIER-R", "RUN-R"),
            ],
            occurredAt,
            occurredAt.AddSeconds(1),
            7,
            terminalCleanupCompleted ? "COMPLETED" : "RUNNING");
        var scope = new WorkScopeDto(
            "WS-1", "PLANT-1", "Equipment", "EQ-1", "Cleaner",
            null, "EQ-1", null, null, "RECIPE-1", null, 1m,
            0m, 0m, 0m, null, "Created", false, null, null, null,
            1, occurredAt.UtcDateTime, "tester", null, null);
        return new WorkScopeProjectionClaim(
            evidence, scope, "worker-1", 1, 1, occurredAt.AddMinutes(2));
    }

    private sealed class StubPolicy : IWorkScopeProjectionPolicy
    {
        private readonly WorkScopeProjectionDecision? _decision;
        private readonly Exception? _exception;

        public StubPolicy(WorkScopeProjectionDecision decision) => _decision = decision;
        public StubPolicy(Exception exception) => _exception = exception;
        public WorkScopeProjectionPolicyIdentity Identity { get; } = new("test-policy", "1");
        public WorkScopeProjectionContext? LastContext { get; private set; }

        public WorkScopeProjectionDecision Decide(WorkScopeProjectionContext context)
        {
            LastContext = context;
            if (_exception is not null) throw _exception;
            return _decision!;
        }
    }

    private sealed class StubStore : IWorkScopeProjectionStore
    {
        private readonly Queue<WorkScopeProjectionClaim> _claims;

        public StubStore(params WorkScopeProjectionClaim[] claims) => _claims = new(claims);

        public int ClaimCalls { get; private set; }
        public int ReadyCalls { get; private set; }
        public Exception? ReadinessFailure { get; init; }
        public List<string> CallOrder { get; } = [];
        public PreparedWorkScopeProjectionDecision? Committed { get; private set; }
        public (string ErrorCode, string ErrorMessage, bool Quarantine, TimeSpan RetryAfter)? Failure { get; private set; }

        public Task EnsureReadyAsync(CancellationToken ct = default)
        {
            ReadyCalls++;
            CallOrder.Add("ready");
            return ReadinessFailure is null
                ? Task.CompletedTask
                : Task.FromException(ReadinessFailure);
        }

        public Task<WorkScopeProjectionClaim?> TryClaimNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            ClaimCalls++;
            CallOrder.Add("claim");
            return Task.FromResult(_claims.TryDequeue(out var claim) ? claim : null);
        }

        public Task<WorkScopeProjectionCommitResult> CommitDecisionAsync(
            WorkScopeProjectionClaim claim,
            PreparedWorkScopeProjectionDecision decision,
            CancellationToken ct = default)
        {
            Committed = decision;
            return Task.FromResult(new WorkScopeProjectionCommitResult(
                decision.Decision.Disposition switch
                {
                    WorkScopeProjectionDisposition.Apply => WorkScopeProjectionCommitKind.Applied,
                    WorkScopeProjectionDisposition.Observe => WorkScopeProjectionCommitKind.Observed,
                    WorkScopeProjectionDisposition.Retry => WorkScopeProjectionCommitKind.RetryScheduled,
                    WorkScopeProjectionDisposition.Quarantine => WorkScopeProjectionCommitKind.Quarantined,
                    _ => throw new ArgumentOutOfRangeException(),
                }));
        }

        public Task<WorkScopeProjectionCommitResult> RecordFailureAsync(
            WorkScopeProjectionClaim claim,
            WorkScopeProjectionPolicyIdentity policy,
            string errorCode,
            string errorMessage,
            bool quarantine,
            TimeSpan retryAfter,
            CancellationToken ct = default)
        {
            Failure = (errorCode, errorMessage, quarantine, retryAfter);
            return Task.FromResult(new WorkScopeProjectionCommitResult(
                quarantine
                    ? WorkScopeProjectionCommitKind.Quarantined
                    : WorkScopeProjectionCommitKind.RetryScheduled));
        }
    }
}
