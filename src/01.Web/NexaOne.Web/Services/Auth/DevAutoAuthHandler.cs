using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NexaOne.Web.Services.Auth;

/// <summary>
/// 개발 전용 자동 인증 스킴 — 정적 SSR 단계에서 라우트 엔드포인트의 [Authorize] 인가를 통과시킨다.
/// 이 앱의 실제 화면 인증은 브라우저의 JWT(<see cref="JwtAuthStateProvider"/>, 클라이언트 측)가 담당하므로,
/// SSR에는 자격이 없어 인증 서비스 부재로 500이 난다. 이 스킴은 로컬/디자인 실행을 위한 것이며 실제 보안
/// 경계가 아니다 — Program.cs에서 Development 환경에만 등록한다(운영은 별도 인증 구성 필요, 미구현).
/// </summary>
public sealed class DevAutoAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "dev"), new Claim(ClaimTypes.NameIdentifier, "dev")],
            Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
