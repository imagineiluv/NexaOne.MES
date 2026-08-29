using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Server.Gateway;
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
    private sealed record CatalogItem(
        string id,
        bool isWrite,
        string? requiredPermission,
        string? source = null,
        string? effect = null,
        string? executionMode = null);
    private sealed record ScreenRow(
        string UI_ID, string TITLE, string? DEFINITION_JSON, string TARGET_CHANNEL, string ENTRY_PATH);

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

    private (string Channel, string EntryPath)? ReadTarget(string uiId)
    {
        using var conn = new SqliteConnection($"Data Source={_factory.DbPath};Foreign Keys=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TARGET_CHANNEL, ENTRY_PATH FROM SYS_SCREEN_TARGET WHERE UI_ID = @uiId";
        cmd.Parameters.AddWithValue("@uiId", uiId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private long CountRows(string table, string uiId)
    {
        using var conn = new SqliteConnection($"Data Source={_factory.DbPath};Foreign Keys=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE UI_ID = @uiId";
        cmd.Parameters.AddWithValue("@uiId", uiId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static async Task<List<ScreenRow>> QueryScreensAsync(
        HttpClient client, string queryId, object parameters)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", parameters);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<ScreenRow>>()) ?? new List<ScreenRow>();
    }

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

        ReadTarget(uiId).Should().Be(("MES", $"/meta/{uiId}"),
            "구 3필드 저장 호출도 MES 기본 대상과 완전 경로를 명시적으로 기록해야 한다");
    }

    [Fact]
    public async Task Upsert_stores_target_path_and_list_filters_by_channel()
    {
        var client = AuthedClient("sys:manage");
        var uiId = "T_TARGET_" + Guid.NewGuid().ToString("N")[..8];
        var json = LayoutDefinitionJson(uiId, "Mobile page", "MDM.PlantList");

        var saveMobile = await client.PostAsJsonAsync(
            "/api/v1/command/SYS.UpsertScreenDefinition",
            new { uiId, title = "Mobile page", definitionJson = json, targetChannel = "MOBILE", entryPath = $"/Mobile/{uiId}" });
        saveMobile.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadTarget(uiId).Should().Be(("MOBILE", $"/Mobile/{uiId}"));

        var loaded = await QueryScreensAsync(client, "SYS.GetScreenDefinition", new { uiId });
        loaded.Should().ContainSingle();
        loaded[0].TARGET_CHANNEL.Should().Be("MOBILE");
        loaded[0].ENTRY_PATH.Should().Be($"/Mobile/{uiId}");

        var mobile = await QueryScreensAsync(client, "SYS.ListScreenDefinitions", new { targetChannel = "MOBILE" });
        var popBefore = await QueryScreensAsync(client, "SYS.ListScreenDefinitions", new { targetChannel = "POP" });
        mobile.Should().Contain(r => r.UI_ID == uiId);
        popBefore.Should().NotContain(r => r.UI_ID == uiId);

        var savePop = await client.PostAsJsonAsync(
            "/api/v1/command/SYS.UpsertScreenDefinition",
            new { uiId, title = "POP page", definitionJson = json, targetChannel = "POP", entryPath = $"/POP/{uiId}" });
        savePop.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadTarget(uiId).Should().Be(("POP", $"/POP/{uiId}"));
        CountRows("SYS_SCREEN_TARGET", uiId).Should().Be(1, "대상 변경은 UI_ID 1:1 행을 갱신해야 한다");

        var mobileAfter = await QueryScreensAsync(client, "SYS.ListScreenDefinitions", new { targetChannel = "MOBILE" });
        var popAfter = await QueryScreensAsync(client, "SYS.ListScreenDefinitions", new { targetChannel = "POP" });
        mobileAfter.Should().NotContain(r => r.UI_ID == uiId);
        popAfter.Should().Contain(r => r.UI_ID == uiId && r.ENTRY_PATH == $"/POP/{uiId}");
    }

    [Fact]
    public async Task Invalid_target_path_rolls_back_definition_and_target_together()
    {
        _ = _factory.CreateClient(); // 호스트/SQLite 스키마 보장
        var registry = _factory.Services.GetRequiredService<IQueryRegistry>();
        var dispatcher = _factory.Services.GetRequiredService<IRuleDispatcher>();
        registry.TryGet("SYS.UpsertScreenDefinition", out var query).Should().BeTrue();
        query.Should().NotBeNull();
        var uiId = "T_ATOMIC_" + Guid.NewGuid().ToString("N")[..8];
        var parameters = new Dictionary<string, object>
        {
            ["uiId"] = uiId,
            ["title"] = "bad route",
            ["definitionJson"] = LayoutDefinitionJson(uiId, "bad route", "MDM.PlantList"),
            ["targetChannel"] = "MOBILE",
            ["entryPath"] = $"/POP/{uiId}", // 채널과 불일치 → ENTRY_PATH NOT NULL 위반
            ["currentUser"] = "e2e-user",
            ["utcNow"] = DateTime.UtcNow,
        };

        var act = () => dispatcher.ExecuteAsync(query!.Sql, parameters);
        await act.Should().ThrowAsync<Exception>();

        CountRows("SYS_SCREEN_DEFINITION", uiId).Should().Be(0,
            "대상 저장 실패 시 앞선 화면정의 INSERT도 같은 command 트랜잭션에서 롤백돼야 한다");
        CountRows("SYS_SCREEN_TARGET", uiId).Should().Be(0);
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
            i => i.id == "SYS.UpsertScreenDefinition"
                 && i.isWrite
                 && i.requiredPermission == "sys:manage"
                 && i.source == "NamedQuery"
                 && i.effect == "Mutating"
                 && i.executionMode == "PerRow",
            "쓰기쿼리는 변경·행 단위 named-query descriptor로 노출돼야 한다");
        items.Should().Contain(
            i => i.id == "MDM.PlantList"
                 && !i.isWrite
                 && i.source == "NamedQuery"
                 && i.effect == "NonMutating"
                 && i.executionMode == "PerRow",
            "조회쿼리는 비변경 named-query descriptor로 노출돼야 한다");
        items.Should().Contain(
            i => i.id == PomWorkOrderMetaCommands.Start
                 && i.isWrite
                 && i.requiredPermission == "pom:execute"
                 && i.source == "BridgeCommand"
                 && i.effect == "Mutating"
                 && i.executionMode == "PerRow",
            "Designer 명령 드롭다운에는 SQL 없는 typed POM bridge 액션도 노출돼야 한다");
        items.Should().Contain(
            i => i.id == MrpConversionMetaCommands.Convert
                 && i.isWrite
                 && i.requiredPermission == "pom:manage"
                 && i.source == "BridgeCommand"
                 && i.effect == "Mutating"
                 && i.executionMode == "HostRequiredAggregate",
            "MRP 전환은 일반 행 실행으로 내려가지 않도록 호스트 일괄 계약을 노출해야 한다");
    }

    [Fact]
    public async Task Query_catalog_without_sys_manage_is_forbidden()
    {
        var client = AuthedClient("fdc:read");   // sys:manage 없음
        var res = await client.GetAsync("/api/v1/sys/queries");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "카탈로그는 sys:manage 미보유 시 403");
    }

    [Fact]
    public async Task Runtime_register_is_an_overlay_and_never_mutates_the_code_catalog()
    {
        const string canonicalUiId = "DEMO_PARAM";
        var client = AuthedClient("sys:manage");
        var provider = Provider(_factory);
        var catalog = _factory.Services.GetRequiredService<ICodeScreenDefinitionCatalog>();
        var original = catalog.Get(canonicalUiId);
        original.Should().NotBeNull();
        var originalTitle = original!.Title;
        var originalPurpose = original.Purpose;
        var originalJson = ScreenDefinitionJson.Serialize(original);

        var runtimeOverride = original with
        {
            Title = "Runtime overlay",
            Purpose = ScreenPurpose.Register,
        };
        provider.Register(runtimeOverride);
        var customUiId = "T_RUNTIME_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var custom = new ScreenDefinition(
            customUiId,
            "Runtime custom",
            Array.Empty<FieldDefinition>(),
            QueryId: "MDM.PlantList");
        provider.Register(custom);

        (await provider.GetAsync(canonicalUiId))!.Title.Should().Be(runtimeOverride.Title,
            "runtime cache must override the built-in code seed when no DB definition exists");
        (await provider.GetAsync(customUiId))!.Title.Should().Be(custom.Title);
        var known = await provider.GetKnownUiIdsAsync();
        known.Should().Contain(canonicalUiId).And.Contain(customUiId);

        catalog.Get(canonicalUiId)!.Title.Should().Be(originalTitle);
        catalog.Get(canonicalUiId)!.Purpose.Should().Be(originalPurpose);
        ScreenDefinitionJson.Serialize(catalog.Get(canonicalUiId)!).Should().Be(originalJson);
        catalog.Get(customUiId).Should().BeNull();
        (await catalog.ListAsync()).Should().ContainSingle(definition => definition.UiId == canonicalUiId)
            .And.NotContain(definition => definition.UiId == customUiId);

        var dbDefinition = runtimeOverride with { Title = "DB-owned definition" };
        var save = await client.PostAsJsonAsync(
            "/api/v1/command/SYS.UpsertScreenDefinition",
            new
            {
                uiId = canonicalUiId,
                title = dbDefinition.Title,
                definitionJson = ScreenDefinitionJson.Serialize(dbDefinition),
            });
        save.EnsureSuccessStatusCode();

        (await provider.GetAsync(canonicalUiId))!.Title.Should().Be(dbDefinition.Title,
            "DB definitions must override the runtime cache");
        catalog.Get(canonicalUiId)!.Title.Should().Be(originalTitle);
        catalog.Get(canonicalUiId)!.Purpose.Should().Be(originalPurpose);
        ScreenDefinitionJson.Serialize(catalog.Get(canonicalUiId)!).Should().Be(originalJson);
    }

    [Fact]
    public async Task Legacy_alias_is_known_while_the_code_catalog_list_remains_canonical()
    {
        const string aliasUiId = "EES_EPT_OVERALL_EQUIPMENT_EFFECIVENESS";
        const string canonicalUiId = "EES_EPT_OVERALL_EQUIPMENT_EFFECTIVENESS";
        var provider = Provider(_factory);
        var catalog = _factory.Services.GetRequiredService<ICodeScreenDefinitionCatalog>();

        var known = await provider.GetKnownUiIdsAsync();
        known.Should().Contain(aliasUiId,
            "legacy routes must remain discoverable by the effective provider");
        (await catalog.GetKnownUiIdsAsync()).Should().Contain(aliasUiId);

        var canonicalDefinitions = await catalog.ListAsync();
        canonicalDefinitions.Should().ContainSingle(definition => definition.UiId == canonicalUiId);
        canonicalDefinitions.Should().NotContain(definition => definition.UiId == aliasUiId,
            "the Designer catalog must not duplicate canonical definitions under aliases");

        catalog.Get(aliasUiId).Should().NotBeNull();
        catalog.Get(aliasUiId)!.UiId.Should().Be(canonicalUiId);
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
