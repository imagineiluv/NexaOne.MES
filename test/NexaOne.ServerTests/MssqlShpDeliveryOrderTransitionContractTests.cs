using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NexaOne.SHP.Domain;
using NexaOne.SHP.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlShpDeliveryOrderTransitionContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Stale_transition_does_not_overwrite_order_or_append_outbox_event()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Events:Outbox:Enabled"] = "true" }).Build();
        var repository = new DeliveryOrderRepository(database.DataSource, configuration);
        var orderId = $"SHP_{Guid.NewGuid():N}";
        await repository.AddAsync(DeliveryOrder.Create(
            orderId, "Customer", "PLANT01", DateTime.UtcNow).Value);

        var confirmation = (await repository.GetByIdAsync(orderId))!;
        var cancellation = (await repository.GetByIdAsync(orderId))!;
        confirmation.Confirm().IsSuccess.Should().BeTrue();
        cancellation.Cancel().IsSuccess.Should().BeTrue();

        (await repository.TryUpdateAsync(confirmation, DeliveryOrderStatus.Draft, false)).Should().BeTrue();
        (await repository.TryUpdateAsync(cancellation, DeliveryOrderStatus.Draft, false)).Should().BeFalse();

        (await repository.GetByIdAsync(orderId))!.Status.Should().Be(DeliveryOrderStatus.Confirmed);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM EES_OUTBOX WHERE AGGREGATE_ID=@orderId",
            new { orderId })).Should().Be(1);
    }

    [Fact]
    public async Task Hold_guard_blocks_stale_shipment_and_release_allows_it()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Events:Outbox:Enabled"] = "true" }).Build();
        var repository = new DeliveryOrderRepository(database.DataSource, configuration);
        var orderId = $"SHPH_{Guid.NewGuid():N}";
        await repository.AddAsync(DeliveryOrder.Create(
            orderId, "Customer", "PLANT01", DateTime.UtcNow).Value);
        var confirmed = (await repository.GetByIdAsync(orderId))!;
        confirmed.Confirm().IsSuccess.Should().BeTrue();
        (await repository.TryUpdateAsync(confirmed, DeliveryOrderStatus.Draft, false)).Should().BeTrue();

        var held = (await repository.GetByIdAsync(orderId))!;
        var staleShipment = (await repository.GetByIdAsync(orderId))!;
        held.Hold().IsSuccess.Should().BeTrue();
        staleShipment.Ship(DateTime.UtcNow).IsSuccess.Should().BeTrue();
        (await repository.TryUpdateAsync(held, DeliveryOrderStatus.Confirmed, false)).Should().BeTrue();
        (await repository.TryUpdateAsync(staleShipment, DeliveryOrderStatus.Confirmed, false)).Should().BeFalse();
        (await repository.GetByIdAsync(orderId))!.IsHeld.Should().BeTrue();
        (await database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM EES_OUTBOX WHERE AGGREGATE_ID=@orderId",
            new { orderId })).Should().Be(2);

        var released = (await repository.GetByIdAsync(orderId))!;
        released.ReleaseHold().IsSuccess.Should().BeTrue();
        (await repository.TryUpdateAsync(released, DeliveryOrderStatus.Confirmed, true)).Should().BeTrue();
        var shipment = (await repository.GetByIdAsync(orderId))!;
        shipment.Ship(DateTime.UtcNow).IsSuccess.Should().BeTrue();
        (await repository.TryUpdateAsync(shipment, DeliveryOrderStatus.Confirmed, false)).Should().BeTrue();
        (await repository.GetByIdAsync(orderId))!.Status.Should().Be(DeliveryOrderStatus.Shipped);
    }
}
