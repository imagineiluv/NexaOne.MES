using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.API.Services;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Services;

/// <summary>ADR-002 — Outbox 디스패처 코어(미발행 폴링 → 발행 → 표시, 실패 시 attempts 증가·계속)를 검증.</summary>
public sealed class OutboxDispatcherTests
{
    private static OutboxMessage Msg(long id, string eventType, int attempts = 0) => new()
    {
        Id = id, EventType = eventType, Module = "FDC", AggregateId = "EQ-1",
        Payload = "Running", OccurredAt = DateTimeOffset.UnixEpoch.UtcDateTime, Attempts = attempts
    };

    private const int MaxAttempts = 10;

    [Fact]
    public async Task DispatchBatch_publishes_then_marks_published()
    {
        var repo = new Mock<IOutboxRepository>();
        repo.Setup(r => r.GetUnpublishedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Msg(1, "EquipmentStateChanged"), Msg(2, "AlarmRaised") });
        var bus = new Mock<IMessageBus>();

        var published = await OutboxDispatcherService.DispatchBatchAsync(
            repo.Object, bus.Object, "nexaone.events", 100, MaxAttempts, NullLogger.Instance, default);

        published.Should().Be(2);
        bus.Verify(b => b.PublishAsync("nexaone.events", It.IsAny<DomainEventMessage>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        repo.Verify(r => r.MarkPublishedAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.MarkPublishedAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.MarkFailedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchBatch_marks_failed_on_publish_error_and_continues()
    {
        var repo = new Mock<IOutboxRepository>();
        repo.Setup(r => r.GetUnpublishedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Msg(1, "Good"), Msg(2, "Bad") });
        var bus = new Mock<IMessageBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(),
                It.Is<DomainEventMessage>(m => m.EventType == "Bad"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var published = await OutboxDispatcherService.DispatchBatchAsync(
            repo.Object, bus.Object, "t", 100, MaxAttempts, NullLogger.Instance, default);

        published.Should().Be(1, "성공분만 카운트하고 실패는 건너뛴다(루프 계속)");
        repo.Verify(r => r.MarkPublishedAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.MarkFailedAsync(2, It.IsAny<CancellationToken>()), Times.Once,
            "발행 실패 행은 attempts를 증가시켜 재시도 대상으로 남긴다");
    }

    [Fact]
    public async Task DispatchBatch_requests_only_messages_below_max_attempts()
    {
        // 디스패처는 maxAttempts를 리포지토리에 전달해 데드레터(한계 도달) 행이 폴링에서 제외되도록 한다.
        var repo = new Mock<IOutboxRepository>();
        repo.Setup(r => r.GetUnpublishedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OutboxMessage>());
        var bus = new Mock<IMessageBus>();

        await OutboxDispatcherService.DispatchBatchAsync(
            repo.Object, bus.Object, "t", 50, MaxAttempts, NullLogger.Instance, default);

        repo.Verify(r => r.GetUnpublishedAsync(50, MaxAttempts, It.IsAny<CancellationToken>()), Times.Once,
            "배치 크기와 함께 maxAttempts 임계값을 리포지토리에 전달해야 한다(데드레터 제외)");
    }

    [Fact]
    public async Task DispatchBatch_dead_letters_message_reaching_max_attempts()
    {
        // attempts가 한계 직전(maxAttempts-1)인 메시지가 또 실패하면 데드레터 경고를 남긴다(테스트는 동작 계속만 검증).
        var repo = new Mock<IOutboxRepository>();
        repo.Setup(r => r.GetUnpublishedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Msg(7, "Poison", attempts: MaxAttempts - 1) });
        var bus = new Mock<IMessageBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<DomainEventMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("still down"));

        var published = await OutboxDispatcherService.DispatchBatchAsync(
            repo.Object, bus.Object, "t", 100, MaxAttempts, NullLogger.Instance, default);

        published.Should().Be(0);
        repo.Verify(r => r.MarkFailedAsync(7, It.IsAny<CancellationToken>()), Times.Once,
            "한계 도달 시에도 attempts를 증가시켜 이후 폴링(ATTEMPTS<max)에서 제외되게 한다");
    }
}
