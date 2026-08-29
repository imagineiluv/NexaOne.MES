using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>§20.12 사용자 메뉴 개인화 E2E — 실 SQLite(dev 메뉴 시드 NX_DEV_*) 위에서 즐겨찾기 추가(멱등·폴더
/// 거부·Screen 존재 검증)/삭제/재정렬 + 최근 기록(재사용 갱신) + 사용자 격리(@currentUser 스코프 SQL)를
/// 검증한다. SQL은 명명 레지스트리 단일 출처(SYS.*FavoriteMenu/*RecentMenu) — 방언 패리티 테스트가 별도 가드.</summary>
public sealed class SysPersonalizationTests : IClassFixture<SysPersonalizationTests.HostFactory>
{
    private const string Secret = "sys-personalization-e2e-jwt-secret-key-at-least-32b";
    private const string Issuer = "nexaone-personalization-test";
    private readonly HostFactory _factory;
    public SysPersonalizationTests(HostFactory factory) => _factory = factory;

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-persona-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");           // Dev + 빈 SQLite → SYS_MENU 시드(SmartUX + NX_DEV_*) 활성
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private HttpClient Client(string userId)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed record FavRow(string MenuId, string MenuName, string UiId, int DisplaySequence);
    private sealed record RecentRow(string MenuId, string MenuName, string UiId, DateTime LastUsedAt);

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var res = await _factory.CreateClient().GetAsync("/api/v1/sys/favorites");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "개인화는 인증 필수(자기 데이터 스코프)");
    }

    [Fact]
    public async Task Favorite_add_is_idempotent_validates_screen_and_isolates_users()
    {
        var userA = Client("persona-a");

        // 추가 — dev 시드의 Screen 메뉴(NX_DEV_MENU → SYS_MENU_MGMT)
        var addRes = await userA.PostAsJsonAsync("/api/v1/sys/favorites", new { menuId = "NX_DEV_MENU" });
        addRes.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"본문: {await addRes.Content.ReadAsStringAsync()}");
        // 중복 추가 — 멱등(§20.12 '중복 시 기존 활성화'의 웹 적응)
        (await userA.PostAsJsonAsync("/api/v1/sys/favorites", new { menuId = "NX_DEV_MENU" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        // 폴더/미존재 — Screen 존재 검증에 걸려 조용히 0행
        (await userA.PostAsJsonAsync("/api/v1/sys/favorites", new { menuId = "NX_DEV" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await userA.PostAsJsonAsync("/api/v1/sys/favorites", new { menuId = "NO_SUCH_MENU" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var favs = await userA.GetFromJsonAsync<List<FavRow>>("/api/v1/sys/favorites");
        favs!.Should().ContainSingle(f => f.MenuId == "NX_DEV_MENU",
            "중복은 멱등, 폴더/미존재는 미기록이어야 한다");
        favs!.Single(f => f.MenuId == "NX_DEV_MENU").UiId.Should().Be("SYS_MENU_MGMT",
            "셸 내비게이션용 UI_ID가 조인돼야 한다");

        // 사용자 격리 — SQL이 @currentUser 스코프라 타인 즐겨찾기는 보이지 않는다
        var userB = Client("persona-b");
        (await userB.GetFromJsonAsync<List<FavRow>>("/api/v1/sys/favorites"))!
            .Should().NotContain(f => f.MenuId == "NX_DEV_MENU");
    }

    [Fact]
    public async Task Favorite_reorder_and_remove_roundtrip()
    {
        var user = Client("persona-reorder");
        await user.PostAsJsonAsync("/api/v1/sys/favorites", new { menuId = "NX_DEV_MENU" });
        await user.PostAsJsonAsync("/api/v1/sys/favorites", new { menuId = "NX_DEV_GRID" });

        // 역순 재정렬 → GRID(1), MENU(2)
        (await user.PutAsJsonAsync("/api/v1/sys/favorites/order", new { menuIds = new[] { "NX_DEV_GRID", "NX_DEV_MENU" } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var ordered = await user.GetFromJsonAsync<List<FavRow>>("/api/v1/sys/favorites");
        ordered!.Select(f => f.MenuId).Should().ContainInOrder("NX_DEV_GRID", "NX_DEV_MENU");

        // 삭제(멱등) → 목록에서 제거
        (await user.DeleteAsync("/api/v1/sys/favorites?menuId=NX_DEV_GRID"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.GetFromJsonAsync<List<FavRow>>("/api/v1/sys/favorites"))!
            .Should().NotContain(f => f.MenuId == "NX_DEV_GRID");
    }

    [Fact]
    public async Task Recent_records_screen_usage_and_ignores_unknown()
    {
        var user = Client("persona-recent");
        (await user.PostAsJsonAsync("/api/v1/sys/recent-menus", new { menuId = "NX_DEV_MENU" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.PostAsJsonAsync("/api/v1/sys/recent-menus", new { menuId = "NX_DEV_GRID" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        // 폴더/미존재는 조용히 무시(탐색 부수 기록 — §20.12)
        (await user.PostAsJsonAsync("/api/v1/sys/recent-menus", new { menuId = "NX_DEV" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var recents = await user.GetFromJsonAsync<List<RecentRow>>("/api/v1/sys/recent-menus");
        recents!.Select(r => r.MenuId).Should().BeEquivalentTo(new[] { "NX_DEV_MENU", "NX_DEV_GRID" },
            "Screen 사용만 기록되고 폴더는 무시돼야 한다");

        // 재사용 — 같은 메뉴 재기록은 행 증가 없이 LAST_USED_AT 갱신(맨 앞 이동)
        (await user.PostAsJsonAsync("/api/v1/sys/recent-menus", new { menuId = "NX_DEV_MENU" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.GetFromJsonAsync<List<RecentRow>>("/api/v1/sys/recent-menus"))!
            .Should().HaveCount(2, "재사용은 upsert(행 불변)여야 한다");
    }

    private sealed record CondItem(string Name, DateTime SavedAt, Dictionary<string, string?> Values);
    private sealed record CondSetting(CondItem? Latest, List<CondItem> Items);

    [Fact]
    public async Task Condition_save_load_latest_and_delete_roundtrip()
    {
        var user = Client("persona-cond");
        const string menu = "/FDC/Monitor/";   // 정규화 검증 — 소문자·후행 슬래시 제거로 같은 버킷

        // 저장 + $latest 자동 저장
        (await user.PostAsJsonAsync("/api/v1/sys/conditions",
            new { menuId = menu, name = "야간조", values = new Dictionary<string, string?> { ["shift"] = "N" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.PostAsJsonAsync("/api/v1/sys/conditions/latest",
            new { menuId = "/fdc/monitor", values = new Dictionary<string, string?> { ["shift"] = "D" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var loaded = await user.GetFromJsonAsync<CondSetting>("/api/v1/sys/conditions?menuId=/fdc/monitor");
        loaded!.Latest.Should().NotBeNull("$latest는 Latest 슬롯으로 분리돼야 한다");
        loaded.Latest!.Values["shift"].Should().Be("D");
        loaded.Items.Should().ContainSingle(i => i.Name == "야간조",
            "경로 변형(/FDC/Monitor/ vs /fdc/monitor)은 정규화로 같은 버킷이어야 한다");

        // '$' 예약 조건명 거부 + $latest 수동 삭제 보호
        (await user.PostAsJsonAsync("/api/v1/sys/conditions",
            new { menuId = menu, name = "$Latest", values = new Dictionary<string, string?>() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "'$' 접두 조건명은 예약(§20.8)");
        (await user.DeleteAsync("/api/v1/sys/conditions?menuId=/fdc/monitor&name=%24latest"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "$latest는 초기화 전용 경로로만 삭제");

        // 이름 삭제(404 분기 포함) + 최근 조건 초기화
        (await user.DeleteAsync("/api/v1/sys/conditions?menuId=/fdc/monitor&name=없는조건"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await user.DeleteAsync("/api/v1/sys/conditions?menuId=/fdc/monitor&name=야간조"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.DeleteAsync("/api/v1/sys/conditions/latest?menuId=/fdc/monitor"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var emptied = await user.GetFromJsonAsync<CondSetting>("/api/v1/sys/conditions?menuId=/fdc/monitor");
        emptied!.Latest.Should().BeNull();
        emptied.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_keeps_only_latest_ten_user_saves()
    {
        var user = Client("persona-cond-cap");
        for (var i = 1; i <= 12; i++)
            (await user.PostAsJsonAsync("/api/v1/sys/conditions",
                new { menuId = "/cap", name = $"조건{i:00}", values = new Dictionary<string, string?> { ["i"] = i.ToString() } }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        await user.PostAsJsonAsync("/api/v1/sys/conditions/latest",
            new { menuId = "/cap", values = new Dictionary<string, string?>() });

        var loaded = await user.GetFromJsonAsync<CondSetting>("/api/v1/sys/conditions?menuId=/cap");
        loaded!.Items.Should().HaveCount(10, "사용자 저장 조건 한도는 10(§20.8), 오래된 것부터 정리");
        loaded.Items.Should().NotContain(i => i.Name == "조건01" || i.Name == "조건02");
        loaded.Latest.Should().NotBeNull("$latest는 한도에 포함되지 않는다");
    }

    [Fact]
    public async Task Dashboard_summary_query_returns_single_row_with_all_kpi_columns()
    {
        // DASHBOARD_SUMMARY 화면(KPI 카드 5장)의 데이터 원천 — 모듈 횡단 카운트 1행(공개 read 쿼리).
        var res = await Client("persona-dash").PostAsJsonAsync(
            "/api/v1/query/SYS.DashboardSummary", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>();
        rows!.Should().HaveCount(1, "요약은 항상 1행(카운트 스칼라 5종)");
        foreach (var col in new[] { "ACTIVE_ALARMS", "OPEN_WORK_ORDERS", "ACTIVE_PLANS", "PENDING_RECIPE_APPROVALS", "OPEN_DELIVERY_ORDERS" })
            rows![0].Keys.Should().Contain(col, "KPI 카드가 바인딩하는 컬럼이 전부 있어야 한다");
    }

    [Fact]
    public async Task Recent_keeps_only_latest_ten()
    {
        // dev 시드 SmartUX 트리에서 Screen 잎 12개를 뽑아 순차 기록 → 상한 10개 유지(§20.12 RecentMenuCount).
        var user = Client("persona-cap");
        var menuRows = await Client("persona-cap").PostAsJsonAsync(
            "/api/v1/query/SYS.MenuTreeForUser", new Dictionary<string, object>());
        menuRows.EnsureSuccessStatusCode();
        var all = await menuRows.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>();
        var screens = all!
            .Where(r => (r.GetValueOrDefault("MENU_TYPE")?.ToString() ?? "") == "Screen")
            .Select(r => r.GetValueOrDefault("MENU_ID")?.ToString() ?? "")
            .Where(id => id.Length > 0)
            .Take(12)
            .ToList();
        screens.Count.Should().BeGreaterThan(10, "상한 검증에는 10개 초과 Screen이 필요하다(dev 시드 전제)");

        foreach (var menuId in screens)
            (await user.PostAsJsonAsync("/api/v1/sys/recent-menus", new { menuId }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await user.GetFromJsonAsync<List<RecentRow>>("/api/v1/sys/recent-menus"))!
            .Should().HaveCount(10, "10개 초과분은 오래된 것부터 정리돼야 한다");
    }
}
