using System.Text.Json;
using NexaOne.Common.Caching;
using NexaOne.Common.Telemetry;

namespace NexaOne.Driver.Redis;

/// <summary>
/// 분산 캐시 구현(ICacheService) — 기존 RedisDriver 위에 JSON 직렬화로 GetOrCreate/Remove를 제공한다.
/// 다중 인스턴스가 캐시를 공유해야 할 때 Cache:Provider=Redis로 활성화한다(인메모리 대안).
/// </summary>
/// <remarks>
/// Redis는 값을 JSON으로 직렬화해 저장하므로 캐시 대상은 직렬화/역직렬화 가능한 타입(DTO/레코드)이어야 한다
/// (도메인 객체는 인메모리 캐시에 적합). 연결은 최초 사용 시 1회 lazy 수립한다(Kafka처럼 opt-in 인프라).
/// </remarks>
public sealed class RedisCacheService : ICacheService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private readonly RedisDriver _driver;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _connected;

    public RedisCacheService(RedisDriver driver, string connectionString)
    {
        _driver = driver;
        _connectionString = connectionString;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected)
            {
                await _driver.ConnectAsync(_connectionString, ct).ConfigureAwait(false);
                _connected = true;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var json = await _driver.GetAsync(key).ConfigureAwait(false);
        if (json is not null)
        {
            var hit = JsonSerializer.Deserialize<T>(json);
            if (hit is not null)
            {
                NexaMesMetrics.RecordCacheHit(key);
                return hit;
            }
        }
        NexaMesMetrics.RecordCacheMiss(key);
        var value = await factory().ConfigureAwait(false);
        await _driver.SetAsync(key, JsonSerializer.Serialize(value), ttl ?? DefaultTtl).ConfigureAwait(false);
        return value;
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _driver.DeleteAsync(key).ConfigureAwait(false);
    }
}
