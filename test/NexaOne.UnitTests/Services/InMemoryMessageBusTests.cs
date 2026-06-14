using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.API.Services;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Services;

/// <summary>ADR-002 — 브로커리스(인메모리) Event Bus. 발행→구독 인프로세스 전달과,
/// Outbox 디스패처가 인메모리 버스를 백본으로 end-to-end 동작하는지(브로커 없이) 검증한다.</summary>
public sealed class InMemoryMessageBusTests
{
    [Fact]
    public async Task Publish_delivers_message_to_subscriber()
    {
        var bus = new InMemoryMessageBus();
        var received = new List<DomainEventMessage>();
        bus.Subscribe((m, _) => { received.Add(m); return Task.CompletedTask; });

        await bus.PublishAsync("t", DomainEventMessage.Create("EquipmentStateChanged", "FDC", "EQ-1", "Running"));

        received.Should().ContainSingle();
        received[0].EventType.Should().Be("EquipmentStateChanged");
        received[0].AggregateId.Should().Be("EQ-1");
        received[0].Payload.Should().Be("Running");
    }

    [Fact]
    public async Task Publish_fans_out_to_all_subscribers()
    {
        var bus = new InMemoryMessageBus();
        var a = 0; var b = 0;
        bus.Subscribe((_, _) => { a++; return Task.CompletedTask; });
        bus.Subscribe((_, _) => { b++; return Task.CompletedTask; });

        await bus.PublishAsync("t", DomainEventMessage.Create("E", "M", "1", "p"));

        a.Should().Be(1);
        b.Should().Be(1, "모든 인프로세스 구독자에게 전달돼야 한다");
    }

    [Fact]
    public async Task Failing_subscriber_is_isolated_and_publish_does_not_throw()
    {
        var bus = new InMemoryMessageBus(NullLogger<InMemoryMessageBus>.Instance);
        var reachedSecond = false;
        bus.Subscribe((_, _) => throw new InvalidOperationException("subscriber down"));
        bus.Subscribe((_, _) => { reachedSecond = true; return Task.CompletedTask; });

        var act = () => bus.PublishAsync("t", DomainEventMessage.Create("E", "M", "1", "p"));

        await act.Should().NotThrowAsync("한 구독자의 실패가 발행/다른 구독자를 막지 않아야 한다");
        reachedSecond.Should().BeTrue("앞 구독자가 실패해도 뒤 구독자는 전달받아야 한다");
    }

    [Fact]
    public async Task Dispatcher_over_inmemory_bus_delivers_to_subscriber_end_to_end()
    {
        // 브로커 없는 end-to-end: 디스패처가 미발행 outbox를 인메모리 버스로 발행 → 구독자 수신 + published 표시.
        var bus = new InMemoryMessageBus();
        var delivered = new List<DomainEventMessage>();
        bus.Subscribe((m, _) => { delivered.Add(m); return Task.CompletedTask; });

        var repo = new Mock<IOutboxRepository>();
        repo.Setup(r => r.GetUnpublishedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new OutboxMessage { Id = 1, EventType = "EquipmentStateChanged", Module = "FDC", AggregateId = "EQ-1", Payload = "Running", OccurredAt = DateTimeOffset.UnixEpoch.UtcDateTime },
                new OutboxMessage { Id = 2, EventType = "AlarmRaised", Module = "EST", AggregateId = "EQ-2", Payload = "High", OccurredAt = DateTimeOffset.UnixEpoch.UtcDateTime },
            });

        var published = await OutboxDispatcherService.DispatchBatchAsync(
            repo.Object, bus, "nexaone.events", 100, 10, NullLogger.Instance, default);

        published.Should().Be(2);
        delivered.Should().HaveCount(2, "디스패처가 인메모리 버스로 발행한 2건이 구독자에게 전달돼야 한다");
        delivered.Select(d => d.EventType).Should().BeEquivalentTo(new[] { "EquipmentStateChanged", "AlarmRaised" });
        repo.Verify(r => r.MarkPublishedAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.MarkPublishedAsync(2, It.IsAny<CancellationToken>()), Times.Once);
    }
}
