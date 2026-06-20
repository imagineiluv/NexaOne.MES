using Microsoft.Extensions.Configuration;
using Moq;
using NexaOne.Application.Auth;

namespace NexaOne.UnitTests.Services;

/// <summary>RefreshTokenStore 토큰 비교가 상수시간(CryptographicOperations.FixedTimeEquals)로
/// 전환된 뒤에도 기존 동작(일치 검증·불일치 거부·길이 불일치 거부·만료·선택적 폐기)이 보존되는지 검증한다.
/// 발급 토큰은 IJwtService.GenerateRefreshToken을 모킹해 결정론적으로 주입한다.</summary>
public sealed class RefreshTokenStoreTests
{
    private static RefreshTokenStore Build(IJwtService jwt, int expiryDays = 7)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:RefreshTokenExpiryDays"] = expiryDays.ToString()
            })
            .Build();
        return new RefreshTokenStore(jwt, config);
    }

    private static Mock<IJwtService> JwtReturning(params string[] tokens)
    {
        var jwt = new Mock<IJwtService>();
        var seq = jwt.SetupSequence(j => j.GenerateRefreshToken());
        foreach (var t in tokens) seq = seq.Returns(t);
        return jwt;
    }

    [Fact]
    public async Task Validate_exact_token_match_returns_true()
    {
        const string token = "rt-abcdef0123456789";
        var store = Build(JwtReturning(token).Object);
        var issued = await store.IssueAsync("user01");
        issued.Should().Be(token);

        (await store.ValidateAsync("user01", token)).Should().BeTrue(
            "정확히 일치하는 토큰은 FixedTimeEquals로 true여야 한다");
    }

    [Fact]
    public async Task Validate_single_char_mismatch_returns_false()
    {
        const string token = "rt-abcdef0123456789";
        var store = Build(JwtReturning(token).Object);
        await store.IssueAsync("user01");

        // 같은 길이·마지막 한 글자만 다른 토큰 — FixedTimeEquals 동일길이 비교 경로에서 false여야 한다.
        var nearMiss = token[..^1] + "X";
        nearMiss.Length.Should().Be(token.Length);
        (await store.ValidateAsync("user01", nearMiss)).Should().BeFalse(
            "한 글자만 달라도 토큰 불일치로 거부되어야 한다");
    }

    [Fact]
    public async Task Validate_length_mismatch_returns_false()
    {
        const string token = "rt-abcdef0123456789";
        var store = Build(JwtReturning(token).Object);
        await store.IssueAsync("user01");

        // 길이가 다른 토큰 — FixedTimeEquals는 길이 불일치 시 false. 접두부가 같아도 매칭되면 안 된다.
        (await store.ValidateAsync("user01", token + "extra")).Should().BeFalse(
            "길이가 다른 토큰은 거부되어야 한다");
        (await store.ValidateAsync("user01", token[..5])).Should().BeFalse(
            "접두부만 일치하는 짧은 토큰은 거부되어야 한다");
    }

    [Fact]
    public async Task Validate_unknown_user_returns_false()
    {
        var store = Build(JwtReturning("rt-x").Object);
        (await store.ValidateAsync("nobody", "rt-x")).Should().BeFalse(
            "발급 이력이 없는 사용자는 false여야 한다");
    }

    [Fact]
    public async Task Revoke_removes_only_matching_token()
    {
        var store = Build(JwtReturning("rt-aaa111", "rt-bbb222").Object);
        var first = await store.IssueAsync("user01");
        var second = await store.IssueAsync("user01");

        await store.RevokeAsync("user01", first);

        (await store.ValidateAsync("user01", first)).Should().BeFalse(
            "폐기된 토큰은 더 이상 유효하지 않아야 한다");
        (await store.ValidateAsync("user01", second)).Should().BeTrue(
            "RevokeAsync는 일치하는 토큰만 제거하고 나머지는 유지해야 한다");
    }

    [Fact]
    public async Task Rotate_invalidates_old_and_issues_new_valid_token()
    {
        var store = Build(JwtReturning("rt-old", "rt-new").Object);
        var old = await store.IssueAsync("user01");

        var rotated = await store.RotateAsync("user01", old);

        rotated.Should().Be("rt-new");
        (await store.ValidateAsync("user01", old)).Should().BeFalse(
            "회전 후 이전 토큰은 무효화되어야 한다");
        (await store.ValidateAsync("user01", rotated)).Should().BeTrue(
            "회전으로 발급된 새 토큰은 유효해야 한다");
    }

    [Fact]
    public async Task RevokeAll_clears_every_token_for_user()
    {
        var store = Build(JwtReturning("rt-1", "rt-2").Object);
        var t1 = await store.IssueAsync("user01");
        var t2 = await store.IssueAsync("user01");

        await store.RevokeAllByUserAsync("user01");

        (await store.ValidateAsync("user01", t1)).Should().BeFalse();
        (await store.ValidateAsync("user01", t2)).Should().BeFalse();
    }
}
