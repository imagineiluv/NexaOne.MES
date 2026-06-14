using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexaOne.API.Controllers.Models;

namespace NexaOne.IntegrationTests.Query;

/// <summary>
/// RuleController 원시 실행 경로(명명 게이트웨이 /query/{id}·/command/{id}가 아닌 *raw* 디스패처) end-to-end 검증.
/// 대상 4종:
///   - POST /api/v1/rule/{ruleName}      ([Authorize]만 — 인증 사용자면 실행)
///   - POST /api/v1/query                (원시 SQL, [Authorize(Policy="perm:sys:manage")])
///   - POST /api/v1/procedure            (저장프로시저, perm:sys:manage)
///   - POST /api/v1/procedure/dataset    (저장프로시저 다중결과, perm:sys:manage)
///
/// 핵심 가치는 인가 가드(권한상승/RCE급 원시 SQL·프로시저 경로 봉쇄):
///   미인증 401 → 권한없는 토큰('fdc:read') 403 → ADMIN("*") 통과 를 각각 단언한다.
///
/// 해피 경로:
///   - POST /query 는 ADMIN으로 무해 쿼리('SELECT 1')를 실제 SQLite에서 실행해 200 + RuleResponse{ isSuccess, data } 직렬화 확인.
///   - POST /rule/{ruleName} 은 인증 사용자가 호출 시 RuleResponse 직렬화 확인. 등록 rule 빈/null(디스패처가
///     미등록 rule 예외를 흡수해 Data=null 반환)이어도 200·isSuccess=true 허용.
///   - procedure/procedure/dataset 의 실행 해피는 SQLite에 저장프로시저 개념이 없어 스킵하고(아래 주석),
///     인가 가드(401/403)만 검증한다.
///
/// 클래스별 고유 SQLite 임시파일 DB(GUID) + FK 비강제 + db/migrations 부트스트랩(TestApiFactory).
/// 기본 인증 클라이언트는 ADMIN "*"(전체 권한, sub="test-admin")이라 perm:sys:manage를 통과한다.
/// </summary>
public sealed class RawDispatcherIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public RawDispatcherIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ──────────────────────────────────────────────────────────────────────────────
    // POST /api/v1/query — 원시 SQL (perm:sys:manage)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawQuery_requires_authentication()
    {
        var anon = _factory.CreateClient();
        var req = new QueryRequest { Sql = "SELECT 1" };
        var resp = await anon.PostAsJsonAsync("/api/v1/query", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "원시 SQL 실행은 토큰 없으면 401이어야 한다(권한상승/RCE급 경로)");
    }

    [Fact]
    public async Task RawQuery_forbidden_without_sys_manage_permission()
    {
        // sys:manage가 없는 권한(fdc:read)만 가진 인증 토큰 — 인가는 핸들러보다 우선하므로 403.
        var limited = _factory.CreateAuthenticatedClient("fdc:read");
        var req = new QueryRequest { Sql = "SELECT 1" };
        var resp = await limited.PostAsJsonAsync("/api/v1/query", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "sys:manage 권한이 없으면 원시 SQL 실행은 403이어야 한다");
    }

    [Fact]
    public async Task RawQuery_admin_executes_harmless_select_and_returns_data()
    {
        var client = _factory.CreateAuthenticatedClient();   // ADMIN "*", perm:sys:manage 통과
        var req = new QueryRequest { Sql = "SELECT 1" };

        var resp = await client.PostAsJsonAsync("/api/v1/query", req);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ADMIN의 무해 원시 쿼리(SELECT 1)는 SQLite에서 실행돼 200이어야 한다. 응답 본문: {body}");

        // RuleResponse<object> 직렬화 — isSuccess=true, data는 행 배열(SELECT 1 → 1행).
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        GetProp(root, "isSuccess").GetBoolean().Should().BeTrue("RuleResponse.IsSuccess=true 직렬화");

        var data = GetProp(root, "data");
        data.ValueKind.Should().Be(JsonValueKind.Array, "QueryAsync 결과(행 목록)는 JSON 배열로 직렬화돼야 한다");
        data.GetArrayLength().Should().Be(1, "SELECT 1 은 정확히 한 행을 반환해야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // POST /api/v1/procedure — 저장프로시저 (perm:sys:manage). 인가 가드만 검증.
    // SQLite에는 저장프로시저가 없어 실행 해피는 스킵한다(ADMIN 호출 시 SqliteException → 실행 시도 자체가
    // 방언 비호환이므로 회귀 가치가 없음). 가드(미인증/권한없음)는 핸들러 진입 전에 평가되므로 검증한다.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawProcedure_requires_authentication()
    {
        var anon = _factory.CreateClient();
        var req = new ProcedureRequest { ProcedureName = "usp_Anything" };
        var resp = await anon.PostAsJsonAsync("/api/v1/procedure", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "저장프로시저 실행은 토큰 없으면 401이어야 한다");
    }

    [Fact]
    public async Task RawProcedure_forbidden_without_sys_manage_permission()
    {
        var limited = _factory.CreateAuthenticatedClient("fdc:read");
        var req = new ProcedureRequest { ProcedureName = "usp_Anything" };
        var resp = await limited.PostAsJsonAsync("/api/v1/procedure", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "sys:manage 권한이 없으면 저장프로시저 실행은 403이어야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // POST /api/v1/procedure/dataset — 저장프로시저 다중결과 (perm:sys:manage). 인가 가드만 검증(상동).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawProcedureDataSet_requires_authentication()
    {
        var anon = _factory.CreateClient();
        var req = new ProcedureRequest { ProcedureName = "usp_Anything" };
        var resp = await anon.PostAsJsonAsync("/api/v1/procedure/dataset", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "저장프로시저(dataset) 실행은 토큰 없으면 401이어야 한다");
    }

    [Fact]
    public async Task RawProcedureDataSet_forbidden_without_sys_manage_permission()
    {
        var limited = _factory.CreateAuthenticatedClient("fdc:read");
        var req = new ProcedureRequest { ProcedureName = "usp_Anything" };
        var resp = await limited.PostAsJsonAsync("/api/v1/procedure/dataset", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "sys:manage 권한이 없으면 저장프로시저(dataset) 실행은 403이어야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // POST /api/v1/rule/{ruleName} — [Authorize]만(인증 사용자면 실행).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRule_requires_authentication()
    {
        var anon = _factory.CreateClient();
        var req = new RuleRequest { Body = new Dictionary<string, object>() };
        var resp = await anon.PostAsJsonAsync("/api/v1/rule/AnyRule", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "rule 실행은 토큰 없으면 401이어야 한다([Authorize])");
    }

    [Fact]
    public async Task ExecuteRule_authenticated_user_passes_authorize_and_returns_rule_response()
    {
        // rule/{ruleName} 은 perm:sys:manage가 아니라 단순 [Authorize] — sys:manage 없는 인증 토큰도 통과해야 한다.
        // 등록된 rule이 없으면 디스패처(NexaFrameworkRuleDispatcher.DispatchAsync)가 예외를 흡수해 null을 반환하므로
        // 컨트롤러는 200 + RuleResponse{ isSuccess=true, data=null } 을 돌려준다(빈/null Data 허용).
        var client = _factory.CreateAuthenticatedClient("fdc:read");   // sys:manage 없음 — 그래도 통과
        var req = new RuleRequest { Body = new Dictionary<string, object> { ["k"] = "v" } };

        var resp = await client.PostAsJsonAsync("/api/v1/rule/SomeUnregisteredRule", req);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"인증 사용자의 rule 호출은 200이어야 한다(미등록 rule이어도 디스패처가 null 흡수). 응답 본문: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        GetProp(root, "isSuccess").GetBoolean().Should().BeTrue("RuleResponse.IsSuccess=true 직렬화");

        // 미등록 rule → Data=null. 직렬화 시 data 프로퍼티가 null이거나(또는 무시 정책상 없을 수 있음) 빈 값이어야 한다.
        if (TryGetProp(root, "data", out var data))
            data.ValueKind.Should().Be(JsonValueKind.Null,
                "미등록 rule은 디스패처가 null을 반환하므로 data는 null이어야 한다");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    // ASP.NET Core 기본 직렬화는 camelCase(isSuccess/data)지만, 대소문자 무관하게 안전히 조회한다.
    private static JsonElement GetProp(JsonElement obj, string name)
    {
        TryGetProp(obj, name, out var value).Should().BeTrue($"응답 JSON에 '{name}' 프로퍼티가 있어야 한다");
        return value;
    }

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
