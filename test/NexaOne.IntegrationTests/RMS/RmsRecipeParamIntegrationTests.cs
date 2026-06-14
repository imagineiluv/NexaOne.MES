using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.RMS;

/// <summary>
/// RMS 레시피 파라미터 CRUD HTTP 통합 테스트 — V032 누락 마이그레이션(RMS_RECIPE_PARAM) 회귀 가드.
/// RecipeParamRepository는 RMS_RECIPE_PARAM을 SELECT/INSERT/UPDATE/DELETE 하지만 V005는 RMS_RECIPE만
/// 만들어, 이 테이블이 모든 환경에서 'no such table'로 실패했다(통합=레시피 본체만, 단위=리포 Mock이라 미포착).
/// 본 테스트는 recipes/{id}/params 의 add→list→update→delete 왕복을 SQLite에서 end-to-end로 돌려
/// 테이블 존재 + 방언(SELECT *·ORDER BY·UPDATE/DELETE WHERE) + 직렬화 + 영속을 실증한다.
/// </summary>
public sealed class RmsRecipeParamIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public RmsRecipeParamIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetParams_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/v1/rms/recipes/ANY/params");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "[Authorize] params 조회는 토큰 없이 401");
    }

    [Fact]
    public async Task Param_lifecycle_add_list_update_delete_persists_on_sqlite()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:rms:manage 통과
        const string recipeId = "RMS-PARAM-LIFE";
        const string equipmentClassId = "CLS-RMS-PARAM";

        // 0) 부모 레시피 생성(FK RECIPE_ID 대상; 하니스 FK-OFF이나 앱 일관성 위해 선행).
        (await client.PostAsJsonAsync("/api/v1/rms/recipes", new
        {
            recipeId, name = "Param Recipe", description = "", equipmentClassId
        })).StatusCode.Should().Be(HttpStatusCode.OK, "부모 레시피 생성이 성공해야 한다");

        // 1) 파라미터 2건 추가(POST recipes/{id}/params → RMS_RECIPE_PARAM INSERT).
        //    감사 컬럼은 명시하지 않으므로 DEFAULT로 채워져야 한다(V032 마이그레이션 정합).
        var add1 = await client.PostAsJsonAsync($"/api/v1/rms/recipes/{recipeId}/params", new
        {
            paramId = "P-TEMP", paramName = "Temperature", paramValue = "250", unit = "C", sortOrder = 2
        });
        var add1Body = await add1.Content.ReadAsStringAsync();
        add1.StatusCode.Should().Be(HttpStatusCode.OK,
            $"파라미터 추가(RMS_RECIPE_PARAM INSERT)가 성공해야 한다 — 테이블 부재 시 여기서 500. 본문: {add1Body}");

        (await client.PostAsJsonAsync($"/api/v1/rms/recipes/{recipeId}/params", new
        {
            paramId = "P-PRES", paramName = "Pressure", paramValue = "10", unit = "bar", sortOrder = 1
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // 2) 목록 조회(GET) → 2건, SORT_ORDER 오름차순(Pressure(1) → Temperature(2)).
        var list = await client.GetFromJsonAsync<List<ParamDto>>($"/api/v1/rms/recipes/{recipeId}/params");
        list.Should().NotBeNull();
        list!.Should().HaveCount(2, "추가한 파라미터 2건이 조회돼야 한다");
        list.Select(p => p.Id).Should().ContainInOrder("P-PRES", "P-TEMP");   // ORDER BY SORT_ORDER 적용
        list.Single(p => p.Id == "P-TEMP").ParamValue.Should().Be("250");

        // 3) 값 수정(PUT recipes/params/{paramId} → UPDATE) → 되읽기에 반영.
        var upd = await client.PutAsJsonAsync("/api/v1/rms/recipes/params/P-TEMP", new { newValue = "300" });
        upd.StatusCode.Should().Be(HttpStatusCode.NoContent, "파라미터 수정은 204여야 한다");

        var afterUpd = await client.GetFromJsonAsync<List<ParamDto>>($"/api/v1/rms/recipes/{recipeId}/params");
        afterUpd!.Single(p => p.Id == "P-TEMP").ParamValue.Should().Be("300",
            "수정한 값이 RMS_RECIPE_PARAM에 영속화돼야 한다(읽기경로 반영)");

        // 4) 삭제(DELETE) → 1건만 남음.
        var del = await client.DeleteAsync("/api/v1/rms/recipes/params/P-PRES");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent, "파라미터 삭제는 204여야 한다");

        var afterDel = await client.GetFromJsonAsync<List<ParamDto>>($"/api/v1/rms/recipes/{recipeId}/params");
        afterDel!.Should().ContainSingle().Which.Id.Should().Be("P-TEMP", "삭제 후 1건(P-TEMP)만 남아야 한다");
    }

    // GET params → RecipeParam 도메인(camelCase). Id는 Entity<string> 식별자(PARAM_ID).
    private sealed record ParamDto(
        string Id, string RecipeId, string ParamName, string ParamValue, string Unit, int SortOrder);
}
