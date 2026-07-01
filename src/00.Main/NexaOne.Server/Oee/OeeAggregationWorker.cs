using Microsoft.Extensions.Hosting;

namespace NexaOne.Server.Oee;

/// <summary>OEE 집계 워커(호스트 레벨, Quartz 비의존 BackgroundService). enabled 시 시작 직후 1회 + interval마다
/// 최근 <c>lookbackDays</c>일의 일자별 윈도를 <see cref="OeeAggregationService"/>로 집계해 OEE 마트를 갱신한다.
/// 기본 OFF(테스트/CI 무영향 — RefreshTokenCleanupWorker 패턴). 예외는 잡아 삼켜 다음 주기를 막지 않는다.</summary>
public sealed class OeeAggregationWorker : BackgroundService
{
    private readonly OeeAggregationService _service;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _lookbackDays;

    public OeeAggregationWorker(OeeAggregationService service, bool enabled, TimeSpan interval, int lookbackDays)
    {
        _service = service;
        _enabled = enabled;
        _interval = interval;
        _lookbackDays = Math.Max(1, lookbackDays);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[OeeAggregationWorker] disabled (enabled=false). Skipping startup.");
            return;
        }
        Console.WriteLine($"[OeeAggregationWorker] started (interval={_interval.TotalSeconds}s, lookbackDays={_lookbackDays}).");
        using var timer = new PeriodicTimer(_interval);
        do { await AggregateOnceAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task AggregateOnceAsync(CancellationToken ct)
    {
        try
        {
            // 최근 N일을 일자(UTC) 윈도로 각각 재집계(멱등 delete+insert). 오늘 포함.
            var today = DateTime.UtcNow.Date;
            int total = 0;
            for (int d = _lookbackDays - 1; d >= 0; d--)
            {
                var start = today.AddDays(-d);
                total += await _service.AggregateWindowAsync(start, start.AddDays(1), ct);
            }
            Console.WriteLine($"[OeeAggregationWorker] aggregated OEE for {total} equipment-day row(s) over {_lookbackDays} day(s).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Console.WriteLine($"[OeeAggregationWorker] aggregation failed: {ex.Message}"); }
    }
}
