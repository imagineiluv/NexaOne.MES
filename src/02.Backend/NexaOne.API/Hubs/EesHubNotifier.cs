using Microsoft.AspNetCore.SignalR;

namespace NexaOne.API.Hubs;

public sealed class EesHubNotifier : IEesHubNotifier
{
    private readonly IHubContext<NexaOneEESHub> _hub;

    public EesHubNotifier(IHubContext<NexaOneEESHub> hub) => _hub = hub;

    public Task NotifyAlarmUpdatedAsync(CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("AlarmUpdated", ct);

    public Task NotifyWorkOrderUpdatedAsync(CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("WorkOrderUpdated", ct);

    public Task NotifyDashboardRefreshAsync(CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("DashboardRefresh", ct);

    public Task NotifyEquipmentStateChangedAsync(string equipmentId, string newState, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("EquipmentStateChanged", new { equipmentId, newState }, ct);

    public Task NotifyFdcDataReceivedAsync(string equipmentId, string parameterId, decimal value, bool isOutOfSpec, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("FdcDataReceived", new { equipmentId, parameterId, value, isOutOfSpec }, ct);

    public Task NotifyInterlockTriggeredAsync(string equipmentId, string parameterId, string action, string message, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("InterlockTriggered", new { equipmentId, parameterId, action, message }, ct);
}
