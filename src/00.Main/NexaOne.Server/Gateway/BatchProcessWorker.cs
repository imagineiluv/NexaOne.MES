using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>배치 주기 실행 워커(기본 OFF — Worker:Sys:BatchProcess:Enabled). v1 스케줄 규약:
/// BATCH_TYPE='Interval' + BATCH_OPTIONS=주기(초)인 정의만 주기 실행한다 — 그 외 유형(레거시 cron 등)은
/// 정의 보존 + 실행 스킵(경고 1회). 실행 자체는 BatchProcessRunner 단일 경로(수동 실행과 동일 규약).
/// 마지막 실행 시각은 인메모리 추적(재기동 시 초기화 → 주기 도래 정의는 1회 즉시 실행됨을 수용).</summary>
public sealed class BatchProcessWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<BatchProcessWorker> _logger;
    private readonly Dictionary<string, DateTime> _lastRun = new(StringComparer.OrdinalIgnoreCase);
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
        _logger.LogInformation("BatchProcessWorker 시작 — poll {Poll}s, 규약: BATCH_TYPE='Interval'+BATCH_OPTIONS=초.", pollSeconds);

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
            if (!string.Equals(type, "Interval", StringComparison.OrdinalIgnoreCase))
            {
                if (_warnedUnsupported.Add(batchId))
                    _logger.LogWarning("배치 '{BatchId}' 유형 '{Type}'은 v1 워커가 실행하지 않습니다(Interval만) — 정의는 보존.", batchId, type);
                continue;
            }
            if (!int.TryParse(Str(def, "BATCH_OPTIONS"), out var intervalSeconds) || intervalSeconds <= 0)
            {
                if (_warnedUnsupported.Add(batchId))
                    _logger.LogWarning("배치 '{BatchId}' BATCH_OPTIONS='{Options}' 파싱 불가 — Interval 유형은 초(정수)여야 합니다.", batchId, Str(def, "BATCH_OPTIONS"));
                continue;
            }

            var now = DateTime.UtcNow;
            if (_lastRun.TryGetValue(batchId, out var last) && (now - last).TotalSeconds < intervalSeconds)
                continue;

            _lastRun[batchId] = now;
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
