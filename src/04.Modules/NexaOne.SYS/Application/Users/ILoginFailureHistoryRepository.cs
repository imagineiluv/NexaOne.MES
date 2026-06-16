using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Users;

public interface ILoginFailureHistoryRepository
{
    Task AddAsync(LoginFailureHistory history, CancellationToken ct = default);
    Task<IReadOnlyList<LoginFailureHistory>> GetRecentByUserAsync(string userId, int count, CancellationToken ct = default);
    /// <summary>발생시각(OCCURRED_AT)이 <paramref name="cutoff"/> 이전인 로그인실패 이력을 삭제하고 삭제 건수를 반환한다(보존정리용).</summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
