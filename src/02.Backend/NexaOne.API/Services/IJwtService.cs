namespace NexaOne.API.Services;

public interface IJwtService
{
    /// <summary>액세스 토큰 발급. requirePasswordChange=true면 pwdChange 클레임을 실어
    /// 비밀번호 변경 전 업무 API 차단에 사용한다 (§20.10).</summary>
    string GenerateAccessToken(string userId, string userName, string plantId, IEnumerable<string> roles,
        bool requirePasswordChange = false);
    string GenerateRefreshToken();
    System.Security.Claims.ClaimsPrincipal? ValidateAccessToken(string token);
}
