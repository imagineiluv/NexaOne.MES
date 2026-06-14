using Microsoft.Extensions.Caching.Memory;
using NexaOne.Common.Caching;

namespace NexaOne.UnitTests.Caching;

/// <summary>인메모리 캐시 구현(ICacheService 기본) — 캐시 적중·무효화 동작 검증.</summary>
public sealed class MemoryCacheServiceTests
{
    private static MemoryCacheService New() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task GetOrCreate_invokes_factory_once_then_serves_from_cache()
    {
        var svc = New();
        var calls = 0;
        Task<int> Factory() { calls++; return Task.FromResult(42); }

        (await svc.GetOrCreateAsync("k", Factory)).Should().Be(42);
        (await svc.GetOrCreateAsync("k", Factory)).Should().Be(42);

        calls.Should().Be(1, "캐시 적중 시 factory는 재호출되지 않아야 한다");
    }

    [Fact]
    public async Task RemoveAsync_invalidates_so_factory_runs_again()
    {
        var svc = New();
        var calls = 0;
        Task<int> Factory() { calls++; return Task.FromResult(calls); }

        await svc.GetOrCreateAsync("k", Factory);   // calls=1
        await svc.RemoveAsync("k");
        var v = await svc.GetOrCreateAsync("k", Factory);   // 무효화 → calls=2

        calls.Should().Be(2);
        v.Should().Be(2, "무효화 후 조회는 factory를 다시 실행해 새 값을 반환해야 한다");
    }
}
