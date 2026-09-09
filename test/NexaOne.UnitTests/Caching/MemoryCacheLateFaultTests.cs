using System.Runtime.CompilerServices;
using NexaOne.Common.Caching;

namespace NexaOne.UnitTests.Caching;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CacheFaultCollection
{
    public const string Name = "Cache late fault collection";
}

[Collection(CacheFaultCollection.Name)]
public sealed class MemoryCacheLateFaultTests
{
    [Fact]
    public async Task Canceled_wait_observes_a_later_factory_failure_without_retaining_the_task()
    {
        var marker = new InvalidOperationException("late cache read " + Guid.NewGuid());
        int unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            if (!args.Exception.Flatten().InnerExceptions.Contains(marker)) return;
            Interlocked.Increment(ref unobserved);
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var task = await CreateLateFailure(marker);
            for (int i = 0; task.IsAlive && i < 100; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(10);
            }
            task.IsAlive.Should().BeFalse("the detached factory has completed");
            Volatile.Read(ref unobserved).Should().Be(0);
        }
        finally { TaskScheduler.UnobservedTaskException -= handler; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CreateLateFailure(Exception marker)
    {
        using var service = new MemoryCacheService();
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = service.GetOrCreateAsync("late", () => completion.Task, ct: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        completion.SetException(marker);
        return new WeakReference(completion.Task);
    }
}
