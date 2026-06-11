using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Menus;

public interface IMenuRepository
{
    Task<IReadOnlyList<MenuItem>> GetAuthorizedMenusAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<MenuItem>> GetAllMenusAsync(CancellationToken ct = default);
    Task AddAsync(MenuItem item, CancellationToken ct = default);
    Task UpdateAsync(MenuItem item, CancellationToken ct = default);
}
