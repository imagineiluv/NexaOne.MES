using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.EST.Application.Oee;
using NexaFramework.Scheduling;

namespace NexaOne.EST.Infrastructure;

/// <summary>OEE 집계 워커 — 모듈 소유 주기 작업(BackgroundService). 실 반복 스케줄러(<see cref="IRecurringScheduler"/>,
/// server.xml의 quartzScheduler 부모 빈)로 구동되며, enabled 시 최근 <c>lookbackDays</c>일을 일자 단위로 작업조 인식
/// 재집계해 OEE 마트를 갱신한다. 기본 OFF(테스트/CI 무영향 — FdcCollectDataRetentionWorker 패턴). 예외는 잡아 삼켜
/// 다음 주기를 막지 않는다. 원자료→마트 계산은 <see cref="IOeeAggregator"/>(OeeAggregationRepository)가 소유한다.</summary>
public sealed class OeeAggregationWorker : BackgroundService
{
    private readonly IRecurringScheduler _scheduler;
    private readonly IOeeAggregator _aggregator;
    private readonly bool _enabled;
    private readonly int _intervalSeconds;
    private readonly int _lookbackDays;

    public OeeAggregationWorker(
        IRecurringScheduler scheduler,
        IOeeAggregator aggregator,
        IConfiguration configuration,
        int intervalSeconds = 3600,
        int lookbackDays = 1)
    {
        _scheduler = scheduler;
        _aggregator = aggregator;
        _enabled = IsTrue(configuration["Worker:Est:OeeAggregation:Enabled"])
            || IsTrue(configuration["Oee:Aggregation:Enabled"]);
        _intervalSeconds = PositiveInt(
            configuration["Oee:Aggregation:IntervalSeconds"], intervalSeconds);
        _lookbackDays = PositiveInt(
            configuration["Oee:Aggregation:LookbackDays"], lookbackDays);
    }

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static int PositiveInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : Math.Max(1, fallback);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[OeeAggregationWorker] disabled (enabled=false). Skipping startup.");
            return;
        }
        Console.WriteLine($"[OeeAggregationWorker] started (interval={_intervalSeconds}s, lookbackDays={_lookbackDays}).");

        await _scheduler.StartAsync(stoppingToken);
        await _scheduler.ScheduleRecurringAsync(
            "est-oee-aggregation",
            TimeSpan.FromSeconds(_intervalSeconds),
            AggregateAsync,
            stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task AggregateAsync(CancellationToken ct)
    {
        try
        {
            // 최근 N일을 일자(UTC) 단위로 작업조 인식 재집계(멱등 delete+insert). 오늘 포함.
            var today = DateTime.UtcNow.Date;
            int total = 0;
            for (int d = _lookbackDays - 1; d >= 0; d--)
                total += await _aggregator.AggregateDayAsync(today.AddDays(-d), ct);
            Console.WriteLine($"[OeeAggregationWorker] aggregated OEE for {total} equipment-shift row(s) over {_lookbackDays} day(s).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Console.WriteLine($"[OeeAggregationWorker] aggregation failed: {ex.Message}"); }
    }
}
