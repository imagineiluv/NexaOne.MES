using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Users;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetAllActiveAsync(CancellationToken ct = default);
    Task<bool> ExistsAsync(string userId, CancellationToken ct = default);
    Task AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);

    /// <summary>§20.10 — 로그인 실패 카운터를 원자적으로 증가시킨다. read-modify-write로는
    /// 동시 실패 시 증가가 유실되고, 전체 행 UPDATE는 다른 컬럼을 덮어쓸 수 있다.
    /// 의미론은 User.RecordLoginFailure와 동일하며, 갱신 후 잠금 만료 시각을 반환한다(잠기지 않았으면 null).</summary>
    Task<DateTime?> RecordLoginFailureAsync(string userId, DateTime utcNow, CancellationToken ct = default);
}
