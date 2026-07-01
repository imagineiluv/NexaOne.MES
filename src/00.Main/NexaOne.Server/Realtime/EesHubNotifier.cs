using Microsoft.AspNetCore.SignalR;

namespace NexaOne.Server.Realtime;

/// <summary>도메인 이벤트/컨트롤러가 UI로 밀어 넣는 실시간 알림 계약. 폐기된 NexaOne.API에서 이식.</summary>
public interface IEesHubNotifier
{
    Task NotifyAlarmUpdatedAsync(CancellationToken ct = default);
    Task NotifyWorkOrderUpdatedAsync(CancellationToken ct = default);
    Task NotifyDashboardRefreshAsync(CancellationToken ct = default);
    Task NotifyEquipmentStateChangedAsync(string equipmentId, string newState, CancellationToken ct = default);
    Task NotifyFdcDataReceivedAsync(string equipmentId, string parameterId, decimal value, bool isOutOfSpec, CancellationToken ct = default);
    Task NotifyInterlockTriggeredAsync(string equipmentId, string parameterId, string action, string message, CancellationToken ct = default);
}

/// <summary>IHubContext&lt;NexaOneEESHub&gt;를 통해 연결된 모든 클라이언트에 이벤트를 브로드캐스트한다.</summary>
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
