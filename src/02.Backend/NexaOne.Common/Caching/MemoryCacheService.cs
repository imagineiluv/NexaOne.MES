using Microsoft.Extensions.Caching.Memory;

namespace NexaOne.Common.Caching;

/// <summary>
/// 인메모리 캐시 구현(ICacheService 기본) — IMemoryCache 기반. 단일 프로세스 캐시로, 직렬화가 없어
/// 도메인 객체도 그대로 캐시할 수 있다(참조 저장). 다중 인스턴스 공유가 필요하면 Redis 구현으로 전환한다.
/// </summary>
public sealed class MemoryCacheService : ICacheService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private readonly IMemoryCache _cache;

    public MemoryCacheService(IMemoryCache cache) => _cache = cache;

    public async Task<T> GetOrCreateAsync<T>(
        string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
            return cached;

        var value = await factory().ConfigureAwait(false);
        _cache.Set(key, value, ttl ?? DefaultTtl);
        return value;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }
}
