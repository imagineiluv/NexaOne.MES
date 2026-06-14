using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NexaOne.Web.Pages.RMS;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// RMS 레시피 승인 대기 페이지(/rms/approval)의 승인 워크플로우 전이·검증 회귀 안전망(bUnit).
/// 핵심 분기: ApprovalState(WaitApproval/Approved1/Approved)에 따라 1차/2차/릴리스 게이트웨이를 호출하고,
/// 담당자 ID 검증(둘 다 미입력 → 오류·게이트웨이 미호출), 거부 사유 공백 → 거부 게이트웨이 미호출을 보장한다.
///
/// 의존성 전략:
///  - IApiClient        : 인터페이스 → Moq.
///  - WaitOverlayService: sealed 구상이나 무인자 생성 가능 → 실인스턴스.
///  - AuthContextService: sealed·비가상 구상(인터페이스/가상 시임 없음) → 실인스턴스로 구성.
///    실제 ProtectedSessionStorage(IJSRuntime + IDataProtectionProvider)를 백킹으로 두며,
///    bUnit Loose JSInterop에서는 토큰 조회가 null이 되어 _currentUserId 가 빈 문자열로 초기화된다.
///    (따라서 _currentUserId 비폴백 분기만 검증 가능 — 폴백 성공 분기는 시임 부재로 검증 불가, 보고만.)
/// </summary>
public sealed class RecipeApprovalTests
{
    private static RecipeDto Recipe(string id, string state, string? firstApprover = null, string? secondApprover = null)
        => new(
            Id: id,
            RecipeName: $"레시피-{id}",
            Description: null,
            EquipmentClassId: "EC-1",
            Version: 1,
            ApprovalState: state,
            FirstApproverId: firstApprover,
            SecondApproverId: secondApprover,
            ReleasedAt: null);

    /// <summary>
    /// 컴포넌트가 주입받는 구상 의존성(WaitOverlayService, AuthContextService)을 실인스턴스로 등록한다.
    /// AuthContextService는 sealed·비가상이라 Moq 불가 → 실제 ProtectedSessionStorage 백킹 실인스턴스를 구성한다.
    /// </summary>
    private static void RegisterRealDependencies(TestContext ctx)
    {
        ctx.Services.AddSingleton<WaitOverlayService>();
        ctx.Services.AddDataProtection();
        ctx.Services.AddScoped(sp => new ProtectedSessionStorage(
            sp.GetRequiredService<IJSRuntime>(),
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        ctx.Services.AddScoped(sp => new AuthTokenService(sp.GetRequiredService<ProtectedSessionStorage>()));
        ctx.Services.AddScoped(sp => new AuthContextService(sp.GetRequiredService<AuthTokenService>()));
    }

    /// <summary>지정 상태(state)의 레시피만 반환하도록 GetRecipesAsync를 셋업한 IApiClient Mock.</summary>
    private static Mock<IApiClient> ApiReturning(params RecipeDto[] recipes)
    {
        var api = new Mock<IApiClient>();
        api.Setup(a => a.GetRecipesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync((string? _, string? state, CancellationToken _) =>
               recipes.Where(r => r.ApprovalState == state).ToList());
        return api;
    }

    private static IElement ButtonWithText(IRenderedComponent<RecipeApproval> cut, string text)
        => cut.FindAll("button").First(b => b.TextContent.Trim() == text);

    // ── 시나리오 (a): 상태별 승인 게이트웨이 분기 ─────────────────────────────────

    [Fact]
    public void WaitApproval_confirm_calls_ApproveRecipe1Async_with_entered_userId()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var recipe = Recipe("R-1", "WaitApproval");
        var api = ApiReturning(recipe);
        ctx.Services.AddSingleton(api.Object);
        RegisterRealDependencies(ctx);

        var cut = ctx.RenderComponent<RecipeApproval>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("레시피-R-1"), TimeSpan.FromSeconds(2));

        ButtonWithText(cut, "워크플로우").Click();           // 워크플로우 모달 열기
        cut.Find("input.nexa-input").Change("OPERATOR-A");    // 담당자 ID 입력(_actionUserId)
        ButtonWithText(cut, "1차 승인").Click();              // ConfirmWorkflowAction

        cut.WaitForAssertion(() =>
            api.Verify(a => a.ApproveRecipe1Async("R-1", "OPERATOR-A", It.IsAny<CancellationToken>()), Times.Once,
                "WaitApproval 상태에서 승인 확인 시 입력한 담당자 ID로 1차 승인 게이트웨이를 호출해야 한다"),
            TimeSpan.FromSeconds(2));

        api.Verify(a => a.ApproveRecipe2Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "1차 승인 단계에서 2차 승인 게이트웨이를 호출하면 안 된다");
        api.Verify(a => a.ReleaseRecipeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "1차 승인 단계에서 릴리스 게이트웨이를 호출하면 안 된다");
    }

    [Fact]
    public void Approved1_confirm_calls_ApproveRecipe2Async()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var recipe = Recipe("R-2", "Approved1", firstApprover: "FIRST");
        var api = ApiReturning(recipe);
        ctx.Services.AddSingleton(api.Object);
        RegisterRealDependencies(ctx);

        var cut = ctx.RenderComponent<RecipeApproval>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("레시피-R-2"), TimeSpan.FromSeconds(2));

        ButtonWithText(cut, "워크플로우").Click();
        cut.Find("input.nexa-input").Change("OPERATOR-B");
        ButtonWithText(cut, "2차 승인").Click();

        cut.WaitForAssertion(() =>
            api.Verify(a => a.ApproveRecipe2Async("R-2", "OPERATOR-B", It.IsAny<CancellationToken>()), Times.Once,
                "Approved1 상태에서 승인 확인 시 2차 승인 게이트웨이를 호출해야 한다(Approved1→ApproveRecipe2 분기)"),
            TimeSpan.FromSeconds(2));

        api.Verify(a => a.ApproveRecipe1Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "2차 승인 단계에서 1차 승인 게이트웨이를 호출하면 안 된다");
        api.Verify(a => a.ReleaseRecipeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "2차 승인 단계에서 릴리스 게이트웨이를 호출하면 안 된다");
    }

    [Fact]
    public void Approved_state_label_is_release_and_hides_actionUserId_input()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var recipe = Recipe("R-3", "Approved", firstApprover: "FIRST", secondApprover: "SECOND");
        var api = ApiReturning(recipe);
        ctx.Services.AddSingleton(api.Object);
        RegisterRealDependencies(ctx);

        var cut = ctx.RenderComponent<RecipeApproval>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("레시피-R-3"), TimeSpan.FromSeconds(2));

        ButtonWithText(cut, "워크플로우").Click();

        // Approved 상태는 액션 라벨이 "릴리스"이고(상태→라벨 매핑), 담당자 ID 입력란을 렌더하지 않는다
        // (razor: 입력란은 WaitApproval/Approved1 에서만 노출). 따라서 유일한 담당자 ID 출처는 _currentUserId 뿐이다.
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "릴리스",
            "Approved 상태에서는 액션 버튼 라벨이 '릴리스'여야 한다(상태→라벨 분기)");
        cut.FindAll("input.nexa-input").Should().BeEmpty(
            "Approved 상태에서는 담당자 ID 입력란을 렌더하지 않아야 한다(WaitApproval/Approved1 에서만 노출)");

        // 담당자 입력란이 없고 _currentUserId 도 빈 값(Loose JSInterop)이므로 릴리스 확인 시 게이트웨이를 호출하지 않는다.
        // (릴리스 양성경로 — _currentUserId 폴백으로 ReleaseRecipeAsync 호출 — 은 sealed AuthContextService 시임 부재로 검증 불가, 보고만.)
        ButtonWithText(cut, "릴리스").Click();
        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("담당자 ID를 확인할 수 없습니다.",
                "Approved 상태에서 담당자 ID(_currentUserId 폴백 포함)가 없으면 오류를 표시해야 한다"),
            TimeSpan.FromSeconds(2));
        api.Verify(a => a.ReleaseRecipeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "담당자 ID를 확인할 수 없으면 릴리스 게이트웨이를 호출하면 안 된다");
        api.Verify(a => a.ApproveRecipe1Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(a => a.ApproveRecipe2Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 시나리오 (b): 담당자 ID 검증 ──────────────────────────────────────────────

    [Fact]
    public void Confirm_with_no_userId_and_empty_currentUser_shows_error_and_skips_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var recipe = Recipe("R-4", "WaitApproval");
        var api = ApiReturning(recipe);
        ctx.Services.AddSingleton(api.Object);
        RegisterRealDependencies(ctx); // Loose JSInterop → _currentUserId == "" (토큰 조회 null)

        var cut = ctx.RenderComponent<RecipeApproval>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("레시피-R-4"), TimeSpan.FromSeconds(2));

        ButtonWithText(cut, "워크플로우").Click();
        // 담당자 ID 미입력 + _currentUserId 빈 값 → 둘 다 없음.
        ButtonWithText(cut, "1차 승인").Click();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("담당자 ID를 확인할 수 없습니다.",
                "담당자 ID(_actionUserId)와 폴백(_currentUserId)이 모두 없으면 오류 문구를 표시해야 한다"),
            TimeSpan.FromSeconds(2));

        api.Verify(a => a.ApproveRecipe1Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "담당자 ID를 확인할 수 없으면 승인 게이트웨이를 호출하면 안 된다");
        api.Verify(a => a.ApproveRecipe2Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(a => a.ReleaseRecipeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 시나리오 (c): 거부 사유 공백 ──────────────────────────────────────────────

    [Fact]
    public void Reject_with_blank_reason_does_not_call_RejectRecipeAsync()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var recipe = Recipe("R-5", "WaitApproval");
        var api = ApiReturning(recipe);
        ctx.Services.AddSingleton(api.Object);
        RegisterRealDependencies(ctx);

        var cut = ctx.RenderComponent<RecipeApproval>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("레시피-R-5"), TimeSpan.FromSeconds(2));

        ButtonWithText(cut, "워크플로우").Click();
        ButtonWithText(cut, "거부").Click();                  // 거부 인라인 모달 열기

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("거부 확인"), TimeSpan.FromSeconds(2));

        // 거부 사유(_rejectReason)를 공백 그대로 두고 거부 확인 → RejectRecipeAsync 미호출.
        ButtonWithText(cut, "거부 확인").Click();

        api.Verify(a => a.RejectRecipeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "거부 사유가 공백이면 거부 게이트웨이를 호출하면 안 된다");
    }

    [Fact]
    public void Reject_with_reason_calls_RejectRecipeAsync_with_reason()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var recipe = Recipe("R-6", "WaitApproval");
        var api = ApiReturning(recipe);
        ctx.Services.AddSingleton(api.Object);
        RegisterRealDependencies(ctx);

        var cut = ctx.RenderComponent<RecipeApproval>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("레시피-R-6"), TimeSpan.FromSeconds(2));

        ButtonWithText(cut, "워크플로우").Click();
        ButtonWithText(cut, "거부").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("거부 확인"), TimeSpan.FromSeconds(2));

        cut.Find("textarea.nexa-input").Change("사양 미달");   // 거부 사유 입력(_rejectReason)
        ButtonWithText(cut, "거부 확인").Click();

        cut.WaitForAssertion(() =>
            api.Verify(a => a.RejectRecipeAsync("R-6", "사양 미달", It.IsAny<CancellationToken>()), Times.Once,
                "거부 사유가 있으면 해당 사유로 거부 게이트웨이를 호출해야 한다(대조군: 공백이면 미호출)"),
            TimeSpan.FromSeconds(2));
    }
}
