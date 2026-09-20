using System.Text.Json;
using Nexa.Components.Messaging.Kafka;
using KafkaConsumerOptions = NexaOne.Infrastructure.Messaging.KafkaConsumerOptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Server;
using NexaOne.Server.Realtime;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class KafkaRealtimeSubscriptionTests
{
    [Fact]
    public void Kafka_transport_composes_a_hosted_realtime_consumer_without_contacting_a_broker()
    {
        using var messageBus = new KafkaMessageBus("localhost:9092");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:GroupId"] = "nexaone-realtime-test",
            ["Events:Outbox:Topic"] = "nexaone.events.test",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEesHubNotifier>(Mock.Of<IEesHubNotifier>());
        services.AddSingleton<ScreenRefreshNotifier>();
        services.AddSingleton<RealtimeAlertFeed>();
        using var provider = services.BuildServiceProvider();

        var subscriber = NexaOneMesRuntimeState.CreateRealtimeSubscriber(
            messageBus,
            configuration,
            provider);

        subscriber.Should().BeOfType<KafkaConsumerService>();
    }

    [Fact]
    public void Kafka_transport_falls_back_to_safe_defaults_for_blank_group_and_topic()
    {
        using var messageBus = new KafkaMessageBus("localhost:9092");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:GroupId"] = "   ",
            ["Events:Outbox:Topic"] = "   ",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEesHubNotifier>(Mock.Of<IEesHubNotifier>());
        services.AddSingleton<ScreenRefreshNotifier>();
        services.AddSingleton<RealtimeAlertFeed>();
        using var provider = services.BuildServiceProvider();

        var subscriber = NexaOneMesRuntimeState.CreateRealtimeSubscriber(
            messageBus,
            configuration,
            provider);

        subscriber.Should().BeOfType<KafkaConsumerService>();
    }

    [Fact]
    public async Task Kafka_consumer_delivers_a_domain_event_and_stops_cleanly()
    {
        var expected = new DomainEventMessage
        {
            EventType = "EquipmentStateChanged",
            AggregateId = "EQ-17",
            Module = "FDC",
            Payload = "RUN",
            OccurredAt = new DateTime(2026, 7, 18, 3, 4, 5, DateTimeKind.Utc),
        };
        var consumeResult = new KafkaRecord("nexaone.events.test", expected.AggregateId,
            JsonSerializer.Serialize(expected), 0, 17);
        var consumeCount = 0;
        var consumer = new Mock<IKafkaConsumerSession>(MockBehavior.Loose);
        consumer.Setup(candidate => candidate.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref consumeCount) == 1) return consumeResult;
                Thread.Sleep(1);
                return null!;
            });
        var delivered = new TaskCompletionSource<DomainEventMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        consumer.Setup(candidate => candidate.Commit(consumeResult)).Callback(() => committed.TrySetResult());
        using var service = new KafkaConsumerService(
            (_, _) => consumer.Object,
            new KafkaConsumerOptions
            {
                GroupId = "nexaone-realtime-test",
                Topics = new[] { "nexaone.events.test" },
            },
            (message, _) =>
            {
                delivered.TrySetResult(message);
                return Task.CompletedTask;
            },
            NullLogger<KafkaConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        var actual = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        actual.Should().BeEquivalentTo(expected);
        consumer.Verify(candidate => candidate.Commit(consumeResult), Times.Once);
        consumer.Verify(candidate => candidate.Dispose(), Times.Once);
    }
}
