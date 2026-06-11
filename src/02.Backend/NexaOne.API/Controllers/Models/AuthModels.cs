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
