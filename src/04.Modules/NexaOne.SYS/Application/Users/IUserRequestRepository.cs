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

    /// <summary>승인 일괄 영속(DATA-6 원자화, POM MixingPersistAsync 패턴) — SYS_USER INSERT + 신청 Approved
    /// UPDATE(+outbox)를 단일 트랜잭션으로 커밋한다. 어느 문장이 실패해도 전체 롤백되어 '사용자만 생성되고
    /// 신청이 대기로 남는' 부분 커밋이 불가능하다.</summary>
    Task ApprovePersistAsync(UserRequest request, User user, CancellationToken ct = default);
}
