using NexaOne.Common;

namespace NexaOne.ServiceContracts.Sys;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — SYS 비-자격증명 쓰기(역할 관리 + 사용자 등록 신청 생명주기 +
/// 사용자 비활성). plugin(SYS)이 구현하고 호스트가 GetBean→캐스트로 Default-ALC DI에 등록한다.
/// Result로 팩토리/상태전이 검증을 분기(Conflict/Validation/NotFound/Success)한다.
///
/// 보안 가드(S7): 자격증명(PASSWORD_HASH)·로그인/리프레시·비밀번호 변경은 격리 인증 경로(AuthController +
/// GatewayLoginService + db/queries-auth)가 소유하므로 본 브리지는 평문 자격증명을 절대 다루지 않는다.
/// 승인(Approve)은 DATA-6 원자화(ApprovePersistAsync 단일 트랜잭션)로 다중 애그리거트 제약이 해소되어 노출하며,
/// 호스트가 생성·해싱한 임시 비밀번호의 "해시"만 전달받는다(평문은 호스트 응답/메일에만 존재).
/// 사용자 잠금 해제(Unlock)는 잠금 보안 상태 소유권(인증 경로) 문제로 계속 제외한다. 순수 조회는
/// 게이트웨이(SYS.xml — PASSWORD_HASH 제외)로 가되, 신청 목록(§19.3.4)은 검증 포함 서비스 조회를 노출한다.</summary>
public interface ISysBridge
{
    // ── 역할(Role) 관리 — 단일 애그리거트, 비-자격증명 ──
    Task<Result<RoleDto>> CreateRoleAsync(string roleId, string roleName, string description, CancellationToken ct = default);
    Task<Result> AddPermissionAsync(string roleId, string permission, CancellationToken ct = default);
    Task<Result> RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default);

    // ── 사용자 등록 신청(UserRequest) 생명주기 — §19.3 신청/조회/승인/반려 ──
    Task<bool> IsUserIdAvailableAsync(string userId, CancellationToken ct = default);
    Task<Result<UserRequestDto>> CreateRequestAsync(UserRegistrationRequestDto request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<UserRequestDto>>> GetRequestsAsync(
        string? plantId = null, string? status = null, string? userId = null,
        string? userName = null, string? email = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
    Task<Result<UserRequestDto>> ApproveRequestAsync(
        string requestId, string roleId, string approvedBy, string tempPasswordHash, CancellationToken ct = default);
    Task<Result<UserRequestDto>> RejectRequestAsync(string requestId, string rejectedBy, string reason, CancellationToken ct = default);

    // ── 사용자 비활성(상태전이) — 단일 애그리거트, 비밀번호 무관(소프트 삭제) ──
    Task<Result> DeactivateUserAsync(string userId, CancellationToken ct = default);
}
