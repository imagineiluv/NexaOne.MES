using NexaOne.Common;

namespace NexaOne.ServiceContracts.Sys;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — SYS 비-자격증명 단일 애그리거트 쓰기(역할 관리 + 사용자 등록
/// 신청 반려 + 사용자 비활성). plugin(SYS)이 구현하고 호스트가 GetBean→캐스트로 Default-ALC DI에 등록한다.
/// Result로 팩토리/상태전이 검증을 분기(Conflict/Validation/NotFound/Success)한다.
///
/// 보안 가드(S7): 자격증명(PASSWORD_HASH)·로그인/리프레시·비밀번호 변경은 격리 인증 경로(AuthController +
/// GatewayLoginService + db/queries-auth)가 소유하므로 본 브리지는 절대 다루지 않는다. 다중 애그리거트
/// (UserRegistration Approve = 사용자 생성 + 신청 승인 연쇄), 사용자 잠금 해제(Unlock, 잠금 보안 상태)는
/// UnitOfWork/인증 경계 선결로 본 브리지에서 제외한다. 순수 조회는 게이트웨이(SYS.xml — PASSWORD_HASH 제외)로.</summary>
public interface ISysBridge
{
    // ── 역할(Role) 관리 — 단일 애그리거트, 비-자격증명 ──
    Task<Result<RoleDto>> CreateRoleAsync(string roleId, string roleName, string description, CancellationToken ct = default);
    Task<Result> AddPermissionAsync(string roleId, string permission, CancellationToken ct = default);
    Task<Result> RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default);

    // ── 사용자 등록 신청(UserRequest) 반려 — 단일 애그리거트, 비-자격증명(Approve는 다중 애그리거트라 제외) ──
    Task<Result<UserRequestDto>> RejectRequestAsync(string requestId, string rejectedBy, string reason, CancellationToken ct = default);

    // ── 사용자 비활성(상태전이) — 단일 애그리거트, 비밀번호 무관(소프트 삭제) ──
    Task<Result> DeactivateUserAsync(string userId, CancellationToken ct = default);
}
