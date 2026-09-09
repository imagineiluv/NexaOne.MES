using Microsoft.Extensions.Caching.Memory;
using NexaOne.Common.Telemetry;

namespace NexaOne.Common.Caching;

/// <summary>
/// 인메모리 캐시 구현(ICacheService 기본) — IMemoryCache 기반. 단일 프로세스 캐시로, 직렬화가 없어
/// 도메인 객체도 그대로 캐시할 수 있다(참조 저장). 다중 인스턴스 공유가 필요하면 Redis 구현으로 전환한다.
/// 조회와 무효화는 동일 서비스 인스턴스를 사용한다. 외부 캐시 직접 쓰기는 게시 권한 제어에 참여하지 않는다.
/// </summary>
public sealed class MemoryCacheService : ICacheService, IDisposable
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private readonly IMemoryCache _cache;
    private readonly bool _ownsCache;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingReads> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>외부 캐시를 빌려 사용한다. 외부 캐시의 해제는 생성한 호출자가 소유한다.</summary>
    public MemoryCacheService(IMemoryCache cache) => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    /// <summary>Spring 컨테이너 등 IMemoryCache를 DI로 주입받기 어려운 호스트용 편의 생성자 —
    /// 기본 옵션의 MemoryCache를 자체 생성하고 Dispose에서 해제한다. FDC 수집 핫패스의
    /// server.xml cacheService 공통 빈과 명시적 new 조립에서 이 생성자를 사용한다.</summary>
    public MemoryCacheService() : this(new MemoryCache(new MemoryCacheOptions())) => _ownsCache = true;

    public async Task<T> GetOrCreateAsync<T>(
        string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        // A hit already owns its snapshot; it need not serialize with other readers or
        // invalidation. Recheck under the gate after a miss before admitting a factory.
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            NexaMesMetrics.RecordCacheHit(key);
            return cached;
        }
        PendingReads? pending = null;
        bool hit;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            hit = _cache.TryGetValue(key, out cached) && cached is not null;
            if (!hit)
            {
                if (!_pending.TryGetValue(key, out pending))
                    _pending.Add(key, pending = new PendingReads());
                pending.Count++;
            }
        }
        if (hit)
        {
            NexaMesMetrics.RecordCacheHit(key);
            return cached!;
        }

        Task<T>? factoryTask = null;
        try
        {
            // Factories and telemetry callbacks run outside the lock. Existing callers own
            // their factory/cancellation closure; concurrent misses are not merged.
            NexaMesMetrics.RecordCacheMiss(key);
            factoryTask = factory();
            var value = await factoryTask.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_disposed && _pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                    _cache.Set(key, value, ttl ?? DefaultTtl);
            }
            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // WaitAsync detaches from the source on cancellation. Observe a later failure
            // without keeping this service alive or delaying the canceled caller.
            if (factoryTask is not null)
                _ = factoryTask.ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                pending!.Count--;
                if (pending.Count == 0 && _pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                    _pending.Remove(key);
            }
        }
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Callers invalidate after a committed write, even if the request was canceled
            // meanwhile. This local operation must not leave the committed value hidden.
            _pending.Remove(key);
            _cache.Remove(key);
        }
        return Task.CompletedTask;
    }

    /// <summary>새 조회/무효화를 차단하고 자체 생성한 캐시만 해제한다. 진행 중 factory는 호출자 소유다.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            _pending.Clear();
            if (_ownsCache) _cache.Dispose();
        }
    }

    // Only active reads retain a generation. Invalidation detaches it immediately; no state
    // accumulates for completed, failed, canceled, or removed keys.
    private sealed class PendingReads
    {
        internal int Count;
    }
}
