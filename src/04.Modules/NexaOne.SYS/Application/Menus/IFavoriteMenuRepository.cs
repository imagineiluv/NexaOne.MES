using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Menus;

public interface IFavoriteMenuRepository
{
    Task<IReadOnlyList<FavoriteMenu>> GetByUserAsync(string userId, CancellationToken ct = default);
    Task UpsertAsync(FavoriteMenu favorite, CancellationToken ct = default);
    Task DeleteAsync(string userId, string menuId, CancellationToken ct = default);
}
