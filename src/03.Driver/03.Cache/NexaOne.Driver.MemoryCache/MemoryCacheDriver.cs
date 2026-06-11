using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace NexaOne.Driver.MemoryCache;

public sealed class MemoryCacheDriver : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<MemoryCacheDriver> _logger;
    private bool _disposed;

    public MemoryCacheDriver(IMemoryCache cache, ILogger<MemoryCacheDriver> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public void Set<T>(string key, T value, TimeSpan? absoluteExpiry = null, TimeSpan? slidingExpiry = null)
    {
        var options = new MemoryCacheEntryOptions();
        if (absoluteExpiry.HasValue) options.AbsoluteExpirationRelativeToNow = absoluteExpiry;
        if (slidingExpiry.HasValue)  options.SlidingExpiration = slidingExpiry;
        _cache.Set(key, value, options);
        _logger.LogDebug("Cache set: {Key}", key);
    }

    public T? Get<T>(string key)
    {
        _cache.TryGetValue(key, out T? value);
        return value;
    }

    public bool TryGet<T>(string key, out T? value) =>
        _cache.TryGetValue(key, out value);

    public T GetOrSet<T>(string key, Func<T> factory, TimeSpan? absoluteExpiry = null, TimeSpan? slidingExpiry = null)
    {
        if (_cache.TryGetValue(key, out T? existing)) return existing!;

        var value = factory();
        Set(key, value, absoluteExpiry, slidingExpiry);
        return value;
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory,
        TimeSpan? absoluteExpiry = null, TimeSpan? slidingExpiry = null)
    {
        if (_cache.TryGetValue(key, out T? existing)) return existing!;

        var value = await factory();
        Set(key, value, absoluteExpiry, slidingExpiry);
        return value;
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
        _logger.LogDebug("Cache removed: {Key}", key);
    }

    public bool Exists(string key) => _cache.TryGetValue(key, out _);

    public void Dispose()
    {
        if (_disposed) return;
        _cache.Dispose();
        _disposed = true;
    }
}
