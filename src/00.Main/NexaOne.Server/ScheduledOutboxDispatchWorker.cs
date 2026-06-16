using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.Common.Telemetry;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;
using NexusFramework.Scheduling;

namespace NexaOne.Server;

/// <summary>
/// 실 반복 스케줄러(<see cref="IRecurringScheduler"/>, Quartz)로 구동되는 Outbox 디스패치 워커(ADR-002, ADR-006).
/// 미발행 이벤트를 주기 폴링해 <see cref="IMessageBus"/>로 발행하고 성공/실패를 표시한다.
/// </summary>
/// <remarks>
/// 게이트(<c>enabled</c>) 기본 OFF. API 티어의 <c>NexaOne.API.Services.OutboxDispatcherService</c>(Task.Delay 루프)와
/// 디스패치 의미는 동일하지만, 구동을 스케줄러로 옮긴 변형이다. 두 워커의 동시 가동은 프로세스 분리(API vs Server)와
/// 게이트 OFF 기본으로 차단된다. 배치 처리 로직은 그 API 디스패처의 <c>DispatchBatchAsync</c> 의미를 그대로 따른다
/// (성공만 published, 실패는 attempts 증가·지수 백오프, 한계 도달 시 데드레터). NexaOne.Server는 Web SDK인
/// NexaOne.API를 참조하지 않으므로(헤드리스 서버 — 웹 티어 비결합) 그 static 메서드를 직접 호출하지 않고 동일 로직을
/// 재현한다. 출처: NexaOne.API.Services.OutboxDispatcherService.DispatchBatchAsync.
/// </remarks>
public sealed class ScheduledOutboxDispatchWorker : BackgroundService
{
    private readonly IRecurringScheduler _scheduler;
    private readonly IOutboxRepository _repo;
    private readonly IMessageBus _bus;
    private readonly bool _enabled;
    private readonly int _intervalSeconds;
    private readonly string _topic;
    private readonly int _batchSize;
    private readonly int _maxAttempts;
    private readonly ILogger _logger;

    public ScheduledOutboxDispatchWorker(
        IRecurringScheduler scheduler,
        IOutboxRepository repo,
        IMessageBus bus,
        bool enabled,
        int intervalSeconds = 5,
        string topic = "nexaone.events",
        int batchSize = 100,
        int maxAttempts = 10)
    {
        _scheduler = scheduler;
        _repo = repo;
        _bus = bus;
        _enabled = enabled;
        _intervalSeconds = intervalSeconds;
        _topic = topic;
        _batchSize = batchSize;
        _maxAttempts = maxAttempts;
        // server.xml 빈 생성 경로라 DI ILogger가 없다 — NullLogger로 무해화(필요 시 콘솔 로깅으로 대체 가능).
        _logger = NullLogger.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[ScheduledOutboxDispatchWorker] disabled (enabled=false). Skipping startup.");
            return;
        }

        Console.WriteLine(
            $"[ScheduledOutboxDispatchWorker] started (topic={_topic}, interval={_intervalSeconds}s, " +
            $"batchSize={_batchSize}, maxAttempts={_maxAttempts}).");

        await _scheduler.StartAsync(stoppingToken);
        await _scheduler.ScheduleRecurringAsync(
            "outbox-dispatch",
            TimeSpan.FromSeconds(_intervalSeconds),
            DispatchBatchAsync,
            stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    /// <summary>미발행 한 배치를 발행·표시한다. 발행 성공만 published, 실패는 attempts 증가(지수 백오프),
    /// 한계 도달 시 데드레터 경고. 출처: NexaOne.API.Services.OutboxDispatcherService.DispatchBatchAsync.</summary>
    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        var pending = await _repo.GetUnpublishedAsync(_batchSize, _maxAttempts, ct);

        foreach (var m in pending)
        {
            try
            {
                await _bus.PublishAsync(_topic,
                    DomainEventMessage.Create(m.EventType, m.Module, m.AggregateId, m.Payload), ct);
                await _repo.MarkPublishedAsync(m.Id, ct);
                NexaMesMetrics.OutboxPublished.Add(
                    1, new KeyValuePair<string, object?>("eventType", m.EventType));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // ATTEMPTS 증가 + 지수 백오프 NEXT_ATTEMPT_AT 설정, 한계 도달 시 DEAD_LETTERED_AT 설정(단말).
                await _repo.MarkFailedAsync(m.Id, m.Attempts, _maxAttempts, ct);
                if (m.Attempts + 1 >= _maxAttempts)
                {
                    NexaMesMetrics.OutboxDeadLettered.Add(
                        1, new KeyValuePair<string, object?>("eventType", m.EventType));
                    _logger.LogError(ex,
                        "Outbox message DEAD-LETTERED after {Attempts} attempts: {Id} ({EventType}). 수동 조치 필요.",
                        m.Attempts + 1, m.Id, m.EventType);
                }
                else
                    _logger.LogError(ex, "Outbox publish failed for {Id} ({EventType}), attempt {Attempts}/{Max}",
                        m.Id, m.EventType, m.Attempts + 1, _maxAttempts);
            }
        }
    }
}
