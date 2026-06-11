using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Users;

public interface ILoginFailureHistoryRepository
{
    Task AddAsync(LoginFailureHistory history, CancellationToken ct = default);
    Task<IReadOnlyList<LoginFailureHistory>> GetRecentByUserAsync(string userId, int count, CancellationToken ct = default);
}
