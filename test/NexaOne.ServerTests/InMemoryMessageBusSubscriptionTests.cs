using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Server.Realtime;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class InMemoryMessageBusSubscriptionTests
{
    [Fact]
    public async Task Disposing_subscription_removes_only_that_handler_and_is_idempotent()
    {
        var bus = new InMemoryMessageBus();
        var firstCalls = 0;
        var secondCalls = 0;
        var first = bus.Subscribe((_, _) =>
        {
            firstCalls++;
            return Task.CompletedTask;
        });
        using var second = bus.Subscribe((_, _) =>
        {
            secondCalls++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync("events", CreateMessage());
        first.Dispose();
        first.Dispose();
        await bus.PublishAsync("events", CreateMessage());

        firstCalls.Should().Be(1);
        secondCalls.Should().Be(2);
    }

    [Fact]
    public async Task Hosted_subscriber_unsubscribes_on_stop_and_can_restart_without_duplicates()
    {
        var bus = new InMemoryMessageBus();
        var notifier = new Mock<IEesHubNotifier>();
        notifier.Setup(candidate => candidate.NotifyDashboardRefreshAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(notifier.Object);
        using var provider = services.BuildServiceProvider();
        var service = new InMemoryBusSubscriberService(
            bus,
            provider.GetRequiredService<IServiceScopeFactory>());

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await bus.PublishAsync("events", CreateMessage());
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await bus.PublishAsync("events", CreateMessage());

        notifier.Verify(
            candidate => candidate.NotifyDashboardRefreshAsync(It.IsAny<CancellationToken>()),
            Times.Once);

        await service.StartAsync(CancellationToken.None);
        await bus.PublishAsync("events", CreateMessage());
        await service.StopAsync(CancellationToken.None);

        notifier.Verify(
            candidate => candidate.NotifyDashboardRefreshAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private static DomainEventMessage CreateMessage()
        => DomainEventMessage.Create("SubscriptionContractProbe", "TEST", "PROBE-1", "{}");
}
