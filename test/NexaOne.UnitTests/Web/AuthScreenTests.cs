using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Pages.Auth;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// 로그인/비밀번호변경 화면(Pages/Auth)의 검증·분기 회귀 안전망 (§19.2.2 / §20.10).
///
/// 핵심 보호 대상:
///  - 로그인 실패 메시지: 계정 잠금(ACCOUNT_LOCKED)만 서버 안내를 노출하고, 그 외 코드는
///    아이디 열거(enumeration) 방지를 위해 동일한 일반 문구로 고정한다.
///  - 임시 비밀번호 변경: 새/확인 불일치 또는 정책 미충족 시 서버를 호출하지 않고(불필요한 왕복·
///    감사로그 오염 방지) 안내 문구만 띄운다. 통과 시에만 ChangePasswordAsync를 호출한다.
///
/// 하니스 메모: 두 화면 모두 sealed 구상 서비스(AuthTokenService→ProtectedSessionStorage,
/// JwtAuthStateProvider)를 주입받는다. 인터페이스가 없어 Moq 불가하므로 DataProtection +
/// ProtectedSessionStorage 실인스턴스를 DI에 등록해 실제로 렌더한다. JS 호출은 Loose 모드라
/// 토큰 저장(SetAsync)이 예외 없이 no-op로 동작하므로 성공 경로의 분기(목적지 네비게이션)까지
/// 검증할 수 있다. IApiClient만 Moq, NavigationManager는 bUnit FakeNavigationManager.
/// </summary>
public sealed class AuthScreenTests
{
    private static TestContext NewCtx(Mock<IApiClient> api)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;     // NexaUI 입력/세션스토리지 JS 호출 허용(렌더·분기만 검증)
        ctx.Services.AddSingleton(api.Object);
        // 구상 sealed 인증 서비스 체인을 DI로 구성(인터페이스 부재로 Moq 불가) — 실인스턴스로 렌더.
        ctx.Services.AddDataProtection();
        ctx.Services.AddScoped<ProtectedSessionStorage>();
        ctx.Services.AddScoped<AuthTokenService>();
        ctx.Services.AddScoped<JwtAuthStateProvider>();
        return ctx;
    }

    private static Mock<IApiClient> LoginReturning(LoginResult result)
    {
        var api = new Mock<IApiClient>();
        api.Setup(a => a.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(result);
        return api;
    }

    // 폼 입력을 채우고 제출한다(EditForm.OnValidSubmit 트리거). 모델에 검증 특성이 없어 항상 valid.
    private static void FillAndSubmit(IRenderedComponent<IComponent> cut, params string[] values)
    {
        var inputs = cut.FindAll("input");
        for (var i = 0; i < values.Length; i++)
            inputs[i].Change(values[i]);
        cut.Find("form").Submit();
    }

    // ── Login (a) 실패 메시지 분기 ────────────────────────────────────────────────

    [Fact]
    public void Login_account_locked_shows_server_message_verbatim()
    {
        // ACCOUNT_LOCKED는 서버가 보낸 안내(남은 시간 포함)를 그대로 노출해야 한다.
        var api = LoginReturning(new LoginResult(null, "ACCOUNT_LOCKED",
            "비밀번호 5회 오류로 잠겼습니다. 3분 후 다시 시도하세요."));
        using var ctx = NewCtx(api);
        var cut = ctx.RenderComponent<Login>();

        FillAndSubmit(cut, "user1", "wrongpw");

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("3분 후 다시 시도하세요.",
                "계정 잠금은 서버 안내(남은 시간 포함)를 그대로 표시해야 한다(§20.10)"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Login_account_locked_with_null_message_falls_back_to_default_lock_text()
    {
        // ACCOUNT_LOCKED인데 서버 메시지가 비면 잠금 전용 기본 안내로 폴백한다(일반 실패문구가 아니라).
        var api = LoginReturning(new LoginResult(null, "ACCOUNT_LOCKED", null));
        using var ctx = NewCtx(api);
        var cut = ctx.RenderComponent<Login>();

        FillAndSubmit(cut, "user1", "wrongpw");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("계정이 잠겼습니다",
                "메시지가 비어도 잠금 코드면 잠금 전용 기본 안내로 폴백해야 한다");
            cut.Markup.Should().NotContain("로그인에 실패했습니다.",
                "잠금 분기는 일반 실패 문구를 쓰지 않는다");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Login_other_error_code_shows_generic_message_and_hides_server_detail()
    {
        // 잠금 외 코드는 아이디 열거 방지를 위해 서버 상세를 숨기고 동일한 일반 문구로 고정한다.
        var api = LoginReturning(new LoginResult(null, "INVALID_CREDENTIALS",
            "해당 사용자가 존재하지 않습니다"));   // 노출되면 안 되는 서버 상세
        using var ctx = NewCtx(api);
        var cut = ctx.RenderComponent<Login>();

        FillAndSubmit(cut, "ghost", "pw");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("로그인에 실패했습니다.",
                "잠금 외 실패는 동일한 일반 문구로 고정돼야 한다");
            cut.Markup.Should().NotContain("존재하지 않습니다",
                "열거 방지: 서버의 구체적 실패 사유를 화면에 노출하면 안 된다");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Login_null_error_code_shows_generic_message()
    {
        // 코드가 아예 없는 실패도 잠금이 아니므로 일반 문구로 처리된다.
        var api = LoginReturning(new LoginResult(null));
        using var ctx = NewCtx(api);
        var cut = ctx.RenderComponent<Login>();

        FillAndSubmit(cut, "user1", "pw");

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("로그인에 실패했습니다.",
                "ErrorCode가 없는 실패도 일반 실패 문구를 표시해야 한다"),
            TimeSpan.FromSeconds(2));
    }

    // ── Login (b) 성공 후 목적지 분기 ─────────────────────────────────────────────

    [Fact]
    public void Login_success_with_require_password_change_navigates_to_change_password()
    {
        var api = LoginReturning(new LoginResult(new LoginResponse(
            "access", "refresh", "user1", "User One", "PLANT1",
            new[] { "ADMIN" }, RequirePasswordChange: true)));
        using var ctx = NewCtx(api);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = ctx.RenderComponent<Login>();

        FillAndSubmit(cut, "user1", "Temp1234!");

        cut.WaitForAssertion(
            () => nav.Uri.Should().EndWith("/auth/change-password",
                "임시 비밀번호 로그인은 비밀번호 변경 화면으로 강제 이동해야 한다"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Login_success_without_require_change_navigates_to_default_home()
    {
        var api = LoginReturning(new LoginResult(new LoginResponse(
            "access", "refresh", "user1", "User One", "PLANT1",
            new[] { "ADMIN" }, RequirePasswordChange: false)));
        using var ctx = NewCtx(api);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = ctx.RenderComponent<Login>();

        FillAndSubmit(cut, "user1", "GoodPass1!");

        cut.WaitForAssertion(
            () => nav.Uri.Should().EndWith("/mdm/equipment",
                "비밀번호 변경이 불필요한 정상 로그인은 기본 홈으로 이동해야 한다"),
            TimeSpan.FromSeconds(2));
    }

    // ── ChangePassword (c)(d)(e) ─────────────────────────────────────────────────

    [Fact]
    public void ChangePassword_mismatch_blocks_call_and_shows_mismatch_message()
    {
        var api = new Mock<IApiClient>();
        using var ctx = NewCtx(api);
        var cut = ctx.RenderComponent<ChangePassword>();

        // 현재 / 새 / 확인 — 새≠확인
        FillAndSubmit(cut, "Temp1234!", "NewPass1!", "NewPass2!");

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("일치하지 않습니다",
                "새 비밀번호와 확인이 다르면 불일치 안내를 표시해야 한다"),
            TimeSpan.FromSeconds(2));
        api.Verify(a => a.ChangePasswordAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "불일치 시 서버 ChangePasswordAsync를 호출하면 안 된다");
    }

    [Fact]
    public void ChangePassword_policy_violation_blocks_call_and_shows_policy_message()
    {
        var api = new Mock<IApiClient>();
        using var ctx = NewCtx(api);
        var cut = ctx.RenderComponent<ChangePassword>();

        // 새=확인이지만 정책 미충족('abc' — 길이/대문자/숫자/특수문자 부족)
        FillAndSubmit(cut, "Temp1234!", "abc", "abc");

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("8자 이상",
                "정책 미충족(예: 'abc') 시 정책 안내 문구를 표시해야 한다"),
            TimeSpan.FromSeconds(2));
        api.Verify(a => a.ChangePasswordAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "정책 미충족 시 서버 ChangePasswordAsync를 호출하면 안 된다");
    }

    [Fact]
    public void ChangePassword_valid_calls_api_and_navigates_home()
    {
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ChangePasswordAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);
        using var ctx = NewCtx(api);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = ctx.RenderComponent<ChangePassword>();

        // 새=확인 + 정책 통과(소문자·대문자·숫자·특수문자·8자↑)
        FillAndSubmit(cut, "Temp1234!", "NewPass1!", "NewPass1!");

        cut.WaitForAssertion(() =>
        {
            api.Verify(a => a.ChangePasswordAsync(
                    "Temp1234!", "NewPass1!", "NewPass1!", It.IsAny<CancellationToken>()),
                Times.Once, "검증 통과 시 입력값 그대로 서버 ChangePasswordAsync를 호출해야 한다");
            nav.Uri.Should().EndWith("/mdm/equipment",
                "변경 성공 후 기본 홈으로 이동해야 한다");
        }, TimeSpan.FromSeconds(2));
    }
}
