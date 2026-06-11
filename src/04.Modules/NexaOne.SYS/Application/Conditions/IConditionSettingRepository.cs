using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Conditions;

public interface IConditionSettingRepository
{
    Task<IReadOnlyList<ConditionSetting>> GetByMenuAsync(string userId, string menuId, CancellationToken ct = default);
    Task<ConditionSetting?> GetAsync(string userId, string menuId, string name, CancellationToken ct = default);
    Task UpsertAsync(ConditionSetting setting, CancellationToken ct = default);
    Task DeleteAsync(string userId, string menuId, string name, CancellationToken ct = default);
}
