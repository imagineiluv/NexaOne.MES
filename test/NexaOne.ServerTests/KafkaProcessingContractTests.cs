using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nexa.Components.Messaging.Kafka;
using NexaOne.Infrastructure.Messaging;
using Xunit;
using KafkaConsumerOptions = NexaOne.Infrastructure.Messaging.KafkaConsumerOptions;

namespace NexaOne.ServerTests;

public sealed class KafkaProcessingContractTests
{
    [Fact]
    public async Task Handler_failure_stops_before_the_next_offset_and_preserves_failure()
    {
        var failure = new InvalidOperationException("handler failed");
        var consumer = Consumer();
        consumer.Setup(session => session.Consume(It.IsAny<TimeSpan>())).Returns(Record());
        using var service = Service(consumer, (_, _) => Task.FromException(failure));
        await service.StartAsync(CancellationToken.None);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
        observed.Should().BeSameAs(failure);
        consumer.Verify(session => session.Consume(It.IsAny<TimeSpan>()), Times.Once);
        consumer.Verify(session => session.Commit(It.IsAny<KafkaRecord>()), Times.Never);
        consumer.Verify(session => session.Resume(), Times.Never);
        consumer.Verify(session => session.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Ambiguous_commit_failure_stops_before_a_later_record()
    {
        var consumer = Consumer();
        consumer.Setup(session => session.Consume(It.IsAny<TimeSpan>())).Returns(Record());
        consumer.Setup(session => session.Commit(It.IsAny<KafkaRecord>())).Throws(new InvalidOperationException("commit outcome unknown"));
        using var service = Service(consumer, (_, _) => Task.CompletedTask);
        await service.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
        consumer.Verify(session => session.Consume(It.IsAny<TimeSpan>()), Times.Once);
        consumer.Verify(session => session.Commit(It.IsAny<KafkaRecord>()), Times.Once);
        consumer.Verify(session => session.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Long_handler_is_polled_while_paused_and_committed_only_after_completion()
    {
        var consumer = Consumer();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = false;
        DeliverOnce(consumer, Record());
        consumer.Setup(session => session.Pause()).Callback(() => paused = true);
        consumer.Setup(session => session.PollWhilePaused(It.IsAny<TimeSpan>())).Callback(() => { polled.TrySetResult(); Thread.Sleep(1); });
        consumer.Setup(session => session.Commit(It.IsAny<KafkaRecord>())).Callback(() => committed.TrySetResult());
        consumer.Setup(session => session.Resume()).Callback(() => resumed.TrySetResult());
        using var service = Service(consumer, (_, token) =>
        {
            paused.Should().BeTrue();
            return release.Task.WaitAsync(token);
        });
        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        committed.Task.IsCompleted.Should().BeFalse();
        release.SetResult();
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        committed.Task.IsCompletedSuccessfully.Should().BeTrue();
        consumer.Verify(session => session.Commit(It.IsAny<KafkaRecord>()), Times.Once);
    }

    [Fact]
    public async Task Poll_failure_cancels_and_drains_the_handler_without_committing()
    {
        var consumer = Consumer();
        var cancellationObserved = false;
        var drained = false;
        DeliverOnce(consumer, Record());
        consumer.Setup(session => session.PollWhilePaused(It.IsAny<TimeSpan>())).Throws(new InvalidOperationException("assignment changed"));
        using var service = Service(consumer, async (_, token) =>
        {
            using var registration = token.Register(() => cancellationObserved = true);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { drained = true; }
        });
        await service.StartAsync(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
        failure.Message.Should().Be("assignment changed");
        cancellationObserved.Should().BeTrue();
        drained.Should().BeTrue();
        consumer.Verify(session => session.Commit(It.IsAny<KafkaRecord>()), Times.Never);
        consumer.Verify(session => session.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Stop_waits_for_the_active_handler_and_does_not_commit_its_late_success()
    {
        var consumer = Consumer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DeliverOnce(consumer, Record());
        using var service = Service(consumer, async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        });
        await service.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = service.StopAsync(CancellationToken.None);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopping.IsCompleted.Should().BeFalse();
        consumer.Verify(session => session.Dispose(), Times.Never);
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        consumer.Verify(session => session.Commit(It.IsAny<KafkaRecord>()), Times.Never);
        consumer.Verify(session => session.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Throwing_cancellation_callback_does_not_hide_poll_failure_or_skip_handler_drain()
    {
        var consumer = Consumer();
        var pollFailure = new InvalidOperationException("poll failed");
        var drained = false;
        DeliverOnce(consumer, Record());
        consumer.Setup(session => session.PollWhilePaused(It.IsAny<TimeSpan>())).Throws(pollFailure);
        using var service = Service(consumer, async (_, token) =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException("cancel callback failed"));
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { drained = true; }
        });
        await service.StartAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<AggregateException>(async () => await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
        error.InnerExceptions[0].Should().BeSameAs(pollFailure);
        error.Flatten().InnerExceptions.Should().Contain(failure => failure.Message == "cancel callback failed");
        drained.Should().BeTrue();
        consumer.Verify(session => session.Commit(It.IsAny<KafkaRecord>()), Times.Never);
        consumer.Verify(session => session.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task Existing_poison_and_null_message_policy_commits_without_calling_the_handler(string? value)
    {
        var consumer = Consumer();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DeliverOnce(consumer, new KafkaRecord("events", "key", value!, 0, 1));
        consumer.Setup(session => session.Commit(It.IsAny<KafkaRecord>())).Callback(() => committed.TrySetResult());
        var calls = 0;
        using var service = Service(consumer, (_, _) => { calls++; return Task.CompletedTask; });
        await service.StartAsync(CancellationToken.None);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        calls.Should().Be(0);
        consumer.Verify(session => session.Commit(It.IsAny<KafkaRecord>()), Times.Once);
    }

    [Fact]
    public async Task Group_and_topics_are_snapshotted_before_the_host_starts()
    {
        var consumer = Consumer();
        var topics = new[] { "first", "second" };
        var options = new KafkaConsumerOptions { GroupId = "original", Topics = topics };
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[]? actualTopics = null;
        consumer.Setup(session => session.SubscribeTopics(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(values => { actualTopics = values.ToArray(); subscribed.TrySetResult(); });
        string? group = null;
        using var service = new KafkaConsumerService((settings, _) => { group = settings.GroupId; return consumer.Object; },
            options, (_, _) => Task.CompletedTask, NullLogger<KafkaConsumerService>.Instance);
        options.GroupId = "changed";
        topics[0] = "changed";
        await service.StartAsync(CancellationToken.None);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        group.Should().Be("original");
        actualTopics.Should().Equal("first", "second");
    }

    [Fact]
    public void Invalid_subscription_is_rejected_before_opening_a_session()
    {
        foreach (var topics in new IReadOnlyList<string>[] { [], ["a", "a"], ["a", " "] })
        {
            var create = () => new KafkaConsumerService((_, _) => throw new InvalidOperationException("must not create"),
                new KafkaConsumerOptions { Topics = topics }, (_, _) => Task.CompletedTask, NullLogger<KafkaConsumerService>.Instance);
            create.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public async Task Publish_preserves_key_payload_and_cancellation_and_disposes_the_owned_component()
    {
        var component = new Mock<IKafkaMessaging>();
        var message = DomainEventMessage.Create("Changed", "SYS", "user-1", "payload");
        using var cancellation = new CancellationTokenSource();
        string? json = null;
        component.Setup(instance => instance.PublishAsync("events", "user-1", It.IsAny<string>(), cancellation.Token))
            .Callback<string, string, string, CancellationToken>((_, _, value, _) => json = value)
            .Returns(Task.FromResult<KafkaPublishResult>(null!));
        using (var bus = new KafkaMessageBus("unused", component.Object, NullLogger<KafkaMessageBus>.Instance))
            await bus.PublishAsync("events", message, cancellation.Token);
        JsonSerializer.Deserialize<DomainEventMessage>(json!)!.Should().BeEquivalentTo(message);
        component.VerifyAll();
        component.Verify(instance => instance.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Batch_publish_stops_after_failure_without_publishing_the_rest()
    {
        var component = new Mock<IKafkaMessaging>();
        var calls = new List<string>();
        component.Setup(instance => instance.PublishAsync("events", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, CancellationToken>((_, key, _, _) =>
            {
                calls.Add(key);
                return key == "two" ? Task.FromException<KafkaPublishResult>(new InvalidOperationException("delivery uncertain"))
                    : Task.FromResult<KafkaPublishResult>(null!);
            });
        using var bus = new KafkaMessageBus("unused", component.Object, NullLogger<KafkaMessageBus>.Instance);
        var publish = () => bus.PublishAsync("events", new[] { "one", "two", "three" }.Select(key => DomainEventMessage.Create("Changed", "SYS", key, "payload")));
        await publish.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Equal("one", "two");
    }

    private static Mock<IKafkaConsumerSession> Consumer()
    {
        var consumer = new Mock<IKafkaConsumerSession>();
        consumer.Setup(session => session.Consume(It.IsAny<TimeSpan>())).Returns(() => { Thread.Sleep(1); return null; });
        consumer.Setup(session => session.PollWhilePaused(It.IsAny<TimeSpan>())).Callback(() => Thread.Sleep(1));
        return consumer;
    }

    private static void DeliverOnce(Mock<IKafkaConsumerSession> consumer, KafkaRecord record)
    {
        var delivered = false;
        consumer.Setup(session => session.Consume(It.IsAny<TimeSpan>())).Returns(() =>
        {
            if (!delivered) { delivered = true; return record; }
            Thread.Sleep(1);
            return null;
        });
    }

    private static KafkaConsumerService Service(Mock<IKafkaConsumerSession> consumer, Func<DomainEventMessage, CancellationToken, Task> handler)
        => new((_, _) => consumer.Object, new KafkaConsumerOptions { Topics = ["events"] }, handler, NullLogger<KafkaConsumerService>.Instance);

    private static KafkaRecord Record() => new("events", "key", JsonSerializer.Serialize(DomainEventMessage.Create("Changed", "SYS", "key", "payload")), 0, 1);
}
