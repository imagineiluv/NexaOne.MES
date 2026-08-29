using NexaFramework.Scheduling;

namespace NexaOne.Server.Gateway;

/// <summary>배치 스케줄 재조정 워커(기본 OFF — Worker:Sys:BatchProcess:Enabled). 배치 정의를 직접 폴링해
/// 실행하지 않고, 공유 Quartz 스케줄러(IRecurringScheduler)에 정의를 잡으로 '등록'한다 — 실제 발화·중복
/// 방지(DisallowConcurrentExecution)·misfire는 Quartz가 맡는다. 스케줄 규약:
/// BATCH_TYPE='Interval' + BATCH_OPTIONS=주기(초) 또는 BATCH_TYPE='Cron' + BATCH_OPTIONS=Quartz cron식
/// (6~7필드 초 분 시 일 월 요일 [연], 요일 1=일, dom·dow 중 한쪽 '?' — CronSchedule 참조, UTC 발화).
/// 정의가 DB에서 동적으로 바뀌므로 주기적으로 재조정(reconcile)한다: 신규/변경은 (재)등록, 사라진(삭제·
/// 무효) 정의는 언스케줄. 실행 자체는 BatchProcessRunner 단일 경로(수동 run API와 동일 규약).
/// (스케줄러 미배선(modules OFF)이면 IRecurringScheduler가 null로 주입돼 주기 실행 없이 조용히 비활성 —
///  수동 run API만 동작.)</summary>
public sealed class BatchProcessWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IRecurringScheduler? _scheduler;
    private readonly IConfiguration _config;
    private readonly ILogger<BatchProcessWorker> _logger;
    private int _schedulerStarted;
    // 등록된 배치 → 스케줄 시그니처("I:{초}" 또는 "C:{식}"). 시그니처가 같으면 재등록하지 않는다
    // (Interval 재등록은 StartNow로 즉시 재발화하므로, 변경 없는 정의의 불필요한 재발화를 막는다).
    private readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedUnsupported = new(StringComparer.OrdinalIgnoreCase);

    public BatchProcessWorker(
        IServiceScopeFactory scopes, IRecurringScheduler? scheduler, IConfiguration config, ILogger<BatchProcessWorker> logger)
    {
        _scopes = scopes;
        _scheduler = scheduler;
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
        if (_scheduler is null)
        {
            _logger.LogWarning("BatchProcessWorker — 공유 Quartz 스케줄러 미배선(modules OFF?) — 주기 실행 불가, 수동 실행만 동작.");
            return;
        }

        var reconcileSeconds = Math.Max(5, _config.GetValue("Worker:Sys:BatchProcess:PollSeconds", 30));
        _logger.LogInformation(
            "BatchProcessWorker 시작(Quartz 잡 등록) — 재조정 {Reconcile}s, 규약: Interval(초) / Cron(Quartz 식).", reconcileSeconds);

        await _scheduler.StartAsync(stoppingToken);
        Volatile.Write(ref _schedulerStarted, 1);

        // 시작 즉시 1회 재조정 후 주기 반복(신규/변경 등록·삭제 언스케줄).
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(reconcileSeconds));
        do
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "배치 스케줄 재조정 실패 — 다음 주기에 재시도."); }
        }
        while (await WaitSafeAsync(timer, stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_scheduler is not null && Interlocked.Exchange(ref _schedulerStarted, 0) == 1)
            await _scheduler.StopAsync(cancellationToken);
    }

    // 현재 유효 정의(ListDefinitionsAsync=Valid만)에서 원하는 스케줄 집합을 만들고, 등록 상태와 비교해
    // 신규/변경은 (재)등록, 목록에서 사라진(삭제·무효화·미지원) 배치는 언스케줄한다.
    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<BatchProcessRunner>();

        var desired = BuildDesired(await runner.ListDefinitionsAsync(ct), out var unsupported);
        foreach (var (batchId, reason) in unsupported) WarnUnsupported(batchId, reason);

        // 신규/변경 등록(시그니처 동일 시 재등록 금지 — 즉시 재발화 방지).
        foreach (var (batchId, sig) in desired)
        {
            if (_registered.TryGetValue(batchId, out var current) && string.Equals(current, sig, StringComparison.Ordinal))
                continue;

            var jobName = JobName(batchId);
            var id = batchId;   // 델리게이트 캡처(루프 변수 캡처 안전).
            if (sig[0] == 'I')
                await _scheduler!.ScheduleRecurringAsync(jobName, TimeSpan.FromSeconds(int.Parse(sig[2..])), c => RunBatchAsync(id, c), ct);
            else
                await _scheduler!.ScheduleRecurringCronAsync(jobName, sig[2..], c => RunBatchAsync(id, c), ct);

            _registered[batchId] = sig;
            _warnedUnsupported.Remove(batchId);   // 재유효화 시 경고 상태 리셋
            _logger.LogInformation("배치 '{BatchId}' 스케줄 등록 — {Signature}.", batchId, sig);
        }

        // 목록에서 사라진(삭제·무효·미지원) 등록분 언스케줄.
        foreach (var batchId in _registered.Keys.Where(k => !desired.ContainsKey(k)).ToList())
        {
            await _scheduler!.UnscheduleAsync(JobName(batchId), ct);
            _registered.Remove(batchId);
            _logger.LogInformation("배치 '{BatchId}' 스케줄 해제 — 정의 제거/무효.", batchId);
        }
    }

    // Quartz 잡 발화 시 호출되는 실행 델리게이트 — 새 스코프에서 Runner로 실행(수동 run과 동일 경로).
    // 델리게이트 예외는 QuartzScheduler가 삼켜 트리거를 유지하므로 여기선 결과 로깅만 한다.
    private async Task RunBatchAsync(string batchId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<BatchProcessRunner>();
        var result = await runner.RunAsync(batchId, "BATCH", ct);
        if (result is { Success: true })
            _logger.LogInformation("배치 '{BatchId}' 실행 완료 — affected={Affected}.", batchId, result.Affected);
        else if (result is null)
            _logger.LogWarning("배치 '{BatchId}' 정의를 찾지 못함(실행 시점 제거?) — 스킵.", batchId);
        // 실패(Success=false)는 Runner가 이력/로그로 남긴다.
    }

    // 정의 목록(ListDefinitionsAsync=Valid만) → 원하는 스케줄 집합(batchId → 시그니처 "I:{초}"/"C:{식}")과
    // 미지원 목록. Quartz 등록/DB에 무관한 순수 함수 — 유형 판별·Interval 초 검증·Quartz cron 사전 검증을 격리 테스트한다.
    internal static Dictionary<string, string> BuildDesired(
        IReadOnlyList<Dictionary<string, object?>> definitions, out List<(string BatchId, string Reason)> unsupported)
    {
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        unsupported = new List<(string, string)>();
        foreach (var def in definitions)
        {
            var batchId = Str(def, "BATCH_ID");
            if (batchId.Length == 0) continue;
            var type = Str(def, "BATCH_TYPE");
            var options = Str(def, "BATCH_OPTIONS");

            if (string.Equals(type, "Interval", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(options, out var seconds) && seconds > 0)
                    desired[batchId] = $"I:{seconds}";
                else
                    unsupported.Add((batchId, $"BATCH_OPTIONS='{options}' 파싱 불가 — Interval 유형은 초(양의 정수)여야 합니다."));
            }
            else if (string.Equals(type, "Cron", StringComparison.OrdinalIgnoreCase))
            {
                if (CronSchedule.TryParse(options, out _))   // Quartz 방언 사전 검증(잘못된 식은 등록 안 함)
                    desired[batchId] = $"C:{options}";
                else
                    unsupported.Add((batchId, $"cron식 '{options}' 파싱 불가 — Quartz 6~7필드(초 분 시 일 월 요일 [연], dom·dow 중 한쪽 '?')여야 합니다."));
            }
            else
                unsupported.Add((batchId, $"유형 '{type}'은 워커가 실행하지 않습니다(Interval/Cron만) — 정의는 보존."));
        }
        return desired;
    }

    private void WarnUnsupported(string batchId, string reason)
    {
        if (_warnedUnsupported.Add(batchId))
            _logger.LogWarning("배치 '{BatchId}' {Reason}", batchId, reason);
    }

    private static string JobName(string batchId) => $"batch:{batchId}";

    private static async Task<bool> WaitSafeAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private static string Str(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";
}
