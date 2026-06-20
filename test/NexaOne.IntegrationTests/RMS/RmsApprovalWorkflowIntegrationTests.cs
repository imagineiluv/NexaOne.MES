using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application.Auth;

namespace NexaOne.IntegrationTests.RMS;

/// <summary>
/// RMS 레시피 다단계 승인 워크플로우 HTTP 통합 테스트 — V005(RMS_RECIPE) 테이블이 SQLite에서
/// 실제로 동작하는지(테이블 존재 + 방언 SELECT/INSERT/UPDATE + 인증 + 직렬화 + 영속)를 상태전이로 실증한다.
///
/// 핵심 시나리오:
///   1) 미인증 401 — request-approval/approve1/approve2/release/reject 모두 [Authorize]로 토큰 없으면 401.
///   2) 해피패스 다단계 전이 되읽기 — Create → RequestApproval → Approve1 → Approve2 → Release.
///      각 단계마다 GET recipes/{id}로 APPROVAL_STATE 전이(Draft→WaitApproval→Approved1→Approved→Released)와
///      승인자 컬럼(FIRST/SECOND_APPROVER_ID)이 RMS_RECIPE에 UPDATE 영속·복원되는지 확인한다.
///   3) reject — Draft → Rejected 전이 후 되읽기.
///
/// 비-부인성(§19, ADR): approve1/approve2/release 엔드포인트는 본문에 승인자를 받지 않고 컨트롤러가
///   CurrentUserId()(토큰 sub)로 기록한다. 따라서 되읽은 FIRST_APPROVER_ID는 1차 승인 토큰의 sub와,
///   SECOND_APPROVER_ID는 2차 승인 토큰의 sub와 일치해야 한다(본문이 아니라 토큰 신원 기록 검증).
///
/// 2차 승인자 분리: 도메인 Recipe.Approve2는 2차 승인자가 1차 승인자와 같으면 Conflict로 거부한다
///   (4-eyes 원칙). TestApiFactory.CreateAuthenticatedClient()는 항상 sub="test-admin"으로 발급하므로,
///   해피패스를 위해 동일 앱의 IJwtService로 다른 sub("test-approver2") 토큰 클라이언트를 직접 만든다.
///
/// FK 주의: FIRST/SECOND_APPROVER_ID → SYS_USER FK가 있으나 하니스는 FK-OFF(Foreign Keys=False)라
///   승인자 행을 SYS_USER에 선행 시드하지 않아도 UPDATE가 성립한다(운영 MSSQL은 FK 강제).
/// </summary>
public sealed class RmsApprovalWorkflowIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    private const string FirstApprover = "test-admin";       // 기본 CreateAuthenticatedClient()의 토큰 sub
    private const string SecondApprover = "test-approver2";   // 4-eyes: 1차와 달라야 Approve2 통과

    public RmsApprovalWorkflowIntegrationTests(TestApiFactory factory) => _factory = factory;

    /// <summary>앱의 IJwtService로 임의 sub의 토큰을 발급해 인증 클라이언트를 만든다.
    /// CreateAuthenticatedClient()는 sub를 고정("test-admin")하므로, 2차 승인자(다른 신원)가 필요한
    /// 4-eyes 전이를 검증하려면 다른 sub의 토큰이 필요하다. 권한은 "*"(ADMIN)로 perm:rms:manage를 통과시킨다.</summary>
    private HttpClient CreateClientAs(string userId)
    {
        var client = _factory.CreateClient();
        var jwt = _factory.Services.GetRequiredService<IJwtService>();
        var token = jwt.GenerateAccessToken(userId, userId, "DEFAULT", new[] { "ADMIN" }, permissions: new[] { "*" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Approval_transitions_require_auth()
    {
        // 토큰 없는 요청은 [Authorize]로 401이어야 한다(라우팅·인증 파이프라인 검증, 시드 불필요).
        var anon = _factory.CreateClient();

        var requestApproval = await anon.PutAsync("/api/v1/rms/recipes/ANY/request-approval", null);
        requestApproval.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 request-approval 전이는 401이어야 한다");

        var approve1 = await anon.PutAsync("/api/v1/rms/recipes/ANY/approve1", null);
        approve1.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 approve1 전이는 401이어야 한다");

        var release = await anon.PutAsync("/api/v1/rms/recipes/ANY/release", null);
        release.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 release 전이는 401이어야 한다");
    }

    [Fact]
    public async Task Full_approval_workflow_persists_state_and_records_approvers_from_token()
    {
        var firstClient = _factory.CreateAuthenticatedClient();   // sub="test-admin" (1차 승인자)
        var secondClient = CreateClientAs(SecondApprover);        // sub="test-approver2" (2차 승인자)
        const string recipeId = "RMS-WF-FULL";
        const string equipmentClassId = "CLS-RMS-WF";

        // 0) 레시피 생성(RMS_RECIPE INSERT) — 본문 필드는 CreateRecipeRequest 레코드와 1:1.
        var createResp = await firstClient.PostAsJsonAsync("/api/v1/rms/recipes", new
        {
            recipeId,
            name = "Workflow Recipe",
            description = "Multi-stage approval workflow",
            equipmentClassId
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"레시피 생성(RMS_RECIPE INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<RecipeDto>();
        created.Should().NotBeNull();
        created!.ApprovalState.Should().Be("Draft", "신규 레시피 초기 상태는 Draft여야 한다");

        // 1) Draft → WaitApproval (request-approval, UPDATE APPROVAL_STATE).
        var reqApproval = await firstClient.PutAsync($"/api/v1/rms/recipes/{recipeId}/request-approval", null);
        var reqBody = await reqApproval.Content.ReadAsStringAsync();
        reqApproval.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"승인요청 전이(Draft→WaitApproval)는 204여야 한다. 본문: {reqBody}");
        (await ReadRecipe(firstClient, recipeId)).ApprovalState.Should().Be("WaitApproval",
            "request-approval 후 되읽은 상태는 WaitApproval이어야 한다(UPDATE 영속·Restore 복원)");

        // 2) WaitApproval → Approved1 (approve1). 승인자는 본문이 아니라 토큰 sub("test-admin")로 기록.
        var approve1 = await firstClient.PutAsync($"/api/v1/rms/recipes/{recipeId}/approve1", null);
        var approve1Body = await approve1.Content.ReadAsStringAsync();
        approve1.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"1차 승인 전이(WaitApproval→Approved1)는 204여야 한다. 본문: {approve1Body}");

        var afterApprove1 = await ReadRecipe(firstClient, recipeId);
        afterApprove1.ApprovalState.Should().Be("Approved1",
            "approve1 후 되읽은 상태는 Approved1이어야 한다");
        afterApprove1.FirstApproverId.Should().Be(FirstApprover,
            "비-부인성: FIRST_APPROVER_ID는 본문이 아니라 1차 승인 토큰 sub(test-admin)로 기록·영속돼야 한다");

        // 3) Approved1 → Approved (approve2). 2차 승인자는 1차와 다른 토큰 sub("test-approver2").
        var approve2 = await secondClient.PutAsync($"/api/v1/rms/recipes/{recipeId}/approve2", null);
        var approve2Body = await approve2.Content.ReadAsStringAsync();
        approve2.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"2차 승인 전이(Approved1→Approved)는 204여야 한다(4-eyes: 다른 승인자). 본문: {approve2Body}");

        var afterApprove2 = await ReadRecipe(firstClient, recipeId);
        afterApprove2.ApprovalState.Should().Be("Approved",
            "approve2 후 되읽은 상태는 Approved여야 한다");
        afterApprove2.SecondApproverId.Should().Be(SecondApprover,
            "비-부인성: SECOND_APPROVER_ID는 2차 승인 토큰 sub(test-approver2)로 기록·영속돼야 한다");
        afterApprove2.FirstApproverId.Should().Be(FirstApprover,
            "2차 승인 후에도 1차 승인자(FIRST_APPROVER_ID)는 유지돼야 한다(UPDATE가 기존 승인자 컬럼을 보존)");

        // 4) Approved → Released (release, UPDATE APPROVAL_STATE + RELEASED_AT).
        var release = await firstClient.PutAsync($"/api/v1/rms/recipes/{recipeId}/release", null);
        var releaseBody = await release.Content.ReadAsStringAsync();
        release.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"배포 전이(Approved→Released)는 204여야 한다. 본문: {releaseBody}");

        var afterRelease = await ReadRecipe(firstClient, recipeId);
        afterRelease.ApprovalState.Should().Be("Released",
            "release 후 되읽은 상태는 Released여야 한다(다단계 전이 누적 영속)");
        afterRelease.FirstApproverId.Should().Be(FirstApprover,
            "배포 후에도 1차 승인자 기록이 보존돼야 한다");
        afterRelease.SecondApproverId.Should().Be(SecondApprover,
            "배포 후에도 2차 승인자 기록이 보존돼야 한다");
        afterRelease.ReleasedAt.Should().NotBeNull(
            "배포 시 RELEASED_AT(DATETIME2→TEXT)가 기록·영속·역직렬화돼야 한다");

        // 5) state 필터 조회(GetByStateAsync → WHERE APPROVAL_STATE=@state, enum.ToString()) — 방언 필터 실증.
        var releasedList = await firstClient.GetFromJsonAsync<List<RecipeDto>>("/api/v1/rms/recipes?state=Released");
        releasedList.Should().NotBeNull();
        releasedList!.Should().ContainSingle(r => r.Id == recipeId,
            "Released 상태 필터 조회에 배포된 레시피가 정확히 1건 포함돼야 한다");
    }

    [Fact]
    public async Task Reject_transitions_recipe_to_rejected_and_persists()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string recipeId = "RMS-WF-REJECT";
        const string equipmentClassId = "CLS-RMS-REJECT";

        var createResp = await client.PostAsJsonAsync("/api/v1/rms/recipes", new
        {
            recipeId, name = "Reject Recipe", description = "", equipmentClassId
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"레시피 생성이 성공해야 한다. 본문: {await createResp.Content.ReadAsStringAsync()}");

        // Draft → Rejected (reject, 본문 RejectRequest(Reason) 1:1).
        var reject = await client.PutAsJsonAsync($"/api/v1/rms/recipes/{recipeId}/reject", new { reason = "Out of spec" });
        var rejectBody = await reject.Content.ReadAsStringAsync();
        reject.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"반려 전이(Draft→Rejected)는 204여야 한다. 본문: {rejectBody}");

        var afterReject = await ReadRecipe(client, recipeId);
        afterReject.ApprovalState.Should().Be("Rejected",
            "reject 후 되읽은 상태는 Rejected여야 한다(UPDATE 영속·Restore 복원)");
    }

    private async Task<RecipeDto> ReadRecipe(HttpClient client, string recipeId)
    {
        var resp = await client.GetAsync($"/api/v1/rms/recipes/{recipeId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"단건 조회(GET recipes/{recipeId})가 성공해야 한다. 응답 본문: {body}");
        var dto = await resp.Content.ReadFromJsonAsync<RecipeDto>();
        dto.Should().NotBeNull($"레시피 {recipeId} 응답이 역직렬화돼야 한다. 본문: {body}");
        return dto!;
    }

    // 응답 본문은 RMS 도메인 Recipe(camelCase, JsonStringEnumConverter로 enum은 문자열).
    // Id는 AuditableEntity/AggregateRoot의 식별자(RECIPE_ID). 승인자/배포시각은 전이로 채워진다.
    private sealed record RecipeDto(
        string Id,
        string RecipeName,
        string EquipmentClassId,
        int Version,
        string ApprovalState,
        string? FirstApproverId,
        string? SecondApproverId,
        DateTime? ReleasedAt);
}
