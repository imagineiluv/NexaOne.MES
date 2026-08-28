using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using NexaDB.Data.Abstractions.Models;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.FDC.Infrastructure;

/// <summary>
/// DB가 권위를 갖는 FDC GLOBAL writer lease입니다. 소유권 행은 해제해도 삭제하지 않으므로
/// FENCE_TOKEN이 재사용되지 않습니다. renew/release는 acquire가 발급한 불투명 grant의
/// owner+fence+config digest+256-bit secret hash를 모두 CAS합니다.
/// </summary>
public sealed class FdcRuntimeLease : QueryRepository, IFdcRuntimeLease
{
    internal static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromDays(1);

    private const int MaximumOwnerIdLength = 100;
    private const int Sha256ByteLength = 32;
    private const int Sha256HexLength = Sha256ByteLength * 2;

    private const string SelectStateSql = """
        SELECT OWNER_ID, FENCE_TOKEN, LEASE_EXPIRES_AT, HEARTBEAT_AT, CONFIG_REVISION
          FROM FDC_RUNTIME_OWNERSHIP
         WHERE LEASE_SCOPE = 'GLOBAL'
        """;

    private const string AcquireSqlServerSql = """
        DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();
        UPDATE FDC_RUNTIME_OWNERSHIP
           SET OWNER_ID = @OwnerId,
               FENCE_TOKEN = FENCE_TOKEN + 1,
               LEASE_EXPIRES_AT = DATEADD(MILLISECOND, @LeaseMilliseconds, @Now),
               HEARTBEAT_AT = @Now,
               CONFIG_REVISION = @ConfigRevisionSha256,
               LEASE_SECRET_HASH = @LeaseSecretHash,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = @Now
         WHERE LEASE_SCOPE = 'GLOBAL'
           AND FENCE_TOKEN = @ExpectedFenceToken
           AND ((@ExpectedOwnerId IS NULL AND OWNER_ID IS NULL)
                OR OWNER_ID COLLATE Latin1_General_100_BIN2
                   = @ExpectedOwnerId COLLATE Latin1_General_100_BIN2)
           AND FENCE_TOKEN < 9223372036854775807
           AND (OWNER_ID IS NULL OR LEASE_EXPIRES_AT <= @Now);
        """;

    private const string AcquireSqliteSql = """
        UPDATE FDC_RUNTIME_OWNERSHIP
           SET OWNER_ID = @OwnerId,
               FENCE_TOKEN = FENCE_TOKEN + 1,
               LEASE_EXPIRES_AT = STRFTIME('%Y-%m-%d %H:%M:%f', 'now', @LeaseModifier),
               HEARTBEAT_AT = STRFTIME('%Y-%m-%d %H:%M:%f', 'now'),
               CONFIG_REVISION = @ConfigRevisionSha256,
               LEASE_SECRET_HASH = @LeaseSecretHash,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
         WHERE LEASE_SCOPE = 'GLOBAL'
           AND FENCE_TOKEN = @ExpectedFenceToken
           AND ((@ExpectedOwnerId IS NULL AND OWNER_ID IS NULL) OR OWNER_ID = @ExpectedOwnerId)
           AND FENCE_TOKEN < 9223372036854775807
           AND (OWNER_ID IS NULL
                OR LEASE_EXPIRES_AT <= STRFTIME('%Y-%m-%d %H:%M:%f', 'now'));
        """;

    private const string RenewSqlServerSql = """
        DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();
        UPDATE FDC_RUNTIME_OWNERSHIP
           SET LEASE_EXPIRES_AT = DATEADD(MILLISECOND, @LeaseMilliseconds, @Now),
               HEARTBEAT_AT = @Now,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = @Now
         WHERE LEASE_SCOPE = 'GLOBAL'
           AND OWNER_ID COLLATE Latin1_General_100_BIN2
               = @OwnerId COLLATE Latin1_General_100_BIN2
           AND FENCE_TOKEN = @FenceToken
           AND CONFIG_REVISION COLLATE Latin1_General_100_BIN2
               = @ConfigRevisionSha256 COLLATE Latin1_General_100_BIN2
           AND LEASE_SECRET_HASH COLLATE Latin1_General_100_BIN2
               = @LeaseSecretHash COLLATE Latin1_General_100_BIN2
           AND LEASE_EXPIRES_AT > @Now;
        """;

    private const string RenewSqliteSql = """
        UPDATE FDC_RUNTIME_OWNERSHIP
           SET LEASE_EXPIRES_AT = STRFTIME('%Y-%m-%d %H:%M:%f', 'now', @LeaseModifier),
               HEARTBEAT_AT = STRFTIME('%Y-%m-%d %H:%M:%f', 'now'),
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
         WHERE LEASE_SCOPE = 'GLOBAL'
           AND OWNER_ID = @OwnerId
           AND FENCE_TOKEN = @FenceToken
           AND CONFIG_REVISION = @ConfigRevisionSha256
           AND LEASE_SECRET_HASH = @LeaseSecretHash
           AND LEASE_EXPIRES_AT > STRFTIME('%Y-%m-%d %H:%M:%f', 'now');
        """;

    private const string ReleaseSqlServerSql = """
        DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();
        UPDATE FDC_RUNTIME_OWNERSHIP
           SET OWNER_ID = NULL,
               LEASE_EXPIRES_AT = NULL,
               HEARTBEAT_AT = NULL,
               CONFIG_REVISION = NULL,
               LEASE_SECRET_HASH = NULL,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = @Now
         WHERE LEASE_SCOPE = 'GLOBAL'
           AND OWNER_ID COLLATE Latin1_General_100_BIN2
               = @OwnerId COLLATE Latin1_General_100_BIN2
           AND FENCE_TOKEN = @FenceToken
           AND CONFIG_REVISION COLLATE Latin1_General_100_BIN2
               = @ConfigRevisionSha256 COLLATE Latin1_General_100_BIN2
           AND LEASE_SECRET_HASH COLLATE Latin1_General_100_BIN2
               = @LeaseSecretHash COLLATE Latin1_General_100_BIN2;
        """;

    private const string ReleaseSqliteSql = """
        UPDATE FDC_RUNTIME_OWNERSHIP
           SET OWNER_ID = NULL,
               LEASE_EXPIRES_AT = NULL,
               HEARTBEAT_AT = NULL,
               CONFIG_REVISION = NULL,
               LEASE_SECRET_HASH = NULL,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
         WHERE LEASE_SCOPE = 'GLOBAL'
           AND OWNER_ID = @OwnerId
           AND FENCE_TOKEN = @FenceToken
           AND CONFIG_REVISION = @ConfigRevisionSha256
           AND LEASE_SECRET_HASH = @LeaseSecretHash;
        """;

    private readonly ServiceObjectProcessor _processor;
    private readonly DatabaseProviderKind _providerKind;

    public FdcRuntimeLease(EesDataSource dataSource) : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _providerKind = dataSource.Provider.Kind;
        if (_providerKind is not (DatabaseProviderKind.SqlServer or DatabaseProviderKind.Sqlite))
        {
            throw new NotSupportedException(
                $"FDC runtime ownership lease does not support database provider '{_providerKind}'.");
        }
    }

    public async Task<FdcRuntimeLeaseAcquireResult> TryAcquireAsync(
        string ownerId,
        string configRevisionSha256,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ownerId = ValidateText(ownerId, nameof(ownerId), MaximumOwnerIdLength);
        configRevisionSha256 = ValidateSha256Hex(
            configRevisionSha256, nameof(configRevisionSha256));
        var leaseMilliseconds = QuantizeLeaseMilliseconds(leaseDuration, nameof(leaseDuration));
        var observed = await GetStateAsync(ct).ConfigureAwait(false);
        var leaseSecret = RandomNumberGenerator.GetBytes(Sha256ByteLength);

        try
        {
            var leaseSecretHash = HashSecret(leaseSecret);
            var parameters = Parameters(
                ownerId,
                configRevisionSha256,
                leaseSecretHash,
                fenceToken: null,
                leaseMilliseconds,
                expectedOwnerId: observed.OwnerId,
                expectedFenceToken: observed.FenceToken);
            var affected = await _processor.ExecuteAsync(
                Sql(AcquireSqlServerSql, AcquireSqliteSql), parameters, ct).ConfigureAwait(false);
            var state = await GetStateAsync(ct).ConfigureAwait(false);

            if (affected == 0)
                return new FdcRuntimeLeaseAcquireResult(false, state, null);
            if (affected != 1)
                throw new DBConcurrencyException(
                    $"FDC GLOBAL lease acquisition affected {affected} rows; expected exactly one.");

            var acquired = string.Equals(state.OwnerId, ownerId, StringComparison.Ordinal)
                           && string.Equals(
                               state.ConfigRevisionSha256,
                               configRevisionSha256,
                               StringComparison.Ordinal)
                           && state.LeaseExpiresAt is not null
                           && observed.FenceToken < long.MaxValue
                           && state.FenceToken == observed.FenceToken + 1;
            if (!acquired)
                return new FdcRuntimeLeaseAcquireResult(false, state, null);

            var authority = new FdcRuntimeAuthority(
                ownerId,
                state.FenceToken,
                configRevisionSha256,
                state.LeaseExpiresAt!.Value);
            return new FdcRuntimeLeaseAcquireResult(
                true,
                state,
                new RuntimeLeaseGrant(authority, leaseSecret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leaseSecret);
        }
    }

    public async Task<FdcRuntimeLeaseGrant?> TryRenewAsync(
        FdcRuntimeLeaseGrant grant,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var runtimeGrant = ValidateGrant(grant);
        var authority = runtimeGrant.Authority;
        var leaseMilliseconds = QuantizeLeaseMilliseconds(leaseDuration, nameof(leaseDuration));
        var leaseSecretHash = HashSecret(runtimeGrant.LeaseSecret);
        var parameters = Parameters(
            authority.OwnerId,
            authority.ConfigRevision,
            leaseSecretHash,
            authority.FenceToken,
            leaseMilliseconds);
        var affected = await _processor.ExecuteAsync(
            Sql(RenewSqlServerSql, RenewSqliteSql), parameters, ct).ConfigureAwait(false);
        if (affected == 0) return null;
        if (affected != 1)
            throw new DBConcurrencyException(
                $"FDC GLOBAL lease renewal affected {affected} rows; expected exactly one.");

        var state = await GetStateAsync(ct).ConfigureAwait(false);
        var renewed = string.Equals(state.OwnerId, authority.OwnerId, StringComparison.Ordinal)
                      && state.FenceToken == authority.FenceToken
                      && string.Equals(
                          state.ConfigRevisionSha256,
                          authority.ConfigRevision,
                          StringComparison.Ordinal)
                      && state.LeaseExpiresAt is not null;
        if (!renewed) return null;

        return new RuntimeLeaseGrant(
            authority with { LeaseExpiresAt = state.LeaseExpiresAt!.Value },
            runtimeGrant.LeaseSecret);
    }

    public async Task<bool> TryReleaseAsync(
        FdcRuntimeLeaseGrant grant,
        CancellationToken ct = default)
    {
        var runtimeGrant = ValidateGrant(grant);
        var authority = runtimeGrant.Authority;
        var leaseSecretHash = HashSecret(runtimeGrant.LeaseSecret);
        var affected = await _processor.ExecuteAsync(
            Sql(ReleaseSqlServerSql, ReleaseSqliteSql),
            Parameters(
                authority.OwnerId,
                authority.ConfigRevision,
                leaseSecretHash,
                authority.FenceToken,
                leaseMilliseconds: null),
            ct).ConfigureAwait(false);
        if (affected is 0 or 1) return affected == 1;
        throw new DBConcurrencyException(
            $"FDC GLOBAL lease release affected {affected} rows; expected at most one.");
    }

    public async Task<FdcRuntimeLeaseState> GetStateAsync(CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<LeaseRow>(SelectStateSql, ct: ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException(
                "FDC GLOBAL runtime ownership row is missing. Refusing to recreate a lost fence counter.");
        }

        return new FdcRuntimeLeaseState(
            NullIfBlank(row.OwnerId),
            row.FenceToken,
            AsUtc(row.LeaseExpiresAt),
            AsUtc(row.HeartbeatAt),
            NullIfBlank(row.ConfigRevision));
    }

    private string Sql(string sqlServer, string sqlite) =>
        _providerKind == DatabaseProviderKind.SqlServer ? sqlServer : sqlite;

    private static object Parameters(
        string ownerId,
        string configRevisionSha256,
        string leaseSecretHash,
        long? fenceToken,
        int? leaseMilliseconds,
        string? expectedOwnerId = null,
        long? expectedFenceToken = null) => new
    {
        OwnerId = ownerId,
        ConfigRevisionSha256 = configRevisionSha256,
        LeaseSecretHash = leaseSecretHash,
        FenceToken = fenceToken,
        ExpectedOwnerId = expectedOwnerId,
        ExpectedFenceToken = expectedFenceToken,
        LeaseMilliseconds = leaseMilliseconds ?? 0,
        LeaseModifier = leaseMilliseconds is null
            ? string.Empty
            : $"+{(leaseMilliseconds.Value / 1000m).ToString("0.000", CultureInfo.InvariantCulture)} seconds",
    };

    private static RuntimeLeaseGrant ValidateGrant(FdcRuntimeLeaseGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant is not RuntimeLeaseGrant runtimeGrant)
        {
            throw new ArgumentException(
                "The lease grant was not issued by FdcRuntimeLease.", nameof(grant));
        }

        return runtimeGrant;
    }

    private static string ValidateText(string value, string paramName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentOutOfRangeException(
                paramName, normalized.Length, $"Value must be at most {maximumLength} characters.");
        return normalized;
    }

    private static string ValidateSha256Hex(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        var normalized = value.Trim();
        if (normalized.Length != Sha256HexLength
            || normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Configuration revision must be a 64-character SHA-256 hexadecimal digest.",
                paramName);
        }

        return normalized.ToLowerInvariant();
    }

    private static int QuantizeLeaseMilliseconds(TimeSpan value, string paramName)
    {
        var milliseconds = Math.Ceiling(value.TotalMilliseconds);
        if (milliseconds < MinimumLeaseDuration.TotalMilliseconds
            || milliseconds > MaximumLeaseDuration.TotalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"FDC runtime lease duration must quantize to an integer millisecond between " +
                $"{MinimumLeaseDuration} and {MaximumLeaseDuration}.");
        }

        return checked((int)milliseconds);
    }

    private static string HashSecret(ReadOnlySpan<byte> secret) =>
        Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant();

    private static DateTime? AsUtc(DateTime? value) => value is null
        ? null
        : value.Value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class RuntimeLeaseGrant : FdcRuntimeLeaseGrant
    {
        public RuntimeLeaseGrant(FdcRuntimeAuthority authority, ReadOnlySpan<byte> leaseSecret)
            : base(authority)
        {
            LeaseSecret = leaseSecret.ToArray();
        }

        internal byte[] LeaseSecret { get; }
    }

    private sealed class LeaseRow
    {
        public string? OwnerId { get; set; }
        public long FenceToken { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public DateTime? HeartbeatAt { get; set; }
        public string? ConfigRevision { get; set; }
    }
}
