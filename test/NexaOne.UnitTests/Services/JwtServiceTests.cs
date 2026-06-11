using Microsoft.Extensions.Configuration;
using NexaOne.API.Services;

namespace NexaOne.UnitTests.Services;

/// <summary>§20.10 — pwdChange 클레임 발급/검증. 갱신 시 클레임 승계와
/// 미들웨어 차단 판정이 이 클레임에 의존한다.</summary>
public sealed class JwtServiceTests
{
    private static JwtService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "UNIT_TEST_SECRET_KEY_0123456789ABCDEF_32B+",
                ["Jwt:Issuer"] = "NexaOne",
                ["Jwt:Audience"] = "NexaOne"
            })
            .Build();
        return new JwtService(config);
    }

    [Fact]
    public void GenerateAccessToken_with_requirePasswordChange_includes_pwdChange_claim()
    {
        var svc = Build();

        var token = svc.GenerateAccessToken("u001", "Alice", "P1", new[] { "OPERATOR" }, requirePasswordChange: true);
        var principal = svc.ValidateAccessToken(token);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtService.PasswordChangeClaim)?.Value.Should().Be("true");
    }

    [Fact]
    public void GenerateAccessToken_default_omits_pwdChange_claim()
    {
        var svc = Build();

        var token = svc.GenerateAccessToken("u001", "Alice", "P1", new[] { "OPERATOR" });
        var principal = svc.ValidateAccessToken(token);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtService.PasswordChangeClaim).Should().BeNull(
            "정상 상태 토큰에 클레임이 실리면 모든 사용자가 차단된다");
    }

    [Fact]
    public void ValidateAccessToken_rejects_token_signed_with_other_key()
    {
        var other = new JwtService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "ANOTHER_SECRET_KEY_0123456789ABCDEF_32BYTES",
                ["Jwt:Issuer"] = "NexaOne",
                ["Jwt:Audience"] = "NexaOne"
            }).Build());

        var token = other.GenerateAccessToken("u001", "Alice", "P1", new[] { "OPERATOR" });

        Build().ValidateAccessToken(token).Should().BeNull();
    }
}
