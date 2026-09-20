using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Moq.Protected;
using NexaDB.Data.Sqlite;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

public sealed class BusinessMembershipDatabaseTemplate : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nexa-business-template-{Guid.NewGuid():N}.db");
    public BusinessMembershipDatabaseTemplate() => SqliteSchemaInitializer.EnsureSchema($"Data Source={_path};Pooling=False");
    public string Copy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-business-{Guid.NewGuid():N}.db");
        File.Copy(_path, path);
        return path;
    }
    public void Dispose() => File.Delete(_path);
}

public sealed class BusinessMembershipTests : IClassFixture<BusinessMembershipDatabaseTemplate>, IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;
    private readonly BusinessMembershipBridge _bridge;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();

    public BusinessMembershipTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _bridge = NewBridge();
        Execute("""
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
                CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('BUSINESS_MEMBER', 'Member', '', '', 0, 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID,
                CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('member', 'Member', '', '', 'BUSINESS_MEMBER',
                'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }

    private BusinessMembershipBridge NewBridge() => new(new EesDataSource
    {
        Provider = new SqliteProvider(), ConnectionString = _connectionString,
    });

    private Task<Result<BusinessMembership>> Save(long version = 0, bool active = true,
        string[]? permissions = null, string administrator = "admin", CancellationToken ct = default)
        => _bridge.SaveMembershipAsync(administrator, _tenant, _organization, "member",
            new BusinessMembershipChange(version, active, permissions ?? ["stock.read"]), ct);

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute(sql);
    }
    private long Count(string table)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM " + table);
    }

    [Fact]
    public async Task Persisted_identity_is_shared_across_scopes_and_survives_restart_and_reactivation()
    {
        var first = (await Save(permissions: ["stock.read", "equipment.read"])).Value;
        first.BusinessUserId.Should().NotBe(Guid.Empty);
        first.Permissions.Should().Equal("equipment.read", "stock.read");
        var other = (await _bridge.SaveMembershipAsync("admin", Guid.NewGuid(), Guid.NewGuid(), "member",
            new(0, true, ["stock.post"]))).Value;
        other.BusinessUserId.Should().Be(first.BusinessUserId);
        (await Save(1, false)).IsSuccess.Should().BeTrue();
        (await _bridge.GetAccessAsync("member", _tenant, _organization)).Should().BeNull();
        var revoked = (await _bridge.GetMembershipAsync("admin", _tenant, _organization, "member")).Value;
        revoked.IsActive.Should().BeFalse();
        revoked.Version.Should().Be(2);
        (await Save(2)).Value.BusinessUserId.Should().Be(first.BusinessUserId);

        SqliteSchemaInitializer.EnsureSchema(_connectionString);
        var restarted = await NewBridge().GetAccessAsync("member", _tenant, _organization);
        restarted!.BusinessUserId.Should().Be(first.BusinessUserId);
        restarted.Version.Should().Be(3);
        Count("SYS_BUSINESS_IDENTITY").Should().Be(1);
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(4);
    }

    [Fact]
    public async Task Access_batches_use_paired_canonical_keysets_across_the_128_row_boundary_without_writes()
    {
        await Save(active: false);
        (await _bridge.SaveMembershipAsync("admin", _tenant, _organization, "admin", new(0, true, ["stock.read"]))).IsSuccess.Should().BeTrue();
        var firstTenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var secondTenant = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var scopes = Enumerable.Range(1, 130).Select(index => new
        {
            Tenant = (index <= 129 ? firstTenant : secondTenant).ToString("D"),
            Organization = Guid.Parse($"00000000-0000-0000-0000-{(index <= 129 ? index : 1):D12}").ToString("D")
        }).ToArray();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await connection.ExecuteAsync("""
            INSERT INTO SYS_BUSINESS_MEMBERSHIP (TENANT_ID, ORGANIZATION_ID, USER_ID, IS_ACTIVE, PERMISSIONS,
                MEMBERSHIP_VERSION, UPDATED_BY, UPDATED_AT)
            VALUES (@Tenant, @Organization, 'member', 1, 'stock.read', 1, 'admin', CURRENT_TIMESTAMP)
            """, scopes, transaction);
        var writes = await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction);

        var first = await _bridge.ListAccessInTransactionAsync(transaction, "member");
        first.Should().HaveCount(128).And.OnlyContain(m => m.IsActive && m.UserId == "member");
        var second = await _bridge.ListAccessInTransactionAsync(transaction, "member", first[^1].TenantId, first[^1].OrganizationId);
        second.Should().HaveCount(2);
        first.Concat(second).Select(m => (m.TenantId.ToString("D"), m.OrganizationId.ToString("D")))
            .Should().Equal(scopes.Select(s => (s.Tenant, s.Organization)));
        (await _bridge.ListAccessInTransactionAsync(transaction, "member", second[^1].TenantId, second[^1].OrganizationId)).Should().BeEmpty();
        (await _bridge.ListAccessInTransactionAsync(transaction, "member", limit: 1)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(first[0]);
        ((IList<BusinessMembership>)first).IsReadOnly.Should().BeTrue();
        (await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction)).Should().Be(writes);
        transaction.Connection.Should().BeSameAs(connection);
        transaction.Rollback();
        Count("SYS_BUSINESS_MEMBERSHIP").Should().Be(2);
        Count("SYS_BUSINESS_IDENTITY").Should().Be(2);
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Access_batches_observe_uncommitted_grant_and_user_revocation_and_preserve_rollback()
    {
        var saved = (await Save()).Value;
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            var before = (await _bridge.ListAccessInTransactionAsync(transaction, "member")).Single();
            before.Should().BeEquivalentTo(saved);
            await connection.ExecuteAsync("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='equipment.read', MEMBERSHIP_VERSION=2 WHERE USER_ID='member'", transaction: transaction);
            var current = (await _bridge.ListAccessInTransactionAsync(transaction, "member")).Single();
            current.Permissions.Should().Equal("equipment.read"); current.Version.Should().Be(2);
            before.Permissions.Should().Equal("stock.read"); before.Version.Should().Be(1);
            foreach (var mutation in new[] { "UPDATE SYS_BUSINESS_MEMBERSHIP SET IS_ACTIVE=0 WHERE USER_ID='member'",
                "UPDATE SYS_USER SET IS_ACTIVE=0 WHERE USER_ID='member'", "UPDATE SYS_USER SET IS_DELETED=1 WHERE USER_ID='member'" })
            {
                await connection.ExecuteAsync(mutation, transaction: transaction);
                var writes = await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction);
                (await _bridge.ListAccessInTransactionAsync(transaction, "member")).Should().BeEmpty();
                (await _bridge.ListAccessInTransactionAsync(transaction, "missing-user")).Should().BeEmpty();
                (await _bridge.ListAccessInTransactionAsync(transaction, "admin")).Should().BeEmpty();
                (await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction)).Should().Be(writes);
                await connection.ExecuteAsync("""
                    UPDATE SYS_BUSINESS_MEMBERSHIP SET IS_ACTIVE=1 WHERE USER_ID='member';
                    UPDATE SYS_USER SET IS_ACTIVE=1, IS_DELETED=0 WHERE USER_ID='member';
                    """, transaction: transaction);
            }
            await connection.ExecuteAsync("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='*' WHERE USER_ID='member'", transaction: transaction);
            await Assert.ThrowsAsync<InvalidDataException>(() => _bridge.ListAccessInTransactionAsync(transaction, "member"));
            transaction.Rollback();
        }
        connection.State.Should().Be(ConnectionState.Open);
        (await _bridge.GetAccessAsync("member", _tenant, _organization)).Should().BeEquivalentTo(saved);
        Count("SYS_BUSINESS_IDENTITY").Should().Be(1);
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(1);
    }

    [Fact]
    public async Task Access_batches_validate_keys_paired_cursors_limits_and_live_transaction()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        foreach (var user in new[] { null!, "", " ", " member", "member ", new string('x', 51) })
            await Assert.ThrowsAsync<ArgumentException>(() => _bridge.ListAccessInTransactionAsync(transaction, user));
        foreach (var (tenant, organization) in new (Guid?, Guid?)[]
            { (_tenant, null), (null, _organization), (Guid.Empty, _organization), (_tenant, Guid.Empty), (Guid.Empty, Guid.Empty) })
            await Assert.ThrowsAsync<ArgumentException>(() => _bridge.ListAccessInTransactionAsync(transaction, "member", tenant, organization));
        foreach (var limit in new[] { 0, -1, 129 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _bridge.ListAccessInTransactionAsync(transaction, "member", limit: limit));
        (await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction)).Should().Be(0);
        transaction.Rollback();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _bridge.ListAccessInTransactionAsync(transaction, "member"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _bridge.ListAccessInTransactionAsync(null!, "member"));
    }

    [Theory]
    [InlineData(ConnectionState.Open, IsolationLevel.ReadCommitted)]
    [InlineData(ConnectionState.Open, IsolationLevel.RepeatableRead)]
    [InlineData(ConnectionState.Open, IsolationLevel.Snapshot)]
    [InlineData(ConnectionState.Closed, IsolationLevel.Serializable)]
    [InlineData(ConnectionState.Broken, IsolationLevel.Serializable)]
    public async Task Access_batches_reject_nonserializable_or_nonopen_transactions(ConnectionState state, IsolationLevel isolation)
    {
        var connection = new Mock<DbConnection>(MockBehavior.Strict);
        connection.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        using var connectionObject = connection.Object;
        connection.SetupGet(value => value.State).Returns(state);
        var transaction = new Mock<DbTransaction>(MockBehavior.Strict);
        transaction.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        using var transactionObject = transaction.Object;
        transaction.Protected().SetupGet<DbConnection>("DbConnection").Returns(connectionObject);
        transaction.SetupGet(value => value.IsolationLevel).Returns(isolation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _bridge.ListAccessInTransactionAsync(transactionObject, "member"));
        connection.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());
        transaction.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());
    }

    [Fact]
    public async Task Access_batches_honor_cancellation_before_touching_the_transaction()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var transaction = new Mock<DbTransaction>(MockBehavior.Strict);
        transaction.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        using var transactionObject = transaction.Object;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _bridge.ListAccessInTransactionAsync(transactionObject, "member", ct: cancellation.Token));
        error.CancellationToken.Should().Be(cancellation.Token);
        transaction.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task No_membership_is_inferred_from_global_admin_or_different_tenant_organization_user()
    {
        (await _bridge.GetAccessAsync("admin", _tenant, _organization)).Should().BeNull();
        await Save();
        (await _bridge.GetAccessAsync("member", Guid.NewGuid(), _organization)).Should().BeNull();
        (await _bridge.GetAccessAsync("member", _tenant, Guid.NewGuid())).Should().BeNull();
        (await _bridge.GetAccessAsync("admin", _tenant, _organization)).Should().BeNull();
        (await _bridge.GetAccessAsync("member", Guid.Empty, _organization)).Should().BeNull();
    }

    [Theory]
    [InlineData("IS_ACTIVE=0")]
    [InlineData("IS_DELETED=1")]
    public async Task User_revocation_is_observed_without_token_refresh(string mutation)
    {
        await Save();
        Execute($"UPDATE SYS_USER SET {mutation} WHERE USER_ID='member'");
        (await _bridge.GetAccessAsync("member", _tenant, _organization)).Should().BeNull();
        (await Save(1)).Error.Type.Should().Be(ErrorType.Validation);
        (await Save(1, false)).IsSuccess.Should().BeTrue("revocation remains possible for inactive users");
    }

    [Theory]
    [InlineData("UPDATE SYS_ROLE SET PERMISSIONS='' WHERE ROLE_ID='ADMIN'")]
    [InlineData("UPDATE SYS_ROLE SET IS_DELETED=1 WHERE ROLE_ID='ADMIN'")]
    [InlineData("UPDATE SYS_USER SET IS_ACTIVE=0 WHERE USER_ID='admin'")]
    [InlineData("UPDATE SYS_USER SET IS_DELETED=1 WHERE USER_ID='admin'")]
    public async Task Administrator_authority_is_checked_inside_the_write_transaction(string mutation)
    {
        Execute(mutation);
        await FluentActions.Awaiting(() => Save()).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Awaiting(() => _bridge.GetMembershipAsync("admin", _tenant, _organization, "member"))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        Count("SYS_BUSINESS_IDENTITY").Should().Be(0);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("sys:manage")]
    [InlineData("equipment.booking.*")]
    [InlineData("Stock.Read")]
    [InlineData("Stock.Product.Write")]
    [InlineData("stock.product.*")]
    [InlineData("stock.product.write|stock.read")]
    [InlineData("stock.read|stock.post")]
    [InlineData("")]
    public async Task Unsupported_permissions_are_rejected_before_persistence(string permission)
    {
        (await Save(permissions: [permission])).Error.Type.Should().Be(ErrorType.Validation);
        Count("SYS_BUSINESS_IDENTITY").Should().Be(0);
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(0);
    }

    [Fact]
    public async Task Product_enrollment_grant_is_explicit_persisted_and_revocable_independently_of_stock_read()
    {
        (await Save(permissions: ["stock.read"])).IsSuccess.Should().BeTrue();
        (await _bridge.GetAccessAsync("member", _tenant, _organization))!.Permissions.Should().Equal("stock.read");

        (await Save(1, permissions: ["stock.product.write"])).IsSuccess.Should().BeTrue();
        var restarted = NewBridge();
        (await restarted.GetAccessAsync("member", _tenant, _organization))!.Permissions.Should().Equal("stock.product.write");
        using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            (await restarted.GetAccessInTransactionAsync(transaction, "member", _tenant, _organization))!
                .Permissions.Should().Equal("stock.product.write");
            transaction.Rollback();
        }

        (await Save(2, permissions: ["stock.read"])).IsSuccess.Should().BeTrue();
        (await restarted.GetAccessAsync("member", _tenant, _organization))!.Permissions.Should().Equal("stock.read");
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(3);
    }

    [Fact]
    public async Task Empty_grants_are_valid_but_null_duplicate_and_overflow_inputs_are_rejected()
    {
        (await Save(permissions: ["stock.read", "stock.read"])).IsFailure.Should().BeTrue();
        (await _bridge.SaveMembershipAsync("admin", _tenant, _organization, "member", new(0, true, null!)))
            .IsFailure.Should().BeTrue();
        (await Save(long.MaxValue)).IsFailure.Should().BeTrue();
        (await Save(-1)).IsFailure.Should().BeTrue();
        (await Save(permissions: [])).Value.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_versions_and_retried_creates_have_no_additional_effects()
    {
        await Save();
        (await Save()).Error.Type.Should().Be(ErrorType.Conflict);
        (await Save(2)).Error.Type.Should().Be(ErrorType.Conflict);
        (await Save(1, permissions: ["stock.post"])).Value.Version.Should().Be(2);
        (await Save(1, false)).Error.Type.Should().Be(ErrorType.Conflict);
        (await _bridge.GetAccessAsync("member", _tenant, _organization))!.Permissions.Should().Equal("stock.post");
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_changes_commit_only_one_revision()
    {
        await Save();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Save(1, false))).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Count(result => result.IsFailure && result.Error.Type == ErrorType.Conflict).Should().Be(3);
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_new_identity_membership_and_revision()
    {
        Execute("""
            CREATE TRIGGER reject_business_audit BEFORE INSERT ON SYS_BUSINESS_MEMBERSHIP_AUDIT
            BEGIN SELECT RAISE(ABORT, 'injected audit failure'); END;
            """);
        await FluentActions.Awaiting(() => Save()).Should().ThrowAsync<SqliteException>();
        Count("SYS_BUSINESS_IDENTITY").Should().Be(0);
        Count("SYS_BUSINESS_MEMBERSHIP").Should().Be(0);
        Count("SYS_BUSINESS_MEMBERSHIP_AUDIT").Should().Be(0);
        Execute("DROP TRIGGER reject_business_audit");
        (await Save()).Value.Version.Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_write_cannot_create_identity_or_membership()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await FluentActions.Awaiting(() => Save(ct: cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
        Count("SYS_BUSINESS_IDENTITY").Should().Be(0);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("stock.read|stock.read")]
    [InlineData("stock.read|equipment.read")]
    public async Task Malformed_stored_grants_fail_closed(string permissions)
    {
        await Save();
        Execute($"UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='{permissions}'");
        await FluentActions.Awaiting(() => _bridge.GetAccessAsync("member", _tenant, _organization))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Incremental_bootstrap_creates_all_new_tables_with_constraints_and_no_default_grants()
    {
        Execute("DROP TABLE SYS_BUSINESS_MEMBERSHIP_AUDIT; DROP TABLE SYS_BUSINESS_MEMBERSHIP; DROP TABLE SYS_BUSINESS_IDENTITY;");
        SqliteSchemaInitializer.EnsureSchema(_connectionString);
        Count("SYS_BUSINESS_MEMBERSHIP").Should().Be(0);
        await Save();
        FluentActions.Invoking(() => Execute("UPDATE SYS_BUSINESS_MEMBERSHIP SET MEMBERSHIP_VERSION=0"))
            .Should().Throw<SqliteException>();
        FluentActions.Invoking(() => Execute("DELETE FROM SYS_BUSINESS_IDENTITY"))
            .Should().Throw<SqliteException>();
    }

    [Fact]
    public async Task Http_uses_authenticated_actor_and_live_grants_ignoring_plant_and_body_authority()
    {
        using var factory = new MembershipHttpFactory(_connectionString);
        using var client = factory.CreateClient();
        var route = $"/api/v1/sys/business-memberships/{_tenant}/{_organization}";
        (await client.GetAsync(route + "/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = new("Bearer", MembershipHttpFactory.Token("admin", "*"));
        (await client.GetAsync(route + "/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var created = await client.PutAsJsonAsync(route + "/users/member", new
        {
            expectedVersion = 0, isActive = true, permissions = new[] { "stock.read" },
            administratorId = "spoofed", businessUserId = Guid.Empty,
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            connection.ExecuteScalar<string>("SELECT CHANGED_BY FROM SYS_BUSINESS_MEMBERSHIP_AUDIT")
                .Should().Be("admin");
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", MembershipHttpFactory.Token("member", "*"));
        (await client.GetAsync(route + "/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(route + "/users/member", new BusinessMembershipChange(1, true, ["stock.post"])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "JWT permission alone cannot grant live SYS administration");
        await Save(1, false);
        (await client.GetAsync(route + "/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Authorization = new("Bearer", MembershipHttpFactory.Token("admin", "*"));
        Execute("UPDATE SYS_ROLE SET PERMISSIONS='' WHERE ROLE_ID='ADMIN'");
        (await client.GetAsync(route + "/users/member")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    public void Dispose() => File.Delete(_path);

    private sealed class MembershipHttpFactory(string connectionString) : WebApplicationFactory<Program>
    {
        private const string Secret = "business-membership-test-secret-32-bytes-minimum";
        private const string Issuer = "business-membership-test";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", connectionString);
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.ConfigureServices(services => services.AddSingleton<IBusinessMembershipBridge>(
                new BusinessMembershipBridge(new EesDataSource { Provider = new SqliteProvider(), ConnectionString = connectionString })));
        }
        public static string Token(string userId, string permission)
        {
            var token = new JwtSecurityToken(Issuer, Issuer,
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(Permissions.ClaimType, permission),
                 new Claim("plantId", "asserted-not-authoritative")],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}

[Collection(ChildProcessSmokeCollection.Name)]
[Trait("Category", "HostSmoke")]
public sealed class BusinessMembershipHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_SYS_module_resolves_bridge_and_persists_HTTP_grants()
    {
        using var host = await HostProcess.StartAsync(output, springConfig: null, expectListening: true);
        host.Listening.Should().BeTrue(host.Log);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = "admin", password = "admin", plantId = "untrusted" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.RootElement.GetProperty("accessToken").GetString());
        var route = $"/api/v1/sys/business-memberships/{Guid.NewGuid()}/{Guid.NewGuid()}";
        (await client.GetAsync(route + "/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var save = await client.PutAsJsonAsync(route + "/users/admin", new BusinessMembershipChange(0, true, ["equipment.read"]));
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        var membership = await (await client.GetAsync(route + "/me")).Content.ReadFromJsonAsync<BusinessMembership>();
        membership!.Version.Should().Be(1);
        membership.Permissions.Should().Equal("equipment.read");
    }
}

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class BusinessMembershipMssqlTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Actual_SQL_Server_preserves_identity_CAS_grants_and_audit_revisions()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var user = "business-" + Guid.NewGuid().ToString("N");
        await database.ExecuteAsync("""
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, UPDATED_BY)
            VALUES (@user, 'Business test', '', '', 'ADMIN', 'admin', 'admin')
            """, new { user });
        var bridge = new BusinessMembershipBridge(database.DataSource);
        var created = await bridge.SaveMembershipAsync("admin", tenant, organization, user, new(0, true, ["stock.read"]));
        created.IsSuccess.Should().BeTrue();
        (await bridge.SaveMembershipAsync("admin", tenant, organization, user, new(0, true, ["stock.post"])))
            .Error.Type.Should().Be(ErrorType.Conflict);
        var revoked = await bridge.SaveMembershipAsync("admin", tenant, organization, user, new(1, false, []));
        revoked.Value.BusinessUserId.Should().Be(created.Value.BusinessUserId);
        (await bridge.GetAccessAsync(user, tenant, organization)).Should().BeNull();
        (await new BusinessMembershipBridge(database.DataSource).GetMembershipAsync("admin", tenant, organization, user))
            .Value.Version.Should().Be(2);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM SYS_BUSINESS_MEMBERSHIP_AUDIT WHERE USER_ID=@user", new { user }))
            .Should().Be(2);
    }
}
