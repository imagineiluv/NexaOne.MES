using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Users;

public interface IUserRequestRepository
{
    Task<UserRequest?> GetByIdAsync(string requestId, CancellationToken ct = default);

    /// <summary>사용자 ID로 신청 행 조회 — 사용자당 1행(UNIQUE)이므로 재신청 판정에 사용한다.</summary>
    Task<UserRequest?> GetByUserIdAsync(string userId, CancellationToken ct = default);

    /// <summary>§19.3.4 — 승인 화면 검색: Plant/상태/신청일 구간/ID/이름/이메일.</summary>
    Task<IReadOnlyList<UserRequest>> SearchAsync(
        string? plantId, string? status, string? userId, string? userName, string? email,
        DateTime? from, DateTime? to, CancellationToken ct = default);

    Task AddAsync(UserRequest request, CancellationToken ct = default);
    Task UpdateAsync(UserRequest request, CancellationToken ct = default);
}
