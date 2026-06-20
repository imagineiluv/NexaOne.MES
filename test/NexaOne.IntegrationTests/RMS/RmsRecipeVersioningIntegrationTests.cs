using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application.Auth;

namespace NexaOne.IntegrationTests.RMS;

/// <summary>
/// RMS 레시피 새 버전 파생(CreateNewVersion) HTTP 통합 테스트 — 통합 커버리지가 없던 보안 엔드포인트
/// POST /api/v1/rms/recipes/{recipeId}/new-version([Authorize]+perm:rms:manage)를 SQLite에서 왕복 검증한다.
///
/// 대상 엔드포인트(RmsController.CreateNewVersion):
///  - POST /api/v1/rms/recipes/{recipeId}/new-version  (NewVersionRequest(NewRecipeId)) → Ok(새 Recipe)
///    서비스 CreateNewVersionAsync는 원본이 Released일 때만 source.CreateNewVersion(newId)로
///    Version+1·Draft 신규 레시피를 RMS_RECIPE에 AddAsync(INSERT)하고 Result&lt;Recipe&gt;를 반환한다.
///    컨트롤러는 onSuccess 미지정이라 ToActionResult가 200 OK + 새 레시피 본문을 돌려준다(204 아님).
///
/// 핵심 시나리오(해피패스):
///  1) 레시피 생성(POST recipes) → 승인 플로우(request-approval→approve1→approve2→release)로 Released 전이
///     (4-eyes: approve2는 1차와 다른 토큰 sub가 필요 — RmsApprovalWorkflowIntegrationTests와 동일 방식).
///  2) POST {id}/new-version {newRecipeId} → 200 OK. 본문이 Draft·Version=원본+1로 파생되는지 확인.
///  3) GET 새 레시피 되읽기 — Draft·Version=원본+1·동일 EquipmentClassId가 RMS_RECIPE에 영속·복원(Restore)되는지,
///     원본 GET은 여전히 Released·Version 불변(CreateNewVersion이 원본을 변형하지 않음)인지 검증한다.
///
/// 가드/거부 경로:
///  - 미인증 401 — new-version은 [Authorize]라 토큰 없으면 라우팅·인증 단계에서 401(대상 존재 무관).
///  - 비-Released 거부 409 — 생성 직후 Draft 레시피에 new-version 호출 시 서비스가 Error.Conflict
///    ("Only Released recipes can have a new version.")를 반환 → ConflictObjectResult(409).
///  - 미존재 원본 404 — 없는 recipeId로 호출 시 Error.NotFound → NotFoundObjectResult(404).
///
/// 하니스: TestApiFactory(클래스별 고유 SQLite 임시 DB, FK OFF, db/migrations 부트스트랩).
///   FIRST/SECOND_APPROVER_ID → SYS_USER FK가 있으나 하니스 FK-OFF라 승인자 행 선행 시드 없이 UPDATE 성립.
///   EQUIPMENT_CLASS_ID는 NOT NULL이나 FK 제약이 없어 임의 값 사용 가능. 고유 ID로 클래스 간 격리.
/// </summary>
public sealed class RmsRecipeVersioningIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    private const string FirstApprover = "test-admin";        // 기본 CreateAuthenticatedClient()의 토큰 sub(1차 승인자)
    private const string SecondApprover = "test-approver2";    // 4-eyes: 1차와 달라야 Approve2 통과

    public RmsRecipeVersioningIntegrationTests(TestApiFactory factory) => _factory = factory;

    /// <summary>앱의 IJwtService로 임의 sub의 토큰을 발급해 인증 클라이언트를 만든다.
    /// CreateAuthenticatedClient()는 sub를 "test-admin"으로 고정하므로, 4-eyes(2차 승인자 분리) 전이를
    /// 거치려면 다른 sub의 토큰이 필요하다. 권한은 "*"(ADMIN)로 perm:rms:manage를 통과시킨다.
    /// (RmsApprovalWorkflowIntegrationTests.CreateClientAs와 동일 방식 — 기존 RMS 테스트의 신원 커스터마이즈 패턴 재사용.)</summary>
    private HttpClient CreateClientAs(string userId)
    {
        var client = _factory.CreateClient();
        var jwt = _factory.Services.GetRequiredService<IJwtService>();
        var token = jwt.GenerateAccessToken(userId, userId, "DEFAULT", new[] { "ADMIN" }, permissions: new[] { "*" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task CreateNewVersion_requires_auth()
    {
        // 토큰 없는 요청은 [Authorize]로 401이어야 한다(라우팅·인증 파이프라인 먼저 강제 — 대상 존재 무관, 시드 불필요).
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync(
            "/api/v1/rms/recipes/ANY/new-version",
            new { newRecipeId = "ANY-V2" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "RmsController.CreateNewVersion은 [Authorize]+perm:rms:manage라 토큰 없는 요청은 401이어야 한다");
    }

    [Fact]
    public async Task CreateNewVersion_on_draft_recipe_is_rejected_with_conflict()
    {
        // 생성 직후 Draft 레시피는 새 버전 파생 대상이 아니다 — 서비스가 비-Released를 Error.Conflict로 거부(409).
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:rms:manage 통과
        const string recipeId = "RMS-VER-DRAFT";
        const string equipmentClassId = "CLS-RMS-VER-DRAFT";

        var createResp = await client.PostAsJsonAsync("/api/v1/rms/recipes", new
        {
            recipeId, name = "Draft Recipe", description = "", equipmentClassId
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"레시피 생성(RMS_RECIPE INSERT)이 성공해야 한다. 응답 본문: {await createResp.Content.ReadAsStringAsync()}");

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/rms/recipes/{recipeId}/new-version",
            new { newRecipeId = "RMS-VER-DRAFT-V2" });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            $"Draft(비-Released) 원본의 new-version은 Error.Conflict→409여야 한다. 응답 본문: {body}");

        // 거부됐으므로 파생본은 영속되지 않아야 한다(부수효과 없음).
        var ghost = await client.GetAsync("/api/v1/rms/recipes/RMS-VER-DRAFT-V2");
        ghost.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "거부된 new-version의 신규 레시피는 INSERT되지 않아 GET 시 404여야 한다");
    }

    [Fact]
    public async Task CreateNewVersion_on_missing_recipe_returns_not_found()
    {
        // 존재하지 않는 원본 — 서비스가 Error.NotFound를 반환 → NotFoundObjectResult(404).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/rms/recipes/RMS-VER-NOPE/new-version",
            new { newRecipeId = "RMS-VER-NOPE-V2" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "미존재 원본의 new-version은 Error.NotFound→404여야 한다");
    }

    [Fact]
    public async Task CreateNewVersion_from_released_recipe_derives_draft_with_incremented_version()
    {
        // 해피패스: 원본을 Released까지 전이한 뒤 new-version → Version+1·Draft 파생본이 영속·복원되고,
        //          원본은 Released·Version 불변으로 유지되는지 왕복 검증한다.
        var firstClient = _factory.CreateAuthenticatedClient();   // sub="test-admin"(1차 승인자)
        var secondClient = CreateClientAs(SecondApprover);        // sub="test-approver2"(2차 승인자, 4-eyes)
        const string sourceId = "RMS-VER-SRC";
        const string newId = "RMS-VER-SRC-V2";
        const string equipmentClassId = "CLS-RMS-VER";

        // 0) 레시피 생성(Draft, Version=1).
        var createResp = await firstClient.PostAsJsonAsync("/api/v1/rms/recipes", new
        {
            recipeId = sourceId,
            name = "Versioned Recipe",
            description = "Source recipe for versioning",
            equipmentClassId
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"레시피 생성(RMS_RECIPE INSERT)이 성공해야 한다. 응답 본문: {await createResp.Content.ReadAsStringAsync()}");
        var created = await createResp.Content.ReadFromJsonAsync<RecipeDto>();
        created.Should().NotBeNull();
        created!.Version.Should().Be(1, "신규 레시피의 초기 버전은 1이어야 한다");

        // 1) Draft → Released 다단계 전이(request-approval→approve1→approve2[다른 sub]→release).
        await Transition(firstClient, $"/api/v1/rms/recipes/{sourceId}/request-approval", "승인요청(Draft→WaitApproval)");
        await Transition(firstClient, $"/api/v1/rms/recipes/{sourceId}/approve1", "1차 승인(WaitApproval→Approved1)");
        await Transition(secondClient, $"/api/v1/rms/recipes/{sourceId}/approve2", "2차 승인(Approved1→Approved, 4-eyes)");
        await Transition(firstClient, $"/api/v1/rms/recipes/{sourceId}/release", "배포(Approved→Released)");

        var released = await ReadRecipe(firstClient, sourceId);
        released.ApprovalState.Should().Be("Released", "전이 누적 후 원본은 Released여야 한다(new-version 전제조건)");
        released.Version.Should().Be(1, "배포만으로는 버전이 증가하지 않으므로 원본 버전은 여전히 1이어야 한다");

        // 2) new-version 파생 — 컨트롤러는 onSuccess 미지정이라 200 OK + 새 Recipe 본문을 반환한다(204 아님).
        var versionResp = await firstClient.PostAsJsonAsync(
            $"/api/v1/rms/recipes/{sourceId}/new-version",
            new { newRecipeId = newId });
        var versionBody = await versionResp.Content.ReadAsStringAsync();
        versionResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Released 원본의 new-version(RMS_RECIPE INSERT)은 200 OK + 새 레시피 본문이어야 한다. 응답 본문: {versionBody}");

        var derived = await versionResp.Content.ReadFromJsonAsync<RecipeDto>();
        derived.Should().NotBeNull("new-version 응답은 파생된 새 레시피로 역직렬화돼야 한다");
        derived!.Id.Should().Be(newId, "파생 레시피의 식별자는 요청한 newRecipeId여야 한다");
        derived.Version.Should().Be(2, "파생 레시피의 버전은 원본(1)+1이어야 한다(CreateNewVersion 규칙)");
        derived.ApprovalState.Should().Be("Draft", "파생 레시피는 새 Draft로 시작해야 한다(승인 진행 미상속)");
        derived.EquipmentClassId.Should().Be(equipmentClassId, "파생 레시피는 원본의 설비클래스를 그대로 복사해야 한다");

        // 3) 새 레시피 되읽기(GET) — RMS_RECIPE INSERT가 영속되고 읽기경로(Restore)에서 Draft·Version=2로 복원되는지.
        //    Restore가 아닌 Create로 재구성하면 Version이 1로 강제돼 본 단언이 실패하고 그 차이가 곧 읽기경로 상태손실 버그다.
        var rereadNew = await ReadRecipe(firstClient, newId);
        rereadNew.Version.Should().Be(2,
            "되읽은 파생 레시피의 Version은 2로 영속·복원돼야 한다 — 1이면 읽기경로 상태손실(ToDomain이 Create로 재구성) 버그다");
        rereadNew.ApprovalState.Should().Be("Draft", "되읽은 파생 레시피의 상태는 Draft여야 한다");
        rereadNew.EquipmentClassId.Should().Be(equipmentClassId, "되읽은 파생 레시피의 설비클래스가 원본과 동일해야 한다");

        // 4) 원본 불변 확인 — CreateNewVersion은 source(this)를 변형하지 않으므로 원본은 여전히 Released·Version=1.
        var rereadSource = await ReadRecipe(firstClient, sourceId);
        rereadSource.ApprovalState.Should().Be("Released",
            "파생 후에도 원본은 Released로 유지돼야 한다(new-version은 원본을 변형하지 않음)");
        rereadSource.Version.Should().Be(1, "파생 후에도 원본 버전은 1로 불변이어야 한다");
    }

    private async Task Transition(HttpClient client, string path, string label)
    {
        var resp = await client.PutAsync(path, null);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"{label} 전이는 204 NoContent여야 한다. 응답 본문: {body}");
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
    // Id는 AuditableEntity 식별자(RECIPE_ID). Version·ApprovalState로 버전 파생/상태를 검증한다.
    private sealed record RecipeDto(
        string Id,
        string RecipeName,
        string EquipmentClassId,
        int Version,
        string ApprovalState);
}
