using System.Security.Cryptography;
using System.Text;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.FDC.Application.Fdc;

internal sealed class DisabledRunAdmissionService : IRunAdmissionService
{
    internal const string Code = "RUN_ADMISSION_FEATURE_DISABLED";
    private const string Message =
        "Run admission is disabled until its durable shared admission ledger is available.";

    internal static DisabledRunAdmissionService Instance { get; } = new();

    private DisabledRunAdmissionService()
    {
    }

    public Task<RunAdmissionDecisionDto> AcquireAsync(
        RunAdmissionAcquireDto request,
        CancellationToken ct = default) =>
        Task.FromResult(new RunAdmissionDecisionDto(false, Code, Message, null));

    public Task<RunAdmissionStatusDto> KeepAliveAsync(
        RunAdmissionLeaseProofDto request,
        CancellationToken ct = default) =>
        Task.FromResult(new RunAdmissionStatusDto(
            false,
            Code,
            Message,
            DateTimeOffset.UtcNow,
            null,
            null,
            true));

    public Task<RunAdmissionReleaseDto> ReleaseAsync(
        RunAdmissionLeaseProofDto request,
        CancellationToken ct = default) =>
        Task.FromResult(new RunAdmissionReleaseDto(false, Code, Message));
}

/// <summary>
/// FDC 안전 snapshot과 동일 process-generation/fence에 묶인 단일 설비 자동운전 lease를 발급한다.
/// DB writer lease를 복제하지 않고, 짧은 keep-alive와 process-local session을 사용해 서버 재시작과 통신 단절을
/// fail-closed로 만든다. 실제 Stop 실행은 lease 변경을 관찰하는 설비 client의 책임이다.
/// </summary>
internal sealed class RunAdmissionService : IRunAdmissionService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessionsByEquipment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tombstone> _tombstonesByLease = new(StringComparer.Ordinal);
    private readonly Dictionary<RequestIdentity, Tombstone> _tombstonesByRequest = new();
    private readonly IRunAdmissionSafetySource _safetySource;
    private readonly TimeProvider _timeProvider;
    private readonly RunAdmissionOptions _options;
    private readonly string _authorityGeneration = Convert.ToHexString(
        RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    internal RunAdmissionService(
        IRunAdmissionSafetySource safetySource,
        RunAdmissionOptions options,
        TimeProvider? timeProvider = null)
    {
        _safetySource = safetySource ?? throw new ArgumentNullException(nameof(safetySource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        RunAdmissionOptions.Validate(options);
    }

    public Task<RunAdmissionDecisionDto> AcquireAsync(
        RunAdmissionAcquireDto request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var equipmentId = Required(request.EquipmentId, nameof(request.EquipmentId), 100);
        var clientId = Required(request.ClientId, nameof(request.ClientId), 100);
        var requestId = Required(request.RequestId, nameof(request.RequestId), 100);
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();

        lock (_gate)
        {
            PurgeExpired(timestamp);
            var requestIdentity = new RequestIdentity(equipmentId, clientId, requestId);
            if (_tombstonesByRequest.ContainsKey(requestIdentity))
            {
                return Task.FromResult(Denied(
                    "RUN_ADMISSION_REQUEST_RETIRED",
                    "This acquire request already reached a terminal state and cannot issue another lease."));
            }

            var safety = _safetySource.Capture(equipmentId);
            if (!safety.IsPermitted || safety.Authority is null)
            {
                if (_sessionsByEquipment.TryGetValue(equipmentId, out var unsafeSession))
                    Retire(unsafeSession, timestamp);
                return Task.FromResult(Denied(
                    safety.Code,
                    safety.Message));
            }

            if (_sessionsByEquipment.TryGetValue(equipmentId, out var existing))
            {
                if (string.Equals(existing.ClientId, clientId, StringComparison.Ordinal)
                    && string.Equals(existing.RequestId, requestId, StringComparison.Ordinal)
                    && AuthorityMatches(existing, safety.Authority))
                {
                    return Task.FromResult(Admitted(existing, now, timestamp));
                }

                if (!AuthorityMatches(existing, safety.Authority))
                {
                    Retire(existing, timestamp);
                    return Task.FromResult(Denied(
                        "RUN_ADMISSION_AUTHORITY_CHANGED",
                        "The FDC runtime or safety generation changed; the previous automatic-run lease was revoked."));
                }

                return Task.FromResult(Denied(
                    "RUN_ADMISSION_ALREADY_OWNED",
                    "This equipment already has a current automatic-run lease."));
            }

            // Every live session must already own capacity for its eventual terminal record.
            // Otherwise a burst of distinct equipment sessions could all retire at once and
            // grow the supposedly bounded ledger past MaxTombstones.
            if (_tombstonesByLease.Count + _sessionsByEquipment.Count >= _options.MaxTombstones)
            {
                return Task.FromResult(Denied(
                    "RUN_ADMISSION_LEDGER_CAPACITY_REACHED",
                    "The terminal admission ledger reached its configured capacity; no new lease is issued."));
            }

            var accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var session = new Session(
                equipmentId,
                clientId,
                requestId,
                Guid.NewGuid().ToString("N"),
                _authorityGeneration,
                safety.Authority.FenceToken,
                safety.Authority.OwnerId,
                safety.Authority.ConfigRevision,
                safety.Authority.SafetyEpoch,
                timestamp,
                timestamp,
                accessToken);
            _sessionsByEquipment.Add(equipmentId, session);
            return Task.FromResult(Admitted(session, now, timestamp));
        }
    }

    public Task<RunAdmissionStatusDto> KeepAliveAsync(
        RunAdmissionLeaseProofDto request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var proof = NormalizeProof(request);
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();

        lock (_gate)
        {
            PurgeExpired(timestamp);
            if (!_sessionsByEquipment.TryGetValue(proof.EquipmentId, out var session))
            {
                var isAbsent = IsKnownTombstone(proof)
                               || !string.Equals(
                                   proof.AuthorityGeneration,
                                   _authorityGeneration,
                                   StringComparison.Ordinal);
                return Task.FromResult(NotCurrent(
                    isAbsent
                        ? "RUN_ADMISSION_REVOKED"
                        : "RUN_ADMISSION_NOT_FOUND",
                    "The automatic-run lease is absent or no longer current.",
                    now,
                    isAbsent));
            }
            if (!ProofMatches(session, proof))
            {
                if (IsKnownTombstone(proof))
                {
                    return Task.FromResult(NotCurrent(
                        "RUN_ADMISSION_REVOKED",
                        "The exact automatic-run lease is already absent or revoked.",
                        now,
                        isAbsent: true));
                }
                return Task.FromResult(NotCurrent(
                    "RUN_ADMISSION_PROOF_CONFLICT",
                    "The automatic-run lease proof does not match the current owner.",
                    now,
                    isAbsent: false));
            }

            var safety = _safetySource.Capture(proof.EquipmentId);
            if (!safety.IsPermitted
                || safety.Authority is null
                || !AuthorityMatches(session, safety.Authority))
            {
                Retire(session, timestamp);
                return Task.FromResult(NotCurrent(
                    safety.IsPermitted ? "RUN_ADMISSION_AUTHORITY_CHANGED" : safety.Code,
                    safety.IsPermitted
                        ? "The FDC runtime authority changed; the automatic-run lease was revoked."
                        : safety.Message,
                    now,
                    isAbsent: true));
            }

            session.LastKeepAliveTimestamp = timestamp;
            var keepAliveRemaining = KeepAliveRemaining(session, timestamp);
            return Task.FromResult(new RunAdmissionStatusDto(
                true,
                "RUN_ADMISSION_CURRENT",
                "The automatic-run lease remains current.",
                now,
                now.Add(keepAliveRemaining),
                ToWholeMilliseconds(keepAliveRemaining),
                IsAbsent: false));
        }
    }

    public Task<RunAdmissionReleaseDto> ReleaseAsync(
        RunAdmissionLeaseProofDto request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var proof = NormalizeProof(request);
        var timestamp = _timeProvider.GetTimestamp();

        lock (_gate)
        {
            PurgeExpired(timestamp);
            if (!_sessionsByEquipment.TryGetValue(proof.EquipmentId, out var session))
            {
                return Task.FromResult(IsKnownTombstone(proof)
                    || !string.Equals(
                        proof.AuthorityGeneration,
                        _authorityGeneration,
                        StringComparison.Ordinal)
                    ? new RunAdmissionReleaseDto(
                        true,
                        "RUN_ADMISSION_ALREADY_RELEASED",
                        "The exact lease is absent, revoked, or belongs to a prior server generation.")
                    : new RunAdmissionReleaseDto(
                        false,
                        "RUN_ADMISSION_PROOF_CONFLICT",
                        "The authority did not issue the supplied automatic-run capability."));
            }
            if (!ProofMatches(session, proof))
            {
                if (IsKnownTombstone(proof))
                {
                    return Task.FromResult(new RunAdmissionReleaseDto(
                        true,
                        "RUN_ADMISSION_ALREADY_RELEASED",
                        "The exact automatic-run lease is already absent or revoked."));
                }
                return Task.FromResult(new RunAdmissionReleaseDto(
                    false,
                    "RUN_ADMISSION_PROOF_CONFLICT",
                    "The automatic-run lease proof does not match the current owner."));
            }

            Retire(session, timestamp);
            return Task.FromResult(new RunAdmissionReleaseDto(
                true,
                "RUN_ADMISSION_RELEASED",
                "The automatic-run lease was released."));
        }
    }

    private void PurgeExpired(long timestamp)
    {
        foreach (var session in _sessionsByEquipment.Values
                     .Where(session => IsExpired(session, timestamp))
                     .ToArray())
        {
            Retire(session, timestamp);
        }

        foreach (var tombstone in _tombstonesByLease.Values
                     .Where(tombstone => Elapsed(tombstone.CreatedTimestamp, timestamp) >= tombstone.PurgeAfter)
                     .ToArray())
        {
            _tombstonesByLease.Remove(tombstone.LeaseId);
            _tombstonesByRequest.Remove(tombstone.RequestIdentity);
        }
    }

    private void Retire(Session session, long timestamp)
    {
        if (_sessionsByEquipment.TryGetValue(session.EquipmentId, out var current)
            && ReferenceEquals(current, session))
        {
            _sessionsByEquipment.Remove(session.EquipmentId);
        }

        var hardRemaining = HardRemaining(session, timestamp);
        var tombstone = new Tombstone(
            session.EquipmentId,
            session.ClientId,
            session.RequestId,
            session.LeaseId,
            session.AuthorityGeneration,
            session.Fence,
            SHA256.HashData(Encoding.UTF8.GetBytes(session.AccessToken)),
            timestamp,
            hardRemaining >= _options.TombstoneRetention
                ? hardRemaining
                : _options.TombstoneRetention);
        _tombstonesByLease[session.LeaseId] = tombstone;
        _tombstonesByRequest[tombstone.RequestIdentity] = tombstone;
    }

    private bool IsKnownTombstone(NormalizedProof proof) =>
        _tombstonesByLease.TryGetValue(proof.LeaseId, out var tombstone)
        && string.Equals(tombstone.EquipmentId, proof.EquipmentId, StringComparison.Ordinal)
        && string.Equals(tombstone.ClientId, proof.ClientId, StringComparison.Ordinal)
        && string.Equals(tombstone.AuthorityGeneration, proof.AuthorityGeneration, StringComparison.Ordinal)
        && tombstone.Fence == proof.Fence
        && SecretDigestEquals(tombstone.AccessTokenDigest, proof.AccessToken);

    private static bool ProofMatches(Session session, NormalizedProof proof) =>
        string.Equals(session.EquipmentId, proof.EquipmentId, StringComparison.Ordinal)
        && string.Equals(session.ClientId, proof.ClientId, StringComparison.Ordinal)
        && string.Equals(session.LeaseId, proof.LeaseId, StringComparison.Ordinal)
        && string.Equals(session.AuthorityGeneration, proof.AuthorityGeneration, StringComparison.Ordinal)
        && session.Fence == proof.Fence
        && SecretEquals(session.AccessToken, proof.AccessToken);

    private static bool AuthorityMatches(Session session, FdcRunAdmissionAuthority authority) =>
        session.Fence == authority.FenceToken
        && session.SafetyEpoch == authority.SafetyEpoch
        && string.Equals(session.EquipmentId, authority.EquipmentId, StringComparison.Ordinal)
        && string.Equals(session.RuntimeOwnerId, authority.OwnerId, StringComparison.Ordinal)
        && string.Equals(session.ConfigRevision, authority.ConfigRevision, StringComparison.Ordinal);

    private static bool SecretEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static bool SecretDigestEquals(byte[] expectedDigest, string supplied)
    {
        var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
    }

    private RunAdmissionDecisionDto Admitted(
        Session session,
        DateTimeOffset observedAt,
        long timestamp)
    {
        var hardRemaining = HardRemaining(session, timestamp);
        var keepAliveRemaining = KeepAliveRemaining(session, timestamp);
        return new(
        true,
        "RUN_ADMISSION_GRANTED",
        "A current automatic-run lease was granted.",
        new RunAdmissionLeaseDto(
            session.EquipmentId,
            session.ClientId,
            session.LeaseId,
            session.AuthorityGeneration,
            session.Fence,
            observedAt,
            observedAt.Add(hardRemaining),
            observedAt.Add(keepAliveRemaining),
            ToWholeMilliseconds(hardRemaining),
            ToWholeMilliseconds(keepAliveRemaining),
            session.AccessToken));
    }

    private static RunAdmissionDecisionDto Denied(string code, string message) =>
        new(false, code, message, null);

    private static RunAdmissionStatusDto NotCurrent(
        string code,
        string message,
        DateTimeOffset observedAt,
        bool isAbsent) =>
        new(false, code, message, observedAt, null, null, isAbsent);

    private static NormalizedProof NormalizeProof(RunAdmissionLeaseProofDto request)
    {
        if (request.Fence <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Fence));
        return new NormalizedProof(
            Required(request.EquipmentId, nameof(request.EquipmentId), 100),
            Required(request.ClientId, nameof(request.ClientId), 100),
            Required(request.LeaseId, nameof(request.LeaseId), 100),
            Required(request.AuthorityGeneration, nameof(request.AuthorityGeneration), 100),
            request.Fence,
            Required(request.AccessToken, nameof(request.AccessToken), 200));
    }

    private static string Required(string value, string name, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value cannot exceed {maximumLength} characters.");
        return normalized;
    }

    private bool IsExpired(Session session, long timestamp) =>
        HardRemaining(session, timestamp) <= TimeSpan.Zero
        || KeepAliveRemaining(session, timestamp) <= TimeSpan.Zero;

    private TimeSpan HardRemaining(Session session, long timestamp) =>
        Remaining(session.IssuedTimestamp, timestamp, _options.HardLeaseDuration);

    private TimeSpan KeepAliveRemaining(Session session, long timestamp)
    {
        var keepAlive = Remaining(
            session.LastKeepAliveTimestamp,
            timestamp,
            _options.KeepAliveLeaseDuration);
        var hard = HardRemaining(session, timestamp);
        return keepAlive <= hard ? keepAlive : hard;
    }

    private TimeSpan Remaining(long startTimestamp, long timestamp, TimeSpan duration)
    {
        var elapsed = Elapsed(startTimestamp, timestamp);
        return elapsed >= duration ? TimeSpan.Zero : duration - elapsed;
    }

    private TimeSpan Elapsed(long startTimestamp, long timestamp)
    {
        var elapsed = _timeProvider.GetElapsedTime(startTimestamp, timestamp);
        // Timestamp providers are required to be monotonic. A broken provider must fail closed.
        return elapsed < TimeSpan.Zero ? TimeSpan.MaxValue : elapsed;
    }

    private static long ToWholeMilliseconds(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : checked((long)Math.Floor(duration.TotalMilliseconds));

    private sealed class Session(
        string equipmentId,
        string clientId,
        string requestId,
        string leaseId,
        string authorityGeneration,
        long fence,
        string runtimeOwnerId,
        string configRevision,
        long safetyEpoch,
        long issuedTimestamp,
        long lastKeepAliveTimestamp,
        string accessToken)
    {
        public string EquipmentId { get; } = equipmentId;
        public string ClientId { get; } = clientId;
        public string RequestId { get; } = requestId;
        public string LeaseId { get; } = leaseId;
        public string AuthorityGeneration { get; } = authorityGeneration;
        public long Fence { get; } = fence;
        public string RuntimeOwnerId { get; } = runtimeOwnerId;
        public string ConfigRevision { get; } = configRevision;
        public long SafetyEpoch { get; } = safetyEpoch;
        public long IssuedTimestamp { get; } = issuedTimestamp;
        public long LastKeepAliveTimestamp { get; set; } = lastKeepAliveTimestamp;
        public string AccessToken { get; } = accessToken;
    }

    private sealed record Tombstone(
        string EquipmentId,
        string ClientId,
        string RequestId,
        string LeaseId,
        string AuthorityGeneration,
        long Fence,
        byte[] AccessTokenDigest,
        long CreatedTimestamp,
        TimeSpan PurgeAfter)
    {
        public RequestIdentity RequestIdentity { get; } = new(EquipmentId, ClientId, RequestId);
    }

    private readonly record struct RequestIdentity(
        string EquipmentId,
        string ClientId,
        string RequestId);

    private sealed record NormalizedProof(
        string EquipmentId,
        string ClientId,
        string LeaseId,
        string AuthorityGeneration,
        long Fence,
        string AccessToken);
}

internal interface IRunAdmissionSafetySource
{
    FdcRunAdmissionSafetySnapshot Capture(string equipmentId);
}

internal sealed record FdcRunAdmissionAuthority(
    string EquipmentId,
    string OwnerId,
    long FenceToken,
    string ConfigRevision,
    long SafetyEpoch);

internal sealed record FdcRunAdmissionSafetySnapshot(
    bool IsPermitted,
    string Code,
    string Message,
    FdcRunAdmissionAuthority? Authority)
{
    public static FdcRunAdmissionSafetySnapshot Denied(string code, string message) =>
        new(false, code, message, null);

    public static FdcRunAdmissionSafetySnapshot Permitted(FdcRunAdmissionAuthority authority) =>
        new(true, "FDC_RUN_PERMITTED", "FDC interlock runtime permits automatic operation.", authority);
}

internal sealed class FdcCollectorRunAdmissionSafetySource(FdcCollectorService collector)
    : IRunAdmissionSafetySource
{
    private readonly FdcCollectorService _collector =
        collector ?? throw new ArgumentNullException(nameof(collector));

    public FdcRunAdmissionSafetySnapshot Capture(string equipmentId) =>
        _collector.CaptureRunAdmissionSafety(equipmentId);
}

internal sealed record RunAdmissionOptions(
    TimeSpan KeepAliveLeaseDuration,
    TimeSpan HardLeaseDuration,
    TimeSpan TombstoneRetention,
    int MaxTombstones = 100_000)
{
    internal static void Validate(RunAdmissionOptions options)
    {
        if (options.KeepAliveLeaseDuration < TimeSpan.FromSeconds(3)
            || options.KeepAliveLeaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Run-admission keep-alive lease must be between 3 seconds and 5 minutes.");
        }
        if (options.HardLeaseDuration < TimeSpan.FromMinutes(1)
            || options.HardLeaseDuration > TimeSpan.FromDays(1)
            || options.HardLeaseDuration < options.KeepAliveLeaseDuration * 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Run-admission hard lease must be 1 minute..1 day and at least three keep-alive leases.");
        }
        if (options.TombstoneRetention < TimeSpan.FromMinutes(1)
            || options.TombstoneRetention > TimeSpan.FromDays(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Run-admission tombstone retention must be between 1 minute and 2 days.");
        }
        if (options.MaxTombstones is < 100 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Run-admission terminal ledger capacity must be between 100 and 1000000.");
        }
    }
}
