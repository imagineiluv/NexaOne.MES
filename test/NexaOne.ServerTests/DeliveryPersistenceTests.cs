using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Collaboration;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>Real SQLite acceptance of the product-owned durable delivery adapter.</summary>
public sealed class DeliveryPersistenceTests
    : IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private static readonly string[] Grants =
    [
        "delivery.manage-template", "delivery.manage-profile", "delivery.queue",
        "delivery.read", "delivery.cancel", "delivery.manage-dead-letter"
    ];
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly BillingBridge _bridge;
    private readonly BusinessMembershipBridge _memberships;

    public DeliveryPersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _bridge = NewBridge();
        _memberships = new(DataSource());
        Execute("""
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('DELIVERY-MEMBER','Delivery member','', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('delivery-user','Delivery owner','','','DELIVERY-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('delivery-reader','Delivery reader','','','DELIVERY-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }

    public async Task InitializeAsync()
    {
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "delivery-user",
            new(0, true, Grants))).IsSuccess.Should().BeTrue();
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "delivery-reader",
            new(0, true, ["delivery.read"]))).IsSuccess.Should().BeTrue();
    }

    public Task DisposeAsync() { File.Delete(_path); return Task.CompletedTask; }
    private EesDataSource DataSource() => new()
        { Provider = new SqliteProvider(), ConnectionString = _connectionString };
    private BillingBridge NewBridge(TimeProvider? clock = null) => new(
        DataSource(), new BusinessMembershipBridge(DataSource()), new BusinessMasterDirectory(DataSource()), clock);
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
    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection.ExecuteScalar<T>(sql)!;
    }
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);

    [Fact]
    public async Task Queue_replay_persists_rendered_payload_and_cancel_across_bridge_recreation()
    {
        var template = await _bridge.CreateTemplateAsync("delivery-user", _tenant, _organization,
            "Invoice", "Invoice {{number}}", "Hello {{customer}}", ["number", "customer"]);
        var profile = await _bridge.CreateProfileAsync("delivery-user", _tenant, _organization,
            "Primary email", "smtp", "secret://mail/primary", new(3, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)));
        var operation = Guid.NewGuid();
        var input = new DeliveryQueueInput(template.Id, profile.Id, "buyer@example.com",
            [new("number", "INV-42"), new("customer", "Ada")]);

        var queued = await _bridge.QueueAsync("delivery-user", _tenant, _organization, operation, input);
        var replay = await NewBridge().QueueAsync("delivery-user", _tenant, _organization, operation, input);

        replay.Should().BeEquivalentTo(queued, options => options.WithStrictOrdering());
        queued.State.Should().Be(DeliveryState.Pending);
        queued.Payload.Subject.Should().Be("Invoice INV-42");
        queued.Payload.Body.Should().Be("Hello Ada");
        queued.Payload.CredentialReference.Should().Be("secret://mail/primary");
        (await NewBridge().GetAsync("delivery-reader", _tenant, _organization, queued.Id))
            .Should().BeEquivalentTo(queued, options => options.WithStrictOrdering());
        await Error(() => NewBridge().QueueAsync("delivery-user", _tenant, _organization, operation,
            input with { Recipient = "other@example.com" }), "DELIVERY_OPERATION_CONFLICT");

        var cancelled = await NewBridge().CancelAsync("delivery-user", _tenant, _organization,
            queued.Id, queued.Version);
        cancelled.State.Should().Be(DeliveryState.Cancelled);
        (await NewBridge().GetAsync("delivery-reader", _tenant, _organization, queued.Id))
            .Should().BeEquivalentTo(cancelled, options => options.WithStrictOrdering());
        Count("COL_DELIVERY_TEMPLATE").Should().Be(1);
        Count("COL_DELIVERY_PROFILE").Should().Be(1);
        Count("COL_DELIVERY_REQUEST").Should().Be(1);
    }

    [Fact]
    public async Task Permissions_scope_and_deactivation_fail_closed()
    {
        await Error(() => _bridge.CreateTemplateAsync("delivery-reader", _tenant, _organization,
            "Denied", "Subject", "Body"), "BUSINESS_ACCESS_DENIED");
        await Error(() => _bridge.GetAsync("delivery-user", _tenant, Guid.NewGuid(), Guid.NewGuid()),
            "BUSINESS_ACCESS_DENIED");

        var template = await _bridge.CreateTemplateAsync("delivery-user", _tenant, _organization,
            "Notice", "Subject", "Body");
        var profile = await _bridge.CreateProfileAsync("delivery-user", _tenant, _organization,
            "Mail", "smtp", "secret://mail/primary", new(2, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));
        var inactive = await _bridge.DeactivateProfileAsync("delivery-user", _tenant, _organization,
            profile.Id, profile.Version);
        inactive.Active.Should().BeFalse();
        await Error(() => _bridge.QueueAsync("delivery-user", _tenant, _organization, Guid.NewGuid(),
            new(template.Id, inactive.Id, "buyer@example.com")), "DELIVERY_PROFILE_INACTIVE");
    }

    [Fact]
    public async Task Service_principal_claim_settlement_and_live_scope_revocation_are_persisted()
    {
        IDeliveryAutomationBridge automation = _bridge;
        var principal = await automation.SavePrincipalAsync(
            "admin", "mail-dispatch", new(0, "Mail dispatcher", true));
        principal.IsSuccess.Should().BeTrue();
        var grant = await automation.SaveScopeAsync(
            "admin", "mail-dispatch", _tenant, _organization, new(0, true));
        grant.IsSuccess.Should().BeTrue();
        (await automation.ListActiveScopesAsync("mail-dispatch")).Items.Single()
            .Should().BeEquivalentTo(grant.Value);

        var template = await _bridge.CreateTemplateAsync("delivery-user", _tenant, _organization,
            "Notice", "Subject", "Body");
        var profile = await _bridge.CreateProfileAsync("delivery-user", _tenant, _organization,
            "Mail", "smtp", "mail-primary", new(2, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));
        var queued = await _bridge.QueueAsync("delivery-user", _tenant, _organization, Guid.NewGuid(),
            new(template.Id, profile.Id, "buyer@example.com"));

        var claimed = (await automation.ClaimDueAsync(
            "mail-dispatch", _tenant, _organization, grant.Value.Version, 10, TimeSpan.FromMinutes(1))).Single();
        claimed.Id.Should().Be(queued.Id);
        claimed.State.Should().Be(DeliveryState.Leased);
        claimed.LeaseId.Should().NotBeNull();
        var delivered = await automation.CompleteAsync(
            "mail-dispatch", _tenant, _organization, grant.Value.Version,
            claimed.Id, claimed.Version, claimed.LeaseId!.Value, "smtp:accepted");
        delivered.State.Should().Be(DeliveryState.Delivered);
        delivered.ProviderReceipt.Should().Be("smtp:accepted");

        var actor = principal.Value.AuditActorId.ToString("D");
        Scalar<string>("SELECT USER_ID FROM ERP_BILLING_AUDIT WHERE RESOURCE_TYPE='delivery' AND OPERATION='delivered'")
            .Should().Be(actor);
        Scalar<long>("SELECT COUNT(*) FROM SYS_USER WHERE USER_ID='svc.del.mail-dispatch' AND IS_ACTIVE=0")
            .Should().Be(1, "the compatibility identity must never be login-enabled");
        Scalar<long>("SELECT COUNT(*) FROM SYS_BUSINESS_MEMBERSHIP WHERE USER_ID='svc.del.mail-dispatch'")
            .Should().Be(0, "delivery authority comes only from its dedicated scope grant");

        var afterClaim = await _bridge.QueueAsync("delivery-user", _tenant, _organization, Guid.NewGuid(),
            new(template.Id, profile.Id, "revoked@example.com"));
        var revokedLease = (await automation.ClaimDueAsync(
            "mail-dispatch", _tenant, _organization, grant.Value.Version, 10, TimeSpan.FromMinutes(1))).Single();
        revokedLease.Id.Should().Be(afterClaim.Id);

        var revoked = await automation.SaveScopeAsync(
            "admin", "mail-dispatch", _tenant, _organization, new(grant.Value.Version, false));
        revoked.Value.IsActive.Should().BeFalse();
        (await automation.ListActiveScopesAsync("mail-dispatch")).Total.Should().Be(0);
        await Error(() => automation.ClaimDueAsync(
            "mail-dispatch", _tenant, _organization, grant.Value.Version, 10, TimeSpan.FromMinutes(1)),
            "BUSINESS_ACCESS_DENIED");
        await Error(() => automation.CompleteAsync(
            "mail-dispatch", _tenant, _organization, grant.Value.Version,
            revokedLease.Id, revokedLease.Version, revokedLease.LeaseId!.Value, "smtp:must-not-settle"),
            "BUSINESS_ACCESS_DENIED");
        Count("COL_DELIVERY_SERVICE_PRINCIPAL_AUDIT").Should().Be(1);
        Count("COL_DELIVERY_SERVICE_SCOPE_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Restart_recovers_retry_and_expired_final_lease_without_losing_receipt_or_error_code()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        var bridge = NewBridge(clock);
        IDeliveryAutomationBridge automation = bridge;
        (await automation.SavePrincipalAsync("admin", "mail-recovery", new(0, "Mail recovery", true)))
            .IsSuccess.Should().BeTrue();
        var scope = (await automation.SaveScopeAsync(
            "admin", "mail-recovery", _tenant, _organization, new(0, true))).Value;
        var template = await bridge.CreateTemplateAsync("delivery-user", _tenant, _organization,
            "Recovery", "Subject", "Body");
        var retryProfile = await bridge.CreateProfileAsync("delivery-user", _tenant, _organization,
            "Retry", "smtp", "mail-primary", new(2, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));
        var retry = await bridge.QueueAsync("delivery-user", _tenant, _organization, Guid.NewGuid(),
            new(template.Id, retryProfile.Id, "retry@example.com"));
        var firstLease = (await automation.ClaimDueAsync(
            "mail-recovery", _tenant, _organization, scope.Version, 10, TimeSpan.FromMinutes(1))).Single();
        var failed = await automation.FailAsync(
            "mail-recovery", _tenant, _organization, scope.Version,
            retry.Id, firstLease.Version, firstLease.LeaseId!.Value, "DELIVERY_PROVIDER_SEND_FAILED");
        failed.State.Should().Be(DeliveryState.RetryScheduled);
        failed.LastErrorCode.Should().Be("DELIVERY_PROVIDER_SEND_FAILED");

        clock.Advance(TimeSpan.FromSeconds(10));
        IDeliveryAutomationBridge restarted = NewBridge(clock);
        var secondLease = (await restarted.ClaimDueAsync(
            "mail-recovery", _tenant, _organization, scope.Version, 10, TimeSpan.FromMinutes(1))).Single();
        secondLease.AttemptCount.Should().Be(2);
        var delivered = await restarted.CompleteAsync(
            "mail-recovery", _tenant, _organization, scope.Version,
            retry.Id, secondLease.Version, secondLease.LeaseId!.Value, "smtp:restart-accepted");
        delivered.ProviderReceipt.Should().Be("smtp:restart-accepted");

        var finalProfile = await bridge.CreateProfileAsync("delivery-user", _tenant, _organization,
            "Final", "smtp", "mail-primary", new(1, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));
        var abandoned = await bridge.QueueAsync("delivery-user", _tenant, _organization, Guid.NewGuid(),
            new(template.Id, finalProfile.Id, "expired@example.com"));
        var abandonedLease = (await restarted.ClaimDueAsync(
            "mail-recovery", _tenant, _organization, scope.Version, 10, TimeSpan.FromMinutes(1))).Single();
        abandonedLease.Id.Should().Be(abandoned.Id);

        clock.Advance(TimeSpan.FromMinutes(1));
        (await ((IDeliveryAutomationBridge)NewBridge(clock)).ClaimDueAsync(
            "mail-recovery", _tenant, _organization, scope.Version, 10, TimeSpan.FromMinutes(1)))
            .Should().BeEmpty("an expired final-attempt lease is dead-lettered rather than dispatched again");
        var dead = await NewBridge(clock).GetAsync(
            "delivery-user", _tenant, _organization, abandoned.Id);
        dead.State.Should().Be(DeliveryState.DeadLetter);
        dead.LastErrorCode.Should().Be("DELIVERY_LEASE_EXPIRED");
        dead.LastLeaseId.Should().Be(abandonedLease.LeaseId);
    }

    [Fact]
    public async Task Dead_letter_list_retry_and_discard_are_scoped_paged_and_operation_idempotent()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 24, 5, 0, 0, TimeSpan.Zero));
        var bridge = NewBridge(clock);
        IDeliveryAutomationBridge automation = bridge;
        (await automation.SavePrincipalAsync("admin", "mail-operator", new(0, "Mail operator", true)))
            .IsSuccess.Should().BeTrue();
        var scope = (await automation.SaveScopeAsync(
            "admin", "mail-operator", _tenant, _organization, new(0, true))).Value;
        var template = await bridge.CreateTemplateAsync("delivery-user", _tenant, _organization,
            "Operations", "Subject", "Body");
        var profile = await bridge.CreateProfileAsync("delivery-user", _tenant, _organization,
            "One attempt", "smtp", "mail-primary", new(1, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));
        var requests = new List<DeliveryRequest>();
        foreach (var recipient in new[] { "first@example.com", "second@example.com" })
        {
            var queued = await bridge.QueueAsync("delivery-user", _tenant, _organization, Guid.NewGuid(),
                new(template.Id, profile.Id, recipient));
            var lease = (await automation.ClaimDueAsync(
                "mail-operator", _tenant, _organization, scope.Version, 1, TimeSpan.FromMinutes(1))).Single();
            lease.Id.Should().Be(queued.Id);
            requests.Add(await automation.FailAsync(
                "mail-operator", _tenant, _organization, scope.Version,
                lease.Id, lease.Version, lease.LeaseId!.Value, "DELIVERY_PROVIDER_SEND_FAILED"));
        }

        var ordered = requests.OrderBy(value => value.Id).ToArray();
        var templates = await bridge.ListTemplatesAsync("delivery-reader", _tenant, _organization);
        templates.Total.Should().Be(1);
        templates.Items.Single().Id.Should().Be(template.Id);
        var profiles = await bridge.ListProfilesAsync("delivery-reader", _tenant, _organization);
        profiles.Total.Should().Be(1);
        profiles.Items.Single().Id.Should().Be(profile.Id);
        var requestPage = await bridge.ListRequestsAsync("delivery-reader", _tenant, _organization, 0, 1);
        requestPage.Total.Should().Be(2);
        requestPage.Items.Single().Id.Should().Be(ordered[0].Id);
        (await bridge.ListRequestsAsync("delivery-reader", _tenant, _organization, 1, 1))
            .Items.Single().Id.Should().Be(ordered[1].Id);
        var firstPage = await bridge.ListDeadLettersAsync(
            "delivery-reader", _tenant, _organization, 0, 1);
        firstPage.Total.Should().Be(2);
        firstPage.Items.Single().Id.Should().Be(ordered[0].Id);
        (await bridge.ListDeadLettersAsync("delivery-reader", _tenant, _organization, 1, 1))
            .Items.Single().Id.Should().Be(ordered[1].Id);
        await Error(() => bridge.RetryDeadLetterAsync("delivery-reader", _tenant, _organization,
            Guid.NewGuid(), ordered[0].Id, ordered[0].Version), "BUSINESS_ACCESS_DENIED");

        var retryOperation = Guid.NewGuid();
        var retried = await bridge.RetryDeadLetterAsync("delivery-user", _tenant, _organization,
            retryOperation, ordered[0].Id, ordered[0].Version);
        retried.State.Should().Be(DeliveryState.Pending);
        retried.AttemptCount.Should().Be(0);
        retried.LastErrorCode.Should().BeNull();
        var claimed = (await automation.ClaimDueAsync(
            "mail-operator", _tenant, _organization, scope.Version, 1, TimeSpan.FromMinutes(1))).Single();
        claimed.Id.Should().Be(retried.Id);
        var replay = await NewBridge(clock).RetryDeadLetterAsync("delivery-user", _tenant, _organization,
            retryOperation, ordered[0].Id, ordered[0].Version);
        replay.Version.Should().Be(claimed.Version, "the operation must not reset a request already reclaimed by the worker");
        replay.State.Should().Be(DeliveryState.Leased);
        await Error(() => bridge.DiscardDeadLetterAsync("delivery-user", _tenant, _organization,
            retryOperation, ordered[0].Id, ordered[0].Version), "DELIVERY_OPERATION_CONFLICT");

        var discardOperation = Guid.NewGuid();
        var discarded = await bridge.DiscardDeadLetterAsync("delivery-user", _tenant, _organization,
            discardOperation, ordered[1].Id, ordered[1].Version);
        discarded.State.Should().Be(DeliveryState.Cancelled);
        discarded.LastErrorCode.Should().Be("DELIVERY_PROVIDER_SEND_FAILED");
        (await NewBridge(clock).DiscardDeadLetterAsync("delivery-user", _tenant, _organization,
            discardOperation, ordered[1].Id, ordered[1].Version)).Version.Should().Be(discarded.Version);
        (await bridge.ListDeadLettersAsync("delivery-reader", _tenant, _organization)).Total.Should().Be(0);
        Count("COL_DELIVERY_DEAD_LETTER_OPERATION").Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_AUDIT WHERE RESOURCE_TYPE='delivery' "
            + "AND OPERATION IN ('dead-letter-retried','dead-letter-discarded')").Should().Be(2);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
