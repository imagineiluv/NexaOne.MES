using Microsoft.Extensions.Configuration;
using NexaOne.FDC.Application.Fdc;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.UnitTests.Fdc;

public sealed class RunAdmissionServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Missing_or_false_module_gate_exposes_only_a_disabled_direct_bridge(bool? enabled)
    {
        var values = enabled is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>
            {
                ["RunAdmission:Enabled"] = enabled.Value.ToString(),
            };
        var service = NexaOne.FDC.Module.CreateRunAdmissionService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build());
        var proof = new RunAdmissionLeaseProofDto(
            "EQ-1", "cleaner-a", "lease-1", "generation-1", 1, "token");

        var acquire = await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"));
        var keepAlive = await service.KeepAliveAsync(proof);
        var release = await service.ReleaseAsync(proof);

        acquire.IsAdmitted.Should().BeFalse();
        acquire.Lease.Should().BeNull();
        acquire.Code.Should().Be(DisabledRunAdmissionService.Code);
        keepAlive.IsCurrent.Should().BeFalse();
        keepAlive.IsAbsent.Should().BeTrue();
        keepAlive.Code.Should().Be(DisabledRunAdmissionService.Code);
        release.Released.Should().BeFalse();
        release.Code.Should().Be(DisabledRunAdmissionService.Code);
    }

    [Fact]
    public async Task Unsafe_runtime_never_issues_a_capability()
    {
        var safety = new StubSafetySource(FdcRunAdmissionSafetySnapshot.Denied(
            "FDC_RUN_NOT_PERMITTED", "interlock active"));
        var service = Create(safety);

        var result = await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"));

        result.IsAdmitted.Should().BeFalse();
        result.Lease.Should().BeNull();
        result.Code.Should().Be("FDC_RUN_NOT_PERMITTED");
    }

    [Fact]
    public async Task Same_request_is_idempotent_but_a_second_client_cannot_share_equipment()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero));
        var service = Create(new StubSafetySource(Permitted(fence: 7)), clock);
        var request = new RunAdmissionAcquireDto("EQ-1", "cleaner-a", "request-1");

        var first = await service.AcquireAsync(request);
        clock.Advance(TimeSpan.FromSeconds(2));
        var replay = await service.AcquireAsync(request);
        var competing = await service.AcquireAsync(new("EQ-1", "cleaner-b", "request-2"));

        first.IsAdmitted.Should().BeTrue();
        replay.Lease!.LeaseId.Should().Be(first.Lease!.LeaseId);
        replay.Lease!.AccessToken.Should().Be(first.Lease!.AccessToken);
        replay.Lease.ObservedAt.Should().Be(clock.GetUtcNow());
        replay.Lease.KeepAliveTtlMilliseconds.Should().Be(4_000);
        competing.IsAdmitted.Should().BeFalse();
        competing.Code.Should().Be("RUN_ADMISSION_ALREADY_OWNED");
    }

    [Fact]
    public async Task Keep_alive_extends_only_the_soft_deadline_and_preserves_hard_expiry()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero));
        var service = Create(new StubSafetySource(Permitted(fence: 7)), clock);
        var lease = (await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"))).Lease!;
        clock.Advance(TimeSpan.FromSeconds(2));

        var status = await service.KeepAliveAsync(Proof(lease));

        status.IsCurrent.Should().BeTrue();
        status.KeepAliveExpiresAt.Should().Be(clock.GetUtcNow().AddSeconds(6));
        status.KeepAliveTtlMilliseconds.Should().Be(6_000);
        lease.HardExpiresAt.Should().Be(new DateTimeOffset(2026, 8, 28, 13, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Runtime_fence_change_revokes_and_exact_release_is_idempotently_confirmed()
    {
        var safety = new StubSafetySource(Permitted(fence: 7));
        var service = Create(safety);
        var lease = (await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"))).Lease!;
        safety.Snapshot = Permitted(fence: 8);

        var status = await service.KeepAliveAsync(Proof(lease));
        var release = await service.ReleaseAsync(Proof(lease));

        status.IsCurrent.Should().BeFalse();
        status.Code.Should().Be("RUN_ADMISSION_AUTHORITY_CHANGED");
        status.IsAbsent.Should().BeTrue();
        release.Released.Should().BeTrue();
        release.Code.Should().Be("RUN_ADMISSION_ALREADY_RELEASED");
    }

    [Fact]
    public async Task Forged_token_cannot_keep_alive_or_release_current_lease()
    {
        var service = Create(new StubSafetySource(Permitted(fence: 7)));
        var lease = (await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"))).Lease!;
        var forged = Proof(lease) with { AccessToken = Convert.ToBase64String(new byte[32]) };

        var forgedStatus = await service.KeepAliveAsync(forged);
        forgedStatus.Code.Should().Be("RUN_ADMISSION_PROOF_CONFLICT");
        forgedStatus.IsAbsent.Should().BeFalse();
        (await service.ReleaseAsync(forged)).Released.Should().BeFalse();
        (await service.KeepAliveAsync(Proof(lease))).IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task Missed_keep_alive_expires_session_and_allows_new_owner()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero));
        var service = Create(new StubSafetySource(Permitted(fence: 7)), clock);
        var lease = (await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"))).Lease!;
        clock.Advance(TimeSpan.FromSeconds(7));

        var replacement = await service.AcquireAsync(new("EQ-1", "cleaner-b", "request-2"));
        var oldRelease = await service.ReleaseAsync(Proof(lease));

        replacement.IsAdmitted.Should().BeTrue();
        oldRelease.Released.Should().BeTrue();
    }

    [Fact]
    public async Task Transient_interlock_epoch_change_permanently_revokes_old_lease()
    {
        var safety = new StubSafetySource(Permitted(fence: 7, safetyEpoch: 10));
        var service = Create(safety);
        var lease = (await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"))).Lease!;
        safety.Snapshot = Permitted(fence: 7, safetyEpoch: 11);

        var status = await service.KeepAliveAsync(Proof(lease));

        status.IsCurrent.Should().BeFalse();
        status.Code.Should().Be("RUN_ADMISSION_AUTHORITY_CHANGED");
    }

    [Fact]
    public async Task Released_acquire_request_cannot_issue_a_new_capability_during_retention()
    {
        var service = Create(new StubSafetySource(Permitted(fence: 7)));
        var request = new RunAdmissionAcquireDto("EQ-1", "cleaner-a", "request-1");
        var lease = (await service.AcquireAsync(request)).Lease!;
        (await service.ReleaseAsync(Proof(lease))).Released.Should().BeTrue();

        var replay = await service.AcquireAsync(request);

        replay.IsAdmitted.Should().BeFalse();
        replay.Code.Should().Be("RUN_ADMISSION_REQUEST_RETIRED");
    }

    [Fact]
    public async Task Wall_clock_rollback_does_not_extend_monotonic_soft_expiry()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero));
        var service = Create(new StubSafetySource(Permitted(fence: 7)), clock);
        await service.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"));
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(7));
        clock.SetUtcNow(clock.GetUtcNow().AddHours(-12));

        var replacement = await service.AcquireAsync(new("EQ-1", "cleaner-b", "request-2"));

        replacement.IsAdmitted.Should().BeTrue();
    }

    [Fact]
    public async Task Capability_records_redact_access_tokens_from_diagnostic_text()
    {
        var lease = (await Create(new StubSafetySource(Permitted(fence: 7)))
            .AcquireAsync(new("EQ-1", "cleaner-a", "request-1"))).Lease!;

        lease.ToString().Should().NotContain(lease.AccessToken).And.Contain("[REDACTED]");
        Proof(lease).ToString().Should().NotContain(lease.AccessToken).And.Contain("[REDACTED]");
    }

    [Fact]
    public async Task New_server_generation_confirms_old_process_capability_is_absent()
    {
        var safety = new StubSafetySource(Permitted(fence: 7));
        var oldServer = Create(safety);
        var oldLease = (await oldServer.AcquireAsync(new("EQ-1", "cleaner-a", "request-1"))).Lease!;
        var restartedServer = Create(safety);

        var release = await restartedServer.ReleaseAsync(Proof(oldLease));

        release.Released.Should().BeTrue();
        release.Code.Should().Be("RUN_ADMISSION_ALREADY_RELEASED");
    }

    [Fact]
    public async Task Live_sessions_reserve_capacity_for_their_future_terminal_records()
    {
        var service = Create(
            new StubSafetySource(Permitted(fence: 7)),
            maxTombstones: 100);

        for (var index = 0; index < 100; index++)
        {
            var admitted = await service.AcquireAsync(new(
                $"EQ-{index}",
                "cleaner-a",
                $"request-{index}"));
            admitted.IsAdmitted.Should().BeTrue();
        }

        var overflow = await service.AcquireAsync(new(
            "EQ-overflow",
            "cleaner-a",
            "request-overflow"));

        overflow.IsAdmitted.Should().BeFalse();
        overflow.Code.Should().Be("RUN_ADMISSION_LEDGER_CAPACITY_REACHED");
    }

    private static RunAdmissionService Create(
        IRunAdmissionSafetySource safety,
        TimeProvider? timeProvider = null,
        int maxTombstones = 100_000) =>
        new(
            safety,
            new RunAdmissionOptions(
                TimeSpan.FromSeconds(6),
                TimeSpan.FromHours(12),
                TimeSpan.FromDays(1),
                maxTombstones),
            timeProvider);

    private static FdcRunAdmissionSafetySnapshot Permitted(long fence, long safetyEpoch = 1) =>
        FdcRunAdmissionSafetySnapshot.Permitted(new FdcRunAdmissionAuthority(
            "EQ-1",
            "fdc-owner:process-generation",
            fence,
            new string('a', 64),
            safetyEpoch));

    private static RunAdmissionLeaseProofDto Proof(RunAdmissionLeaseDto lease) => new(
        lease.EquipmentId,
        lease.ClientId,
        lease.LeaseId,
        lease.AuthorityGeneration,
        lease.Fence,
        lease.AccessToken);

    private sealed class StubSafetySource(FdcRunAdmissionSafetySnapshot snapshot)
        : IRunAdmissionSafetySource
    {
        public FdcRunAdmissionSafetySnapshot Snapshot { get; set; } = snapshot;

        public FdcRunAdmissionSafetySnapshot Capture(string equipmentId) =>
            Snapshot.Authority is { } authority
                ? Snapshot with { Authority = authority with { EquipmentId = equipmentId } }
                : Snapshot;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration)
        {
            _now = _now.Add(duration);
            AdvanceMonotonic(duration);
        }
        public void AdvanceMonotonic(TimeSpan duration) =>
            _timestamp = checked(_timestamp + duration.Ticks);
        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }
}
