namespace NexaOne.API.Hubs;

public interface IEesHubNotifier
{
    Task NotifyAlarmUpdatedAsync(CancellationToken ct = default);
    Task NotifyWorkOrderUpdatedAsync(CancellationToken ct = default);
    Task NotifyDashboardRefreshAsync(CancellationToken ct = default);
    Task NotifyEquipmentStateChangedAsync(string equipmentId, string newState, CancellationToken ct = default);
    Task NotifyFdcDataReceivedAsync(string equipmentId, string parameterId, decimal value, bool isOutOfSpec, CancellationToken ct = default);
    Task NotifyInterlockTriggeredAsync(string equipmentId, string parameterId, string action, string message, CancellationToken ct = default);
}
