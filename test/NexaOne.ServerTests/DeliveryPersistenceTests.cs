using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
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
        "delivery.read", "delivery.cancel"
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
    private BillingBridge NewBridge() => new(DataSource(), new BusinessMembershipBridge(DataSource()),
        new BusinessMasterDirectory(DataSource()));
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
}
