using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Web.Services.Meta;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>화면정의 영속 E2E(Phase 5a) — modules OFF + SQLite 임시 DB로 다음을 증명한다:
/// command 게이트웨이(SYS.UpsertScreenDefinition)가 DB에 쓰고, GatewayScreenDefinitionProvider가
/// DB에서 읽어 Layout까지 역직렬화하며(라운드트립), 쓰기쿼리 권한 게이트(sys:manage)와 쿼리 카탈로그
/// 노출(GET /api/v1/sys/queries), 그리고 DB 정의가 InMemory 시드를 덮어쓰는 우선순위를 확인한다.</summary>
public sealed class ScreenDefinitionPersistenceTests : IClassFixture<ScreenDefinitionPersistenceTests.ScreenDefFactory>
{
    private const string Secret = "phase5a-screendef-e2e-jwt-secret-key-at-least-32-bytes-long";
    private const string Issuer = "nexaone-test";
    private readonly ScreenDefFactory _factory;
    public ScreenDefinitionPersistenceTests(ScreenDefFactory factory) => _factory = factory;

    public sealed class ScreenDefFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-screendef-e2e-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
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
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    /// <summary>호스트 QueryCatalogController.QueryDescriptor와 대응(웹 JSON 기본 — 대소문자 무시 바인딩).</summary>
    private sealed record CatalogItem(string id, bool isWrite, string? requiredPermission);

    /// <summary>SectionNode→GridWidget(QueryId) 레이아웃을 가진 화면정의 JSON을 만든다.</summary>
    private static string LayoutDefinitionJson(string uiId, string title, string queryId) =>
        ScreenDefinitionJson.Serialize(new ScreenDefinition(
            uiId, title, Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Id = "sec",
                Children = new LayoutNode[] { new GridWidget { Id = "g", QueryId = queryId } },
            }));

    private static IScreenDefinitionProvider Provider(ScreenDefFactory factory) =>
        factory.Services.GetRequiredService<IScreenDefinitionProvider>();

    [Fact]
    public async Task Upsert_then_provider_loads_layout_definition()
    {
        var client = AuthedClient("sys:manage");
        var uiId = "T_LAYOUT_" + Guid.NewGuid().ToString("N")[..8];

        var save = await client.PostAsJsonAsync("/api/v1/command/SYS.UpsertScreenDefinition", new Dictionary<string, object>
        {
            ["uiId"] = uiId,
            ["title"] = "T",
            ["definitionJson"] = LayoutDefinitionJson(uiId, "T", "MDM.PlantList"),
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK, "sys:manage 권한으로 화면정의 upsert는 성공해야 한다");

        var def = await Provider(_factory).GetAsync(uiId);
        def.Should().NotBeNull("provider가 방금 영속한 정의를 DB에서 읽어야 한다");
        def!.Layout.Should().NotBeNull("DB에 저장된 Layout이 역직렬화돼야 한다");
        def.Layout.Should().BeOfType<SectionNode>("최상위 Layout은 SectionNode여야 한다");

        var section = (SectionNode)def.Layout!;
        section.Children.Should().NotBeNull();
        var grid = section.Children!.OfType<GridWidget>().SingleOrDefault();
        grid.Should().NotBeNull("SectionNode 자식에서 GridWidget을 찾아야 한다");
        grid!.QueryId.Should().Be("MDM.PlantList", "GridWidget.QueryId가 라운드트립돼야 한다");
    }

    [Fact]
    public async Task Upsert_without_sys_manage_is_forbidden()
    {
        var client = AuthedClient("fdc:read");   // sys:manage 없음
        var uiId = "T_FORBID_" + Guid.NewGuid().ToString("N")[..8];

        var res = await client.PostAsJsonAsync("/api/v1/command/SYS.UpsertScreenDefinition", new Dictionary<string, object>
        {
            ["uiId"] = uiId,
            ["title"] = "T",
            ["definitionJson"] = LayoutDefinitionJson(uiId, "T", "MDM.PlantList"),
        });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "쓰기쿼리 requiredPermission(sys:manage) 미보유 시 403");

        var def = await Provider(_factory).GetAsync(uiId);
        def.Should().BeNull("거부된 upsert는 DB에 영속되지 않아야 한다(시드에도 없는 uiId)");
    }

    [Fact]
    public async Task Query_catalog_lists_registered_queries()
    {
        var client = AuthedClient("sys:manage");

        var res = await client.GetAsync("/api/v1/sys/queries");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await res.Content.ReadFromJsonAsync<List<CatalogItem>>();
        items.Should().NotBeNull();
        items!.Should().Contain(
            i => i.id == "SYS.UpsertScreenDefinition" && i.isWrite && i.requiredPermission == "sys:manage",
            "쓰기쿼리 SYS.UpsertScreenDefinition은 isWrite=true·requiredPermission=sys:manage로 노출돼야 한다");
        items.Should().Contain(i => i.id == "MDM.PlantList", "조회쿼리 MDM.PlantList도 카탈로그에 노출돼야 한다");
    }

    [Fact]
    public async Task Query_catalog_without_sys_manage_is_forbidden()
    {
        var client = AuthedClient("fdc:read");   // sys:manage 없음
        var res = await client.GetAsync("/api/v1/sys/queries");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "카탈로그는 sys:manage 미보유 시 403");
    }

    [Fact]
    public async Task Db_definition_overrides_seed()
    {
        var client = AuthedClient("sys:manage");

        // 시드 DEMO_GRID의 제목은 "데모 — 메타데이터 그리드(파일 쿼리)" — DB에 "OVERRIDDEN"으로 덮어쓰면
        // provider가 DB판을 반환함이 Title 비교로 증명된다(DB가 InMemory 시드보다 우선).
        var save = await client.PostAsJsonAsync("/api/v1/command/SYS.UpsertScreenDefinition", new Dictionary<string, object>
        {
            ["uiId"] = "DEMO_GRID",
            ["title"] = "OVERRIDDEN",
            ["definitionJson"] = LayoutDefinitionJson("DEMO_GRID", "OVERRIDDEN", "MDM.AreaList"),
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK);

        var def = await Provider(_factory).GetAsync("DEMO_GRID");
        def.Should().NotBeNull();
        def!.Title.Should().Be("OVERRIDDEN", "DB 정의가 InMemory 시드(DEMO_GRID)를 덮어써야 한다");
        def.Layout.Should().BeOfType<SectionNode>("DB판의 Layout(SectionNode→GridWidget)이 반환돼야 한다");
        var grid = ((SectionNode)def.Layout!).Children!.OfType<GridWidget>().Single();
        grid.QueryId.Should().Be("MDM.AreaList", "반환된 Layout은 DB판(MDM.AreaList)이어야 한다");
    }
}
