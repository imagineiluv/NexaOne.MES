using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace NexaOne.Driver.Redis;

public sealed class RedisDriver : IDisposable
{
    private readonly ILogger<RedisDriver> _logger;
    private IConnectionMultiplexer? _connection;
    private bool _disposed;

    public RedisDriver(ILogger<RedisDriver> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string connectionString, CancellationToken ct = default)
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        _logger.LogInformation("Redis connected to {Endpoint}", connectionString);
    }

    private IDatabase GetDatabase() =>
        (_connection ?? throw new InvalidOperationException("RedisDriver not connected.")).GetDatabase();

    public Task SetAsync(string key, string value, TimeSpan? expiry = null) =>
        GetDatabase().StringSetAsync(key, value, expiry.HasValue ? (Expiration)expiry.Value : Expiration.Persist);

    public async Task<string?> GetAsync(string key)
    {
        var value = await GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public Task DeleteAsync(string key) =>
        GetDatabase().KeyDeleteAsync(key);

    public Task<bool> ExistsAsync(string key) =>
        GetDatabase().KeyExistsAsync(key);

    public async Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry)
    {
        var db = GetDatabase();
        return await db.StringSetAsync(key, value, expiry, When.NotExists);
    }

    public async Task<IReadOnlyList<string>> GetSetMembersAsync(string key)
    {
        var members = await GetDatabase().SetMembersAsync(key);
        return members.Select(m => m.ToString()).ToList();
    }

    public Task AddToSetAsync(string key, string member) =>
        GetDatabase().SetAddAsync(key, member);

    public Task RemoveFromSetAsync(string key, string member) =>
        GetDatabase().SetRemoveAsync(key, member);

    public void Dispose()
    {
        if (_disposed) return;
        _connection?.Close();
        _connection?.Dispose();
        _disposed = true;
    }
}
