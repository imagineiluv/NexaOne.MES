using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>배치 주기 실행 워커(기본 OFF — Worker:Sys:BatchProcess:Enabled). 스케줄 규약:
/// BATCH_TYPE='Interval' + BATCH_OPTIONS=주기(초) 또는 BATCH_TYPE='Cron' + BATCH_OPTIONS=6필드 cron식
/// (초 분 시 일 월 요일, '?'=* — 레거시 Quartz 표기 수용, CronSchedule 참조). 그 외 유형은 정의 보존 +
/// 실행 스킵(경고 1회). 실행 자체는 BatchProcessRunner 단일 경로(수동 실행과 동일 규약).
/// 스케줄 상태는 인메모리 추적 — 재기동 시 Interval은 주기 도래분 1회 즉시 실행, Cron은 다음 발생부터.</summary>
public sealed class BatchProcessWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<BatchProcessWorker> _logger;
    private readonly Dictionary<string, DateTime> _lastRun = new(StringComparer.OrdinalIgnoreCase);
    // Cron 다음 발생 시각 캐시 — 키는 batchId, 식이 바뀌면 재계산되도록 표현식도 함께 보관.
    private readonly Dictionary<string, (string Expression, DateTime NextDue)> _cronNext = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedUnsupported = new(StringComparer.OrdinalIgnoreCase);

    public BatchProcessWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<BatchProcessWorker> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Worker:Sys:BatchProcess:Enabled", false))
        {
            _logger.LogInformation("BatchProcessWorker 비활성(Worker:Sys:BatchProcess:Enabled=false) — 수동 실행(run API)만 동작.");
            return;
        }

        var pollSeconds = Math.Max(5, _config.GetValue("Worker:Sys:BatchProcess:PollSeconds", 30));
        _logger.LogInformation(
            "BatchProcessWorker 시작 — poll {Poll}s, 규약: Interval(초) / Cron(6필드 초 분 시 일 월 요일).", pollSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));
        while (await WaitSafeAsync(timer, stoppingToken))
        {
            try { await RunDueAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "배치 스케줄 폴링 실패 — 다음 주기에 재시도."); }
        }
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<BatchProcessRunner>();

        foreach (var def in await runner.ListDefinitionsAsync(ct))
        {
            var batchId = Str(def, "BATCH_ID");
            if (batchId.Length == 0) continue;

            var type = Str(def, "BATCH_TYPE");
            var options = Str(def, "BATCH_OPTIONS");
            var now = DateTime.UtcNow;
            bool due;

            if (string.Equals(type, "Interval", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(options, out var intervalSeconds) || intervalSeconds <= 0)
                {
                    if (_warnedUnsupported.Add(batchId))
                        _logger.LogWarning("배치 '{BatchId}' BATCH_OPTIONS='{Options}' 파싱 불가 — Interval 유형은 초(정수)여야 합니다.", batchId, options);
                    continue;
                }
                due = !_lastRun.TryGetValue(batchId, out var last) || (now - last).TotalSeconds >= intervalSeconds;
                if (due) _lastRun[batchId] = now;
            }
            else if (string.Equals(type, "Cron", StringComparison.OrdinalIgnoreCase))
            {
                if (!CronSchedule.TryParse(options, out var cron) || cron is null)
                {
                    if (_warnedUnsupported.Add(batchId))
                        _logger.LogWarning("배치 '{BatchId}' cron식 '{Options}' 파싱 불가 — 6필드(초 분 시 일 월 요일)여야 합니다.", batchId, options);
                    continue;
                }
                // 표현식 변경/최초 발견 시 다음 발생부터 스케줄(재기동 직후 과거분 몰아 실행 방지).
                if (!_cronNext.TryGetValue(batchId, out var state) || !string.Equals(state.Expression, options, StringComparison.Ordinal))
                {
                    var first = cron.GetNextOccurrence(now);
                    if (first is null)
                    {
                        if (_warnedUnsupported.Add(batchId))
                            _logger.LogWarning("배치 '{BatchId}' cron식 '{Options}'의 발생 시각을 찾지 못했습니다(5년 내 매칭 없음).", batchId, options);
                        continue;
                    }
                    _cronNext[batchId] = (options, first.Value);
                    continue;
                }
                due = now >= state.NextDue;
                if (due)
                {
                    var next = cron.GetNextOccurrence(now);
                    if (next is null) { _cronNext.Remove(batchId); }
                    else _cronNext[batchId] = (options, next.Value);
                }
            }
            else
            {
                if (_warnedUnsupported.Add(batchId))
                    _logger.LogWarning("배치 '{BatchId}' 유형 '{Type}'은 워커가 실행하지 않습니다(Interval/Cron만) — 정의는 보존.", batchId, type);
                continue;
            }

            if (!due) continue;
            var result = await runner.RunAsync(batchId, "BATCH", ct);
            if (result is { Success: true })
                _logger.LogInformation("배치 '{BatchId}' 실행 완료 — affected={Affected}.", batchId, result.Affected);
        }
    }

    private static async Task<bool> WaitSafeAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private static string Str(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";
}
