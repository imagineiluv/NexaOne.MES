using Microsoft.Extensions.Hosting;
using NexusFramework.Scheduling;

namespace NexaOne.SYS.Application.Users;

/// <summary>
/// 로그인실패 이력 보존정리 워커 — 모듈 소유 주기 작업(BackgroundService). 실 반복 스케줄러
/// (<see cref="IRecurringScheduler"/>, server.xml의 quartzScheduler 빈)로 구동되며, 보존기간이 지난
/// (OCCURRED_AT &lt; now - retentionDays) 로그인실패 이력을 주기적으로 삭제한다.
/// </summary>
/// <remarks>
/// 레퍼런스 패턴: ScheduledOutboxDispatchWorker(스케줄러 사용법·enabled 게이트) + FdcCollectionWorker
/// (모듈 소유 BackgroundService·웹 타입 미참조·enabled=false 즉시 no-op).
/// 게이트(<c>_enabled</c>) 기본 OFF. 정리 작업이라 messageBus는 사용하지 않는다.
/// quartzScheduler는 부모(server.xml) 컨텍스트 빈을 cross-context ref로 주입받는다.
/// </remarks>
public sealed class LoginFailureRetentionWorker : BackgroundService
{
    private readonly IRecurringScheduler _scheduler;
    private readonly ILoginFailureHistoryRepository _historyRepo;
    private readonly bool _enabled;
    private readonly int _intervalSeconds;
    private readonly int _retentionDays;

    public LoginFailureRetentionWorker(
        IRecurringScheduler scheduler,
        ILoginFailureHistoryRepository historyRepo,
        bool enabled,
        int intervalSeconds = 86400,
        int retentionDays = 90)
    {
        _scheduler = scheduler;
        _historyRepo = historyRepo;
        _enabled = enabled;
        _intervalSeconds = intervalSeconds;
        _retentionDays = retentionDays;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[LoginFailureRetentionWorker] disabled (enabled=false). Skipping startup.");
            return;
        }

        Console.WriteLine(
            $"[LoginFailureRetentionWorker] started (interval={_intervalSeconds}s, retentionDays={_retentionDays}).");

        await _scheduler.StartAsync(stoppingToken);
        await _scheduler.ScheduleRecurringAsync(
            "sys-login-failure-retention",
            TimeSpan.FromSeconds(_intervalSeconds),
            PurgeAsync,
            stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    /// <summary>보존기간이 지난 로그인실패 이력을 1회 삭제한다. 기준시각(cutoff)은 C#에서 산정해 리포에 넘긴다
    /// (MSSQL/SQLite 날짜 방언 분기 회피). 작업 예외는 잡아 삼켜 다음 주기를 막지 않는다(스케줄러 지속).</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            var deleted = await _historyRepo.DeleteOlderThanAsync(cutoff, ct);
            Console.WriteLine(
                $"[LoginFailureRetentionWorker] purged {deleted} row(s) older than {cutoff:o}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[LoginFailureRetentionWorker] purge failed: {ex.Message}");
        }
    }
}
