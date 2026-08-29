using Microsoft.Extensions.Hosting;
using NexaOne.ServiceContracts.Fdc;
using NexaFramework.Scheduling;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>
/// FDC 수집데이터 보존정리 워커 — 모듈 소유 주기 작업(BackgroundService). 실 반복 스케줄러
/// (<see cref="IRecurringScheduler"/>, server.xml의 quartzScheduler 빈)로 구동되며, 보존기간이 지난
/// (COLLECTED_AT &lt; now - retentionDays) 시계열 수집 행을 주기적으로 삭제한다.
/// </summary>
/// <remarks>
/// 레퍼런스 패턴: ScheduledOutboxDispatchWorker(스케줄러 사용법·enabled 게이트) + FdcCollectionWorker
/// (모듈 소유 BackgroundService·웹 타입 미참조·enabled=false 즉시 no-op).
/// 게이트(<c>_enabled</c>) 기본 OFF. 정리 작업이라 messageBus는 사용하지 않는다.
/// quartzScheduler는 부모(server.xml) 컨텍스트 빈을 cross-context ref로 주입받는다.
/// </remarks>
public sealed class FdcCollectDataRetentionWorker : BackgroundService
{
    private readonly IRecurringScheduler _scheduler;
    private readonly IFdcCollectDataRetentionRepository? _retentionRepo;
    private readonly IFdcTraceRetentionGuard _retentionGuard;
    private readonly bool _enabled;
    private readonly int _intervalSeconds;
    private readonly int _retentionDays;
    private int _consecutiveBatchLimitRuns;

    /// <summary>
    /// 이전 binary constructor ABI를 보존한다. guard 없이 삭제를 켜는 구성은 안전하지 않으므로 즉시
    /// 거부하고, 비활성 legacy 조립만 새 constructor로 위임한다.
    /// </summary>
    public FdcCollectDataRetentionWorker(
        IRecurringScheduler scheduler,
        IFdcCollectDataRepository dataRepo,
        bool enabled,
        int intervalSeconds = 86400,
        int retentionDays = 30)
        : this(
            scheduler,
            dataRepo,
            RequireDisabledLegacyGuard(enabled),
            enabled,
            intervalSeconds,
            retentionDays)
    {
    }

    public FdcCollectDataRetentionWorker(
        IRecurringScheduler scheduler,
        IFdcCollectDataRepository dataRepo,
        IFdcTraceRetentionGuard retentionGuard,
        bool enabled,
        int intervalSeconds = 86400,
        int retentionDays = 30)
        : this(
            scheduler,
            dataRepo,
            retentionGuard,
            enabled,
            bindingChangesQuiesced: false,
            intervalSeconds,
            retentionDays)
    {
    }

    /// <summary>
    /// 보존 실행 전체 기간 IVT binding/cursor의 보호 시작점을 낮출 수 있는 변경이 운영 절차로 동결됐음을
    /// 명시적으로 확인하는 안전 constructor다. 지속 online 변경은 공통 revision/lock protocol 도입 전까지
    /// 지원하지 않는다.
    /// </summary>
    public FdcCollectDataRetentionWorker(
        IRecurringScheduler scheduler,
        IFdcCollectDataRepository dataRepo,
        IFdcTraceRetentionGuard retentionGuard,
        bool enabled,
        bool bindingChangesQuiesced,
        int intervalSeconds = 86400,
        int retentionDays = 30)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        ArgumentNullException.ThrowIfNull(dataRepo);
        _retentionGuard = retentionGuard ?? throw new ArgumentNullException(nameof(retentionGuard));
        _retentionRepo = dataRepo as IFdcCollectDataRetentionRepository;
        if (enabled
            && (_retentionRepo is null || dataRepo is not IFdcTraceRetentionStateRepository))
        {
            throw new InvalidOperationException(
                "Enabled FDC TRACE retention requires one repository implementing both durable retention purge and state contracts.");
        }
        if (enabled && !bindingChangesQuiesced)
        {
            throw new InvalidOperationException(
                "Enabled FDC TRACE retention requires BindingChangesQuiesced=true for the entire process lifetime. "
                + "Binding insert/activate/reactivate, effective-range changes, and cursor rollback must remain frozen.");
        }
        _enabled = enabled;
        _intervalSeconds = intervalSeconds;
        _retentionDays = retentionDays;
    }

    private static IFdcTraceRetentionGuard RequireDisabledLegacyGuard(bool enabled)
    {
        if (enabled)
        {
            throw new InvalidOperationException(
                "FDC TRACE retention cannot be enabled through the legacy constructor without an IVT retention guard.");
        }

        return DisabledFdcTraceRetentionGuard.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[FdcCollectDataRetentionWorker] disabled (enabled=false). Skipping startup.");
            return;
        }

        Console.WriteLine(
            $"[FdcCollectDataRetentionWorker] started (interval={_intervalSeconds}s, retentionDays={_retentionDays}).");

        await _scheduler.StartAsync(stoppingToken);
        await _scheduler.ScheduleRecurringAsync(
            "fdc-collect-data-retention",
            TimeSpan.FromSeconds(_intervalSeconds),
            PurgeAsync,
            stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    /// <summary>보존기간이 지난 수집 행을 1회 삭제한다. 기준시각(cutoff)은 C#에서 산정해 리포에 넘긴다
    /// (MSSQL/SQLite 날짜 방언 분기 회피). 작업 예외는 잡아 삼켜 다음 주기를 막지 않는다(스케줄러 지속).</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            var requestedCutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            var lowWatermark = await _retentionGuard.GetLowWatermarkAsync(ct);
            var cutoff = lowWatermark is { } protectedAt && protectedAt < requestedCutoff
                ? protectedAt
                : requestedCutoff;
            var result = await _retentionRepo!.PurgeOlderThanAsync(cutoff, ct);
            Console.WriteLine(
                $"[FdcCollectDataRetentionWorker] purged {result.DeletedRows} row(s) older than {cutoff:o} "
                + $"(requested={requestedCutoff:o}, IVT-low-watermark={lowWatermark:o}) "
                + $"in {result.Elapsed.TotalMilliseconds:F0} ms.");
            if (result.BatchLimitReached)
            {
                _consecutiveBatchLimitRuns++;
                Console.WriteLine(
                    "[FdcCollectDataRetentionWorker] WARNING: bounded purge cap was reached "
                    + $"for {_consecutiveBatchLimitRuns} consecutive run(s); oldest retained backlog row="
                    + $"{result.OldestRemainingCollectedAt:o}, elapsed={result.Elapsed.TotalMilliseconds:F0} ms.");
            }
            else
            {
                _consecutiveBatchLimitRuns = 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[FdcCollectDataRetentionWorker] purge failed: {ex.Message}");
        }
    }

}

internal sealed class DisabledFdcTraceRetentionGuard : IFdcTraceRetentionGuard
{
    public static readonly DisabledFdcTraceRetentionGuard Instance = new();

    private DisabledFdcTraceRetentionGuard()
    {
    }

    public Task<DateTime?> GetLowWatermarkAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("Disabled FDC TRACE retention guard must never be invoked.");
}
