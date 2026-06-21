using NexaOne.EST.Domain;

namespace NexaOne.EST.Application.Est;

public interface IEquipmentAlarmRepository
{
    Task<EquipmentAlarm?> GetByIdAsync(string alarmId, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentAlarm>> GetByEquipmentAsync(string equipmentId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentAlarm>> GetActiveAlarmsByEquipmentIdsAsync(IReadOnlyList<string> equipmentIds, CancellationToken ct = default);
    Task<int> GetActiveAlarmCountAsync(CancellationToken ct = default);
    Task AddAsync(EquipmentAlarm alarm, CancellationToken ct = default);
    Task UpdateAsync(EquipmentAlarm alarm, CancellationToken ct = default);
}
