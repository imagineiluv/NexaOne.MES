namespace NexaOne.ServiceContracts.Sys;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단). 보안 가드(S7): SYS는 인증 경로가
// 소유하는 자격증명(PASSWORD_HASH)·로그인/리프레시 토큰을 절대 계약에 싣지 않는다. 본 브리지가 다루는 단일
// 애그리거트(Role/MenuRequest/User 상태전이)는 비-자격증명 데이터만 노출한다.

/// <summary>역할(SYS_ROLE) 스냅샷 — 권한 문자열 목록 포함. 자격증명 무관.</summary>
public record RoleDto(string RoleId, string RoleName, string Description, IReadOnlyList<string> Permissions);

/// <summary>사용자 등록 신청(SYS_USER_REQUEST) 스냅샷 — 비밀번호 컬럼 없음(승인 시 SYS_USER에 생성, 본 브리지 제외).
/// 신청 생명주기 단일 애그리거트(반려)만 본 브리지가 다룬다.</summary>
public record UserRequestDto(
    string RequestId, string UserId, string UserName, string Email, string Department, string Position,
    string PlantId, string Status, int RequestVersion, string? RejectReason, string? RejectedBy);
