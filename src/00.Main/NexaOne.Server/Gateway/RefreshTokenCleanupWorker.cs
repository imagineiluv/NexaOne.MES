using Microsoft.Extensions.Hosting;

namespace NexaOne.Server.Gateway;

/// <summary>리프레시 토큰 만료 정리 워커(호스트 레벨, Quartz 비의존 BackgroundService). enabled 시 시작 직후 1회 +
/// interval마다 SysRefreshTokenStore.PurgeExpiredAsync로 retention 경과 토큰을 삭제한다. 기본 OFF(테스트/CI 무영향).
/// 예외는 잡아 삼켜 다음 주기를 막지 않는다(LoginFailureRetentionWorker 패턴).</summary>
public sealed class RefreshTokenCleanupWorker : BackgroundService
{
    private readonly SysRefreshTokenStore _store;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _retention;

    public RefreshTokenCleanupWorker(SysRefreshTokenStore store, bool enabled, TimeSpan interval, TimeSpan retention)
    {
        _store = store;
        _enabled = enabled;
        _interval = interval;
        _retention = retention;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[RefreshTokenCleanupWorker] disabled (enabled=false). Skipping startup.");
            return;
        }
        Console.WriteLine($"[RefreshTokenCleanupWorker] started (interval={_interval.TotalSeconds}s, retentionDays={_retention.TotalDays}).");
        using var timer = new PeriodicTimer(_interval);
        do { await PurgeOnceAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        try
        {
            var deleted = await _store.PurgeExpiredAsync(_retention);
            Console.WriteLine($"[RefreshTokenCleanupWorker] purged {deleted} expired refresh token(s).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Console.WriteLine($"[RefreshTokenCleanupWorker] purge failed: {ex.Message}"); }
    }
}
