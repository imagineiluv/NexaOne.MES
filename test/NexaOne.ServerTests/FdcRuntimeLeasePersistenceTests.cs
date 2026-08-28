using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaOne.FDC.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Fdc;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class FdcRuntimeLeasePersistenceTests
{
    [Fact]
    public async Task Concurrent_hosts_elect_exactly_one_global_writer_and_only_winner_gets_grant()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();
        var configRevision = Revision("concurrent");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            lease.TryAcquireAsync($"owner-{index}", configRevision, TimeSpan.FromSeconds(30))));

        var winner = attempts.Should().ContainSingle(result => result.Acquired).Subject;
        winner.Grant.Should().NotBeNull();
        winner.State.FenceToken.Should().Be(1);
        winner.State.HasOwnerTuple.Should().BeTrue();
        attempts.Where(result => !result.Acquired)
            .Should().OnlyContain(result =>
                result.Grant == null && result.State.FenceToken == winner.State.FenceToken);
        (await lease.GetStateAsync()).Should().Be(winner.State);

        (await lease.TryReleaseAsync(winner.Grant!)).Should().BeTrue();
    }

    [Fact]
    public async Task Expired_grant_cannot_resurrect_renew_or_release_after_new_fence_is_issued()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();
        var first = await lease.TryAcquireAsync(
            "owner-old", Revision("config-a"), TimeSpan.FromSeconds(1));
        first.Acquired.Should().BeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(1_350));

        Action resurrectExpiredFence = () => database.Execute("""
            UPDATE FDC_RUNTIME_OWNERSHIP
               SET HEARTBEAT_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now'),
                   LEASE_EXPIRES_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '+30 seconds')
             WHERE LEASE_SCOPE='GLOBAL';
            """);
        resurrectExpiredFence.Should().Throw<SqliteException>(
            "renewal eligibility must use DB now rather than a writer-supplied heartbeat");

        var second = await lease.TryAcquireAsync(
            "owner-new", Revision("config-b"), TimeSpan.FromSeconds(30));
        second.Acquired.Should().BeTrue();
        second.State.FenceToken.Should().Be(first.State.FenceToken + 1);
        (await lease.TryRenewAsync(first.Grant!, TimeSpan.FromSeconds(30))).Should().BeNull();
        (await lease.TryReleaseAsync(first.Grant!)).Should().BeFalse();

        var renewed = await lease.TryRenewAsync(second.Grant!, TimeSpan.FromSeconds(30));
        renewed.Should().NotBeNull();
        renewed!.Authority.FenceToken.Should().Be(second.State.FenceToken);
        renewed.Authority.LeaseExpiresAt.Should().BeOnOrAfter(second.State.LeaseExpiresAt!.Value);
        (await lease.TryReleaseAsync(renewed)).Should().BeTrue();
    }

    [Fact]
    public async Task Voluntary_release_preserves_counter_and_stale_grant_never_matches_reacquisition()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();
        var first = await lease.TryAcquireAsync(
            "owner-a", Revision("config-a"), TimeSpan.FromSeconds(30));

        (await lease.TryReleaseAsync(first.Grant!)).Should().BeTrue();
        var unowned = await lease.GetStateAsync();
        unowned.HasOwnerTuple.Should().BeFalse();
        unowned.FenceToken.Should().Be(first.State.FenceToken);

        var second = await lease.TryAcquireAsync(
            "owner-b", Revision("config-b"), TimeSpan.FromSeconds(30));
        second.Acquired.Should().BeTrue();
        second.State.FenceToken.Should().Be(first.State.FenceToken + 1);
        (await lease.TryReleaseAsync(first.Grant!)).Should().BeFalse();
        (await lease.TryReleaseAsync(second.Grant!)).Should().BeTrue();
    }

    [Fact]
    public async Task Grant_is_opaque_and_database_persists_only_a_distinct_secret_hash()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();
        var configRevision = Revision("opaque-grant");

        var acquired = await lease.TryAcquireAsync(
            "owner-secret", configRevision.ToUpperInvariant(), TimeSpan.FromSeconds(30));

        acquired.Acquired.Should().BeTrue();
        acquired.State.ConfigRevisionSha256.Should().Be(configRevision,
            "configuration digests are stored in canonical lowercase form");
        acquired.Grant!.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Should().Equal(nameof(FdcRuntimeLeaseGrant.Authority));
        typeof(FdcRuntimeLeaseState).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("Secret", StringComparison.Ordinal));

        var persistedHash = database.ScalarString(
            "SELECT LEASE_SECRET_HASH FROM FDC_RUNTIME_OWNERSHIP WHERE LEASE_SCOPE='GLOBAL';");
        persistedHash.Should().MatchRegex("^[0-9a-f]{64}$");
        persistedHash.Should().NotBe(configRevision);
        (await lease.TryReleaseAsync(acquired.Grant)).Should().BeTrue();
    }

    [Fact]
    public async Task Forged_public_grant_without_issued_secret_is_rejected_before_database_access()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();
        var forged = new ForgedGrant(new FdcRuntimeAuthority(
            "owner-forged", 1, Revision("forged"), DateTime.UtcNow.AddSeconds(30)));

        Func<Task> renew = async () =>
            _ = await lease.TryRenewAsync(forged, TimeSpan.FromSeconds(30));
        Func<Task> release = async () =>
            _ = await lease.TryReleaseAsync(forged);

        await renew.Should().ThrowAsync<ArgumentException>().WithParameterName("grant");
        await release.Should().ThrowAsync<ArgumentException>().WithParameterName("grant");
    }

    [Fact]
    public async Task Configuration_revision_requires_a_64_character_sha256_hex_digest()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();

        Func<Task> shortRevision = async () =>
            _ = await lease.TryAcquireAsync("owner-invalid", "not-a-digest", TimeSpan.FromSeconds(30));
        Func<Task> nonHexRevision = async () =>
            _ = await lease.TryAcquireAsync("owner-invalid", new string('z', 64), TimeSpan.FromSeconds(30));

        await shortRevision.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("configRevisionSha256");
        await nonHexRevision.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("configRevisionSha256");
    }

    [Fact]
    public async Task Lease_duration_is_ceiled_once_to_integer_milliseconds_for_sqlite()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();
        var duration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1);

        var acquired = await lease.TryAcquireAsync("owner-duration", Revision("duration"), duration);

        acquired.Acquired.Should().BeTrue();
        (acquired.State.LeaseExpiresAt!.Value - acquired.State.HeartbeatAt!.Value)
            .Should().Be(TimeSpan.FromMilliseconds(1_001));
        (await lease.TryReleaseAsync(acquired.Grant!)).Should().BeTrue();
    }

    [Fact]
    public async Task SQLite_guards_reject_counter_reuse_tuple_swap_forged_time_and_row_deletion()
    {
        await using var database = TestDatabase.Create();
        var lease = database.CreateLease();
        var acquired = await lease.TryAcquireAsync(
            "owner-guard", Revision("guard"), TimeSpan.FromSeconds(30));

        Action decrementFence = () => database.Execute(
            $"UPDATE FDC_RUNTIME_OWNERSHIP SET FENCE_TOKEN={acquired.State.FenceToken - 1} WHERE LEASE_SCOPE='GLOBAL';");
        decrementFence.Should().Throw<SqliteException>();

        Action swapOwnerWithoutFence = () => database.Execute(
            "UPDATE FDC_RUNTIME_OWNERSHIP SET OWNER_ID='owner-bypass' WHERE LEASE_SCOPE='GLOBAL';");
        swapOwnerWithoutFence.Should().Throw<SqliteException>();

        var persistedHash = database.ScalarString(
            "SELECT LEASE_SECRET_HASH FROM FDC_RUNTIME_OWNERSHIP WHERE LEASE_SCOPE='GLOBAL';");
        var differentHash = (persistedHash[0] == 'a' ? 'b' : 'a') + persistedHash[1..];
        Action swapSecretWithoutFence = () => database.Execute(
            $"UPDATE FDC_RUNTIME_OWNERSHIP SET LEASE_SECRET_HASH='{differentHash}' WHERE LEASE_SCOPE='GLOBAL';");
        swapSecretWithoutFence.Should().Throw<SqliteException>();

        Action forgeFutureHeartbeat = () => database.Execute("""
            UPDATE FDC_RUNTIME_OWNERSHIP
               SET HEARTBEAT_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '+1 hour'),
                   LEASE_EXPIRES_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '+2 hours')
             WHERE LEASE_SCOPE='GLOBAL';
            """);
        forgeFutureHeartbeat.Should().Throw<SqliteException>();

        Action exceedMaximumLease = () => database.Execute("""
            UPDATE FDC_RUNTIME_OWNERSHIP
               SET HEARTBEAT_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now'),
                   LEASE_EXPIRES_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '+2 days')
             WHERE LEASE_SCOPE='GLOBAL';
            """);
        exceedMaximumLease.Should().Throw<SqliteException>();

        Action deleteCounter = () => database.Execute(
            "DELETE FROM FDC_RUNTIME_OWNERSHIP WHERE LEASE_SCOPE='GLOBAL';");
        deleteCounter.Should().Throw<SqliteException>();

        (await lease.TryReleaseAsync(acquired.Grant!)).Should().BeTrue();
    }

    private static string Revision(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class ForgedGrant : FdcRuntimeLeaseGrant
    {
        public ForgedGrant(FdcRuntimeAuthority authority) : base(authority) { }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string path, string connectionString)
        {
            Path = path;
            ConnectionString = connectionString;
        }

        private string Path { get; }
        private string ConnectionString { get; }

        public static TestDatabase Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nexa-fdc-lease-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path};Foreign Keys=False;Pooling=False;Default Timeout=30";
            SqliteSchemaInitializer.Apply(connectionString);
            return new TestDatabase(path, connectionString);
        }

        public FdcRuntimeLease CreateLease() => new(new EesDataSource
        {
            Provider = new SqliteProvider(),
            ConnectionString = ConnectionString,
        });

        public void Execute(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public string ScalarString(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
                   ?? string.Empty;
        }

        public ValueTask DisposeAsync()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
