using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.API.Services;

/// <summary>
/// Transactional Outbox 디스패처(ADR-002). 미발행 이벤트를 폴링해 <see cref="IMessageBus"/>(Kafka)로 발행하고
/// 성공분을 발행 완료로 표시한다. <c>"Events:Outbox:Enabled"=true</c>일 때만 기동(브로커 없는 dev/CI 보호).
/// </summary>
public sealed class OutboxDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessageBus _bus;
    private readonly IConfiguration _config;
    private readonly ILogger<OutboxDispatcherService> _logger;

    public OutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        IMessageBus bus,
        IConfiguration config,
        ILogger<OutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Events:Outbox:Enabled", false))
        {
            _logger.LogInformation("Outbox dispatcher disabled (Events:Outbox:Enabled=false). Skipping startup.");
            return;
        }

        var topic = _config["Events:Outbox:Topic"] ?? "nexaone.events";
        var intervalMs = _config.GetValue("Events:Outbox:PollIntervalMs", 5_000);
        var batchSize = _config.GetValue("Events:Outbox:BatchSize", 100);
        _logger.LogInformation("Outbox dispatcher started (topic={Topic}, interval={Ms}ms).", topic, intervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
                await DispatchBatchAsync(repo, _bus, topic, batchSize, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatch loop error");
            }

            try { await Task.Delay(intervalMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>미발행 한 배치를 발행·표시한다(테스트 가능 코어). 발행 성공만 published, 실패는 attempts 증가.</summary>
    public static async Task<int> DispatchBatchAsync(
        IOutboxRepository repo, IMessageBus bus, string topic, int batchSize, ILogger logger, CancellationToken ct)
    {
        var pending = await repo.GetUnpublishedAsync(batchSize, ct);
        var published = 0;

        foreach (var m in pending)
        {
            try
            {
                await bus.PublishAsync(topic,
                    DomainEventMessage.Create(m.EventType, m.Module, m.AggregateId, m.Payload), ct);
                await repo.MarkPublishedAsync(m.Id, ct);
                published++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publish failed for {Id} ({EventType})", m.Id, m.EventType);
                await repo.MarkFailedAsync(m.Id, ct);
            }
        }

        return published;
    }
}
