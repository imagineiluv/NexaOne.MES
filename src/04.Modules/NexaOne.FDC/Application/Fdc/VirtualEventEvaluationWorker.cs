using Microsoft.Extensions.Hosting;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>가상 이벤트 주기 평가 워커(기본 OFF — Module configuration). 유효 정의 전체를 주기 평가하고
/// 전이는 VirtualEventService가 V069에 기록한다. 개별 정의 실패(수식 오류/값 부재)는 콘솔 경고 후 계속.
/// Program.cs가 Spring 컨텍스트에서 IHostedService로 자동발견해 호스팅한다(LoginFailureRetentionWorker 관례 —
/// Module.cs가 IConfiguration을 해석해 명시적인 실행 정책을 전달한다).</summary>
public sealed class VirtualEventEvaluationWorker : BackgroundService
{
    private readonly VirtualEventService _service;
    private readonly bool _enabled;
    private readonly int _intervalSeconds;

    public VirtualEventEvaluationWorker(VirtualEventService service, bool enabled, int intervalSeconds)
    {
        _service = service;
        _enabled = enabled;
        _intervalSeconds = Math.Max(5, intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[NexaOne.FDC] VirtualEventEvaluationWorker 비활성 — 수동 평가(evaluate API)만 동작.");
            return;
        }

        Console.WriteLine($"[NexaOne.FDC] VirtualEventEvaluationWorker 시작 — 주기 {_intervalSeconds}s.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_intervalSeconds));
        while (await WaitSafeAsync(timer, stoppingToken))
        {
            try
            {
                foreach (var result in await _service.EvaluateAllAsync(stoppingToken))
                    if (result.IsFailure)
                        Console.WriteLine($"[NexaOne.FDC] 가상 이벤트 평가 실패 — {result.Error.Description}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NexaOne.FDC] 가상 이벤트 주기 평가 실패(다음 주기 재시도) — {ex.Message}");
            }
        }
    }

    private static async Task<bool> WaitSafeAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
