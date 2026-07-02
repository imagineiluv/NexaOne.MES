namespace NexaOne.ServiceContracts.Sys;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단). 보안 가드(S7): SYS는 인증 경로가
// 소유하는 자격증명(PASSWORD_HASH)·로그인/리프레시 토큰을 절대 계약에 싣지 않는다. 승인(ApproveRequestAsync)은
// 호스트가 생성한 임시 비밀번호의 "해시"만 전달받으므로 평문 자격증명이 브리지를 넘지 않는다.

/// <summary>역할(SYS_ROLE) 스냅샷 — 권한 문자열 목록 포함. 자격증명 무관.</summary>
public record RoleDto(string RoleId, string RoleName, string Description, IReadOnlyList<string> Permissions);

/// <summary>사용자 등록 신청(SYS_USER_REQUEST) 스냅샷 — 비밀번호 컬럼 없음(승인 시 SYS_USER에 생성).
/// §19.3.4 승인 화면이 필요로 하는 전 필드(연락처/약관/처리 이력)를 노출한다.</summary>
public record UserRequestDto(
    string RequestId, string UserId, string UserName, string Email, string Department, string Position,
    string? Duty, string PlantId, string Language, string? CellPhoneNumber, string? Address,
    string? Description, string? Nickname, string Status, int RequestVersion,
    DateTime RequestedAt, DateTime TermsAcceptedAt,
    string? ApprovedBy, DateTime? ApprovedAt, string? RejectReason, string? RejectedBy, DateTime? RejectedAt);

/// <summary>§19.3.3 — 등록 신청 입력(익명 가입 경로). 비밀번호 없음 — 승인 시 임시 비밀번호가 발급되고
/// 최초 로그인에서 변경이 강제된다(PasswordState=Create). Language는 "ko-KR" 형식 문자열(미지원 값은 ko-KR로 저하).</summary>
public record UserRegistrationRequestDto(
    string UserId, string UserName, string Email, string Department, string Position,
    string PlantId, string Language, bool TermsAccepted, string TermsAcceptedIp,
    string? Duty = null, string? CellPhoneNumber = null, string? Address = null,
    string? Description = null, string? Nickname = null);
