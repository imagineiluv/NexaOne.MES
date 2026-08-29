using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcAlarmHistoryRepository
{
    Task<IReadOnlyList<FdcAlarmHistory>> GetByEquipmentAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<IReadOnlyList<FdcAlarmHistory>> GetOpenAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<FdcAlarmHistory>> GetOpenAsync(
        string equipmentId,
        string parameterId,
        CancellationToken ct = default);
    Task AddAsync(FdcAlarmHistory history, CancellationToken ct = default);
    Task UpdateAsync(FdcAlarmHistory history, CancellationToken ct = default);
}
