using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Users;

public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(string roleId, CancellationToken ct = default);
    Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default);
    Task<bool> ExistsAsync(string roleId, CancellationToken ct = default);
    Task AddAsync(Role role, CancellationToken ct = default);
    Task UpdateAsync(Role role, CancellationToken ct = default);
}
