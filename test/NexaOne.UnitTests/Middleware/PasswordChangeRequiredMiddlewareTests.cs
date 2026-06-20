using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexaOne.API.Middleware;
using NexaOne.Application.Auth;

namespace NexaOne.UnitTests.Middleware;

/// <summary>§20.10 — 비밀번호 변경 강제 사용자의 업무 API 차단.
/// 판정은 pwdChange 클레임으로만 하고, 인증/비밀번호 변경 경로는 허용한다.</summary>
public sealed class PasswordChangeRequiredMiddlewareTests
{
    private static DefaultHttpContext Context(string path, bool pwdChange)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (pwdChange)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(JwtService.PasswordChangeClaim, "true") }, "test"));
        return ctx;
    }

    [Theory]
    [InlineData("/api/v1/sys/menus")]
    [InlineData("/api/v1/mdm/equipments")]
    [InlineData("/hubs/smartees")]
    public async Task Business_paths_are_blocked_with_403(string path)
    {
        var nextCalled = false;
        var mw = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = Context(path, pwdChange: true);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeFalse("차단된 요청은 다음 미들웨어로 가면 안 된다");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.Should().Contain("PASSWORD_CHANGE_REQUIRED");
    }

    [Theory]
    [InlineData("/api/v1/auth/change-password")]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/health")]
    public async Task Auth_and_health_paths_are_allowed(string path)
    {
        var nextCalled = false;
        var mw = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = Context(path, pwdChange: true);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue("비밀번호 변경/인증 경로까지 막으면 빠져나갈 방법이 없다");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task User_without_claim_passes_through()
    {
        var nextCalled = false;
        var mw = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = Context("/api/v1/sys/menus", pwdChange: false);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
}
