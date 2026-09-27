using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SHP.Domain;
using NexaOne.SHP.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class ShpDeliveryOrderTransitionPersistenceTests
    : IClassFixture<ShpDeliveryOrderTransitionPersistenceTests.ShipmentFactory>
{
    private readonly ShipmentFactory _factory;

    public ShpDeliveryOrderTransitionPersistenceTests(ShipmentFactory factory) => _factory = factory;

    public sealed class ShipmentFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-shp-cas-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "shp-cas-e2e-jwt-secret-key-32bytes+!!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-shp-cas-test");
            builder.UseSetting("Jwt:Audience", "nexaone-shp-cas-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    private DeliveryOrderRepository Repository(bool outboxEnabled)
    {
        _ = _factory.CreateClient();
        var dataSource = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Events:Outbox:Enabled"] = outboxEnabled ? "true" : "false",
            }).Build();
        return new DeliveryOrderRepository(dataSource, configuration);
    }

    private long CountOutbox(string orderId)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM EES_OUTBOX WHERE AGGREGATE_ID = @id";
        command.Parameters.AddWithValue("@id", orderId);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task Stale_cancellation_cannot_overwrite_confirmation_or_emit_outbox_event()
    {
        var repo = Repository(outboxEnabled: true);
        var id = $"SHPCAS_{Guid.NewGuid():N}";
        var draft = DeliveryOrder.Create(id, "Customer", "PLANT01", DateTime.UtcNow).Value;
        await repo.AddAsync(draft);

        var confirmation = (await repo.GetByIdAsync(id))!;
        var cancellation = (await repo.GetByIdAsync(id))!;
        confirmation.Confirm().IsSuccess.Should().BeTrue();
        cancellation.Cancel().IsSuccess.Should().BeTrue();

        (await repo.TryUpdateAsync(confirmation, DeliveryOrderStatus.Draft)).Should().BeTrue();
        (await repo.TryUpdateAsync(cancellation, DeliveryOrderStatus.Draft)).Should().BeFalse();

        (await repo.GetByIdAsync(id))!.Status.Should().Be(DeliveryOrderStatus.Confirmed);
        CountOutbox(id).Should().Be(1, "only the winning transition may emit an event");
        confirmation.DomainEvents.Should().BeEmpty();
        cancellation.DomainEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Stale_shipment_cannot_overwrite_cancellation_when_outbox_disabled()
    {
        var repo = Repository(outboxEnabled: false);
        var id = $"SHPCAS_{Guid.NewGuid():N}";
        await repo.AddAsync(DeliveryOrder.Create(id, "Customer", "PLANT01", DateTime.UtcNow).Value);

        var shipment = (await repo.GetByIdAsync(id))!;
        var cancellation = (await repo.GetByIdAsync(id))!;
        shipment.Confirm().IsSuccess.Should().BeTrue();
        cancellation.Confirm().IsSuccess.Should().BeTrue();
        (await repo.TryUpdateAsync(shipment, DeliveryOrderStatus.Draft)).Should().BeTrue();

        var shippedDate = DateTime.UtcNow;
        shipment.Ship(shippedDate).IsSuccess.Should().BeTrue();
        cancellation.Cancel().IsSuccess.Should().BeTrue();
        (await repo.TryUpdateAsync(cancellation, DeliveryOrderStatus.Confirmed)).Should().BeTrue();
        (await repo.TryUpdateAsync(shipment, DeliveryOrderStatus.Confirmed)).Should().BeFalse();

        var persisted = (await repo.GetByIdAsync(id))!;
        persisted.Status.Should().Be(DeliveryOrderStatus.Cancelled);
        persisted.ShippedDate.Should().BeNull();
        CountOutbox(id).Should().Be(0);
    }
}
