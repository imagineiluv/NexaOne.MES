namespace NexaOne.Application.Auth;

public interface IRefreshTokenStore
{
    Task<string> IssueAsync(string userId);
    Task<bool> ValidateAsync(string userId, string token);
    Task<string> RotateAsync(string userId, string oldToken);
    Task RevokeAsync(string userId, string token);
    Task RevokeAllByUserAsync(string userId);
}
