namespace NexaOne.API.Controllers.Models;

public record LoginRequest(string UserId, string Password, string PlantId = "DEFAULT");

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string UserName,
    string PlantId,
    IReadOnlyList<string> Roles,
    bool RequirePasswordChange = false);

public record RefreshRequest(string UserId, string RefreshToken);

public record LogoutRequest(string UserId, string RefreshToken);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmPassword);

public record ResetPasswordRequest(string UserId, string Email);

public record ForgotPasswordRequest(string UserId, string Email);

// ── 응답 DTO(SPA 타입드 클라이언트) — 기존 익명 반환을 명명 타입으로 대체(JSON 형태 동일, camelCase). ──

/// <summary>토큰 갱신/비밀번호 변경 성공 시 재발급 토큰 쌍. 익명 { accessToken, refreshToken }와 동일 형태.</summary>
public record TokenRefreshResponse(string AccessToken, string RefreshToken);

/// <summary>안내 메시지 응답(분실/재설정 등). 익명 { message }와 동일 형태.</summary>
public record MessageResponse(string Message);

/// <summary>현재 인증 주체 요약(/auth/me). 익명 { userId, userName, plantId, roles }와 동일 형태.</summary>
public record CurrentUserResponse(string? UserId, string? UserName, string? PlantId, IReadOnlyList<string> Roles);
