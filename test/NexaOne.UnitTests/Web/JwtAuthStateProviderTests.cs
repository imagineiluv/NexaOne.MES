using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Web.Services.Auth;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// JWT 인증상태 파싱(JwtAuthStateProvider.ParseToken) 검증. 핵심 회귀: API가 사용자명을 'name' 클레임으로
/// 발급하므로(JwtService), 파서가 nameType="name"을 명시해야 Identity.Name이 채워진다(상단바 사용자명 공백 버그).
/// </summary>
public sealed class JwtAuthStateProviderTests
{
    // API JwtService와 동일한 클레임 형태(name=사용자명, role=ClaimTypes.Role)로 서명 토큰을 만든다.
    private static string MakeToken(string userName, string[] roles, DateTime expires)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "u1"), new(JwtRegisteredClaimNames.Name, userName) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("unit-test-only-signing-key-at-least-32-bytes!!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(claims: claims, expires: expires, signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void ParseToken_maps_name_claim_to_identity_name()
    {
        var principal = JwtAuthStateProvider.ParseToken(MakeToken("Alice", new[] { "ADMIN" }, DateTime.UtcNow.AddHours(1)));

        principal.Should().NotBeNull();
        principal!.Identity!.IsAuthenticated.Should().BeTrue();
        principal.Identity.Name.Should().Be("Alice",
            "API가 'name' 클레임으로 발급한 사용자명이 Identity.Name으로 매핑돼야 한다(상단바 표시) — nameType 미지정 시 null");
    }

    [Fact]
    public void ParseToken_returns_null_for_expired_token()
        => JwtAuthStateProvider.ParseToken(MakeToken("Bob", Array.Empty<string>(), DateTime.UtcNow.AddMinutes(-5)))
            .Should().BeNull("만료 토큰은 익명(null)으로 처리돼야 한다");

    [Fact]
    public void ParseToken_returns_null_for_non_jwt_garbage()
        => JwtAuthStateProvider.ParseToken("not-a-jwt-string").Should().BeNull("비-JWT 문자열은 익명(null)이어야 한다");
}
