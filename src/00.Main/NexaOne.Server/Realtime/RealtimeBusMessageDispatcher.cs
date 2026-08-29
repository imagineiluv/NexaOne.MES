using NexaOne.Infrastructure.Messaging;

namespace NexaOne.Server.Realtime;

/// <summary>
/// Owns the transport-neutral conversion from a domain event to SignalR, live-screen refresh,
/// and shell alerts. In-memory and Kafka subscribers use this exact same path.
/// </summary>
internal sealed class RealtimeBusMessageDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScreenRefreshNotifier? _screenRefresh;
    private readonly RealtimeAlertFeed? _alertFeed;

    public RealtimeBusMessageDispatcher(
        IServiceScopeFactory scopeFactory,
        ScreenRefreshNotifier? screenRefresh = null,
        RealtimeAlertFeed? alertFeed = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _screenRefresh = screenRefresh;
        _alertFeed = alertFeed;
    }

    public async Task DispatchAsync(DomainEventMessage message, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IEesHubNotifier>();
        await RealtimeNotificationDispatch.DispatchAsync(
            notifier, message.EventType, message.AggregateId, message.Payload, cancellationToken);

        if (_screenRefresh is not null)
            await _screenRefresh.NotifyAsync();

        if (_alertFeed is not null
            && RealtimeAlertFeed.ToAlert(message.EventType, message.AggregateId) is { } alert)
            await _alertFeed.PublishAsync(alert);
    }
}
