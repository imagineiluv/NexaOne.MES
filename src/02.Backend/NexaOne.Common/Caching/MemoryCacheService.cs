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

    /// <summary>Spring 컨테이너 등 IMemoryCache를 DI로 주입받기 어려운 호스트용 편의 생성자 —
    /// 기본 옵션의 MemoryCache를 자체 생성해 보관한다(프로세스 수명과 함께 유지). FDC 수집 핫패스의
    /// server.xml cacheService 공통 빈이 이 생성자로 조립된다(API 티어는 MS DI로 IMemoryCache를 주입).</summary>
    public MemoryCacheService() : this(new MemoryCache(new MemoryCacheOptions())) { }

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
