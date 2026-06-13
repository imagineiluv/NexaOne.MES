using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcAlarmConfigRepository
{
    Task<IReadOnlyList<FdcAlarmConfig>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<FdcAlarmConfig>> GetActiveConfigsAsync(string equipmentId, string parameterId, CancellationToken ct = default);
    Task AddAsync(FdcAlarmConfig config, CancellationToken ct = default);
    Task UpdateAsync(FdcAlarmConfig config, CancellationToken ct = default);
}
