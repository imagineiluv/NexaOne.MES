using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Menus;

public interface IRecentMenuRepository
{
    Task<IReadOnlyList<RecentMenu>> GetByUserAsync(string userId, CancellationToken ct = default);
    Task UpsertAsync(RecentMenu recent, CancellationToken ct = default);
    Task DeleteAsync(string userId, string menuId, CancellationToken ct = default);
}
