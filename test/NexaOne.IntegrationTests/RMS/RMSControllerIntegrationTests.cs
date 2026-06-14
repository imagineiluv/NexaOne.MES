using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.RMS;

/// <summary>
/// RMS 레시피 관리 HTTP 통합 테스트 — V005 마이그레이션으로 생성되는 RMS_RECIPE 테이블이
/// SQLite에서 실제로 동작하는지(방언 SELECT/INSERT + 인증 + 라우팅 + 직렬화)를 end-to-end로 검증한다.
///
/// 읽기 스모크: GET /api/v1/rms/recipes 는 미인증 401, 인증 200(빈 목록 허용)을 보장한다.
///   - 컨트롤러는 state·equipmentClassId 둘 다 미지정 시 GetByEquipmentClassAsync("")로 위임하므로,
///     쿼리 파라미터 없이도 'EQUIPMENT_CLASS_ID = ''' 동등 조회가 되어 시드 없이 항상 200/빈배열이 된다.
///
/// 쓰기 해피패스: POST /api/v1/rms/recipes 로 레시피(주 엔티티) 1건을 생성한다.
///   - RMS_RECIPE의 FK는 FIRST_APPROVER_ID·SECOND_APPROVER_ID → SYS_USER 뿐이며, 생성 시 둘 다 NULL이라
///     선행 부모 시드가 필요 없다. EQUIPMENT_CLASS_ID는 NOT NULL이지만 FK 제약이 없어 임의 값 사용 가능.
///   - 생성 후 같은 equipmentClassId로 목록을 되읽어(동등 필터, enum 미개입) 영속화를 확인한다.
/// </summary>
public sealed class RMSControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public RMSControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetRecipes_requires_auth_and_returns_ok_when_authenticated()
    {
        // 미인증 클라이언트 → 401 (라우팅·인증 파이프라인 검증, 시드 불필요)
        var anonymous = _factory.CreateClient();
        var anonResp = await anonymous.GetAsync("/api/v1/rms/recipes");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 요청은 [Authorize]로 401이어야 한다");

        // 인증 클라이언트 → 200 + 빈 목록 (RMS_RECIPE 테이블 존재 + SQLite SELECT + 직렬화 검증)
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN)
        var resp = await client.GetAsync("/api/v1/rms/recipes");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"RMS_RECIPE 조회(SELECT)가 성공해야 한다. 응답 본문: {body}");

        var recipes = await resp.Content.ReadFromJsonAsync<List<RecipeDto>>();
        recipes.Should().NotBeNull("목록 엔드포인트는 JSON 배열을 반환해야 한다");
    }

    [Fact]
    public async Task CreateRecipe_persists_and_is_retrievable_by_equipment_class()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:rms:manage 정책 통과
        const string recipeId = "RMS-IT-1";
        const string equipmentClassId = "CLS-RMS";

        // 1) 레시피 생성 — FK 부모 불필요(approver는 NULL). 본문 필드는 CreateRecipeRequest 레코드와 1:1.
        var createResp = await client.PostAsJsonAsync("/api/v1/rms/recipes", new
        {
            recipeId,
            name = "RMS Integration Test Recipe",
            description = "Created by integration test",
            equipmentClassId
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"레시피 생성(RMS_RECIPE INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<RecipeDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(recipeId, "생성된 레시피의 식별자는 요청한 RECIPE_ID여야 한다");
        created.ApprovalState.Should().Be("Draft", "신규 레시피의 초기 승인 상태는 Draft여야 한다");

        // 2) 동일 equipmentClassId로 목록 조회(EQUIPMENT_CLASS_ID 동등 필터) — 영속화 확인.
        var list = await client.GetFromJsonAsync<List<RecipeDto>>(
            $"/api/v1/rms/recipes?equipmentClassId={equipmentClassId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(r => r.Id == recipeId,
            "생성한 레시피가 동일 설비클래스 목록에 정확히 1건 조회되어야 한다");

        // 3) 단건 조회(GET recipes/{id}) — 라우트 파라미터 바인딩 + NotFound 분기 검증.
        var single = await client.GetFromJsonAsync<RecipeDto>($"/api/v1/rms/recipes/{recipeId}");
        single.Should().NotBeNull();
        single!.Id.Should().Be(recipeId);
        single.EquipmentClassId.Should().Be(equipmentClassId);
    }

    [Fact]
    public async Task RequestApproval_then_read_back_preserves_approval_state()
    {
        // 읽기경로 회귀 — 상태 전이 후 되읽었을 때 승인 상태가 유지되는지 검증한다.
        // (RecipeRepository.ToDomain이 Create로 재구성하면 ApprovalState가 Draft로 유실되던 버그 방지.)
        var client = _factory.CreateAuthenticatedClient();
        const string recipeId = "RMS-IT-STATE";
        const string equipmentClassId = "CLS-RMS-STATE";

        var createResp = await client.PostAsJsonAsync("/api/v1/rms/recipes", new
        {
            recipeId, name = "State Recipe", description = "", equipmentClassId
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Draft → WaitApproval 전이.
        var transition = await client.PutAsync($"/api/v1/rms/recipes/{recipeId}/request-approval", null);
        transition.StatusCode.Should().Be(HttpStatusCode.NoContent, "승인요청 전이가 성공해야 한다");

        // 되읽기 — 전이된 상태(WaitApproval)가 그대로 영속/복원되어야 한다(Draft로 유실되면 안 됨).
        var reread = await client.GetFromJsonAsync<RecipeDto>($"/api/v1/rms/recipes/{recipeId}");
        reread.Should().NotBeNull();
        reread!.ApprovalState.Should().Be("WaitApproval",
            "되읽은 레시피의 승인 상태는 전이 결과(WaitApproval)여야 한다 — ToDomain이 상태를 유실하면 안 된다");
    }

    // 응답 본문은 RMS 도메인 Recipe(camelCase, JsonStringEnumConverter로 enum은 문자열).
    private sealed record RecipeDto(string Id, string RecipeName, string EquipmentClassId, string ApprovalState);
}
