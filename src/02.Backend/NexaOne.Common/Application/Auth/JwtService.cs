using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace NexaOne.Application.Auth;

public class JwtService : IJwtService
{
    /// <summary>§20.10 — 비밀번호 변경 강제 표시 클레임. 존재하면 업무 API를 차단한다.</summary>
    public const string PasswordChangeClaim = "pwdChange";

    private readonly IConfiguration _config;

    public JwtService(IConfiguration config) => _config = config;

    // Program.cs의 JwtBearer 검증과 동일한 키(Jwt:SecretKey)를 사용해야 한다 —
    // 키 경로가 어긋나면 발급 토큰이 전부 401이 된다.
    private SymmetricSecurityKey SigningKey()
        => new(Encoding.UTF8.GetBytes(_config["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey is required")));

    public string GenerateAccessToken(string userId, string userName, string plantId, IEnumerable<string> roles,
        bool requirePasswordChange = false, IEnumerable<string>? permissions = null)
    {
        var creds = new SigningCredentials(SigningKey(), SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Name, userName),
            new("plantId", plantId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        // ADR-003 — permission 클레임 발급(권한 기반 PEP). 중복 제거.
        if (permissions is not null)
            claims.AddRange(permissions.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));

        if (requirePasswordChange)
            claims.Add(new Claim(PasswordChangeClaim, "true"));

        var expiryHours = _config.GetValue("Jwt:AccessTokenExpiryHours", 8);
        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expiryHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    public ClaimsPrincipal? ValidateAccessToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();

        try
        {
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = SigningKey(),
                ValidateIssuer = true,
                ValidIssuer = _config["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _config["Jwt:Audience"],
                ValidateLifetime = false
            }, out _);
        }
        catch
        {
            return null;
        }
    }
}
