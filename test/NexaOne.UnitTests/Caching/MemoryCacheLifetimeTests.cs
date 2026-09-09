using Microsoft.Extensions.Caching.Memory;
using NexaOne.Common.Caching;

namespace NexaOne.UnitTests.Caching;

public sealed class MemoryCacheLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_request_does_not_read_or_populate_cache(bool hit)
    {
        using var service = new MemoryCacheService();
        if (hit) await service.GetOrCreateAsync("key", () => Task.FromResult(1));
        var invoked = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetOrCreateAsync("key", () =>
        {
            invoked = true;
            return Task.FromResult(2);
        }, ct: new CancellationToken(true)));
        invoked.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_detaches_only_its_own_wait_and_does_not_publish_the_late_result()
    {
        using var service = new MemoryCacheService();
        using var cancellation = new CancellationTokenSource();
        var abandoned = NewCompletion();
        var remaining = NewCompletion();
        var first = service.GetOrCreateAsync("key", () => abandoned.Task, ct: cancellation.Token);
        var second = service.GetOrCreateAsync("key", () => remaining.Task);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        remaining.SetResult(2);
        (await second).Should().Be(2);
        abandoned.SetResult(1);
        (await service.GetOrCreateAsync("key", () => Task.FromResult(3))).Should().Be(2);
    }

    [Fact]
    public async Task Completing_retired_readers_does_not_revoke_the_new_generation()
    {
        using var service = new MemoryCacheService();
        var first = NewCompletion();
        var second = NewCompletion();
        var fresh = NewCompletion();
        var a = service.GetOrCreateAsync("key", () => first.Task);
        var b = service.GetOrCreateAsync("key", () => second.Task);
        await service.RemoveAsync("key");
        var c = service.GetOrCreateAsync("key", () => fresh.Task);
        first.SetResult(1);
        second.SetResult(2);
        await Task.WhenAll(a, b);
        fresh.SetResult(3);
        (await c).Should().Be(3);
        (await service.GetOrCreateAsync("key", () => Task.FromResult(4))).Should().Be(3);
    }

    [Fact]
    public async Task Factories_do_not_hold_the_cache_lock_while_waiting_for_other_keys()
    {
        using var service = new MemoryCacheService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewCompletion();
        var pending = service.GetOrCreateAsync("slow", async () =>
        {
            entered.SetResult();
            return await release.Task;
        });
        await entered.Task;
        try
        {
            var independent = Task.Run(() => service.GetOrCreateAsync("fast", () => Task.FromResult(2)));
            (await independent.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(2);
            await Task.Run(() => service.RemoveAsync("slow")).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(1); }
        await pending;
        (await service.GetOrCreateAsync("slow", () => Task.FromResult(3))).Should().Be(3);
    }

    [Fact]
    public async Task Failed_factory_can_be_retried()
    {
        using var service = new MemoryCacheService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetOrCreateAsync<int>("key",
            () => throw new InvalidOperationException("read failed")));
        (await service.GetOrCreateAsync("key", () => Task.FromResult(2))).Should().Be(2);
    }

    [Fact]
    public async Task Disposal_preserves_a_borrowed_cache_and_prevents_late_publication()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("other-owner", 42);
        var service = new MemoryCacheService(cache);
        var completion = NewCompletion();
        var pending = service.GetOrCreateAsync("late", () => completion.Task);
        service.Dispose();
        service.Dispose();
        completion.SetResult(1);
        (await pending).Should().Be(1);
        cache.Get<int>("other-owner").Should().Be(42);
        cache.TryGetValue("late", out _).Should().BeFalse();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetOrCreateAsync("key", () => Task.FromResult(3)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RemoveAsync("late"));
    }

    [Fact]
    public async Task Default_owned_cache_stops_accepting_requests_after_disposal()
    {
        var service = new MemoryCacheService();
        await service.GetOrCreateAsync("key", () => Task.FromResult(1));
        service.Dispose();
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetOrCreateAsync("key", () => Task.FromResult(2)));
    }

    [Fact]
    public async Task Post_commit_invalidation_is_not_suppressed_by_request_cancellation()
    {
        using var service = new MemoryCacheService();
        await service.GetOrCreateAsync("key", () => Task.FromResult(1));
        await service.RemoveAsync("key", new CancellationToken(true));
        (await service.GetOrCreateAsync("key", () => Task.FromResult(2))).Should().Be(2);
    }

    private static TaskCompletionSource<int> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
