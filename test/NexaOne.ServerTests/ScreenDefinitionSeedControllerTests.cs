using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Server.Gateway;
using NexaOne.Web.Services.Meta;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>Designer 코드 시드 조회/가져오기의 권한, 진단, insert-only 원자성을 검증합니다.</summary>
public sealed class ScreenDefinitionSeedControllerTests :
    IClassFixture<ScreenDefinitionSeedControllerTests.ScreenSeedFactory>
{
    private const string Secret = "screen-seed-e2e-jwt-secret-key-at-least-32-bytes-long";
    private const string Issuer = "nexaone-screen-seed-test";
    private readonly ScreenSeedFactory _factory;

    public ScreenDefinitionSeedControllerTests(ScreenSeedFactory factory) => _factory = factory;

    public sealed class ScreenSeedFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-screen-seed-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICodeScreenDefinitionCatalog>();
                services.AddSingleton<TestCodeScreenDefinitionCatalog>();
                services.AddSingleton<ICodeScreenDefinitionCatalog>(sp =>
                    sp.GetRequiredService<TestCodeScreenDefinitionCatalog>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 DB 정리 실패 무시 */ }
        }
    }

    /// <summary>
    /// 테스트가 만드는 synthetic 정의만 canonical 카탈로그 위에 겹치는 전용 overlay입니다.
    /// 운영 <see cref="IScreenDefinitionProvider.Register"/>를 코드 시드 등록 경로로 사용하지 않습니다.
    /// </summary>
    public sealed class TestCodeScreenDefinitionCatalog : ICodeScreenDefinitionCatalog
    {
        private readonly CodeScreenDefinitionCatalog _builtIn;
        private readonly ConcurrentDictionary<string, ScreenDefinition> _overlay =
            new(StringComparer.OrdinalIgnoreCase);

        public TestCodeScreenDefinitionCatalog(CodeScreenDefinitionCatalog builtIn) => _builtIn = builtIn;

        public void Register(ScreenDefinition definition) => _overlay[definition.UiId] = definition;

        public ScreenDefinition? Get(string uiId)
            => _overlay.TryGetValue(uiId, out var definition) ? definition : _builtIn.Get(uiId);

        public async Task<IReadOnlySet<string>> GetKnownUiIdsAsync(CancellationToken ct = default)
        {
            var known = new HashSet<string>(
                await _builtIn.GetKnownUiIdsAsync(ct), StringComparer.OrdinalIgnoreCase);
            known.UnionWith(_overlay.Keys);
            return known;
        }

        public async Task<IReadOnlyList<ScreenDefinition>> ListAsync(CancellationToken ct = default)
        {
            var definitions = (await _builtIn.ListAsync(ct))
                .ToDictionary(definition => definition.UiId, StringComparer.OrdinalIgnoreCase);
            foreach (var definition in _overlay.Values)
                definitions[definition.UiId] = definition;
            return definitions.Values
                .OrderBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private sealed record SeedSummary(
        string UiId,
        string Title,
        string Purpose,
        bool DatabaseExists,
        bool CanImport,
        int ErrorCount,
        int AdvisoryCount);

    private sealed record SeedDiagnostic(string Code, string Severity, string Message);

    private sealed record SeedPreview(
        string UiId,
        string Title,
        string Purpose,
        string DefinitionJson,
        string TargetChannel,
        string EntryPath,
        bool DatabaseExists,
        bool CanImport,
        List<SeedDiagnostic> Diagnostics);

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "seed-admin") };
        claims.AddRange(permissions.Select(permission =>
            new Claim(NexaOne.Common.Security.Permissions.ClaimType, permission)));
        var token = new JwtSecurityToken(
            Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private ScreenDefinition RegisterSeed(
        ScreenPurpose purpose = ScreenPurpose.Auto,
        bool includeWritePath = false)
    {
        var uiId = "T_SEED_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var definition = new ScreenDefinition(
            uiId,
            $"Seed {uiId}",
            purpose is ScreenPurpose.Register or ScreenPurpose.Manage
                ? new FieldDefinition[] { new("name", "Name") }
                : Array.Empty<FieldDefinition>(),
            SaveQueryId: includeWritePath ? "MDM.CreatePlant" : null,
            Purpose: purpose);
        _factory.Services.GetRequiredService<TestCodeScreenDefinitionCatalog>().Register(definition);
        return definition;
    }

    private async Task UpsertAsync(
        HttpClient client,
        string uiId,
        string title,
        string definitionJson,
        string targetChannel = "MES",
        string? entryPath = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/command/SYS.UpsertScreenDefinition",
            new { uiId, title, definitionJson, targetChannel, entryPath });
        response.EnsureSuccessStatusCode();
    }

    private StoredScreen? ReadStored(string uiId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={_factory.DbPath};Foreign Keys=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT D.TITLE, D.DEFINITION_JSON, T.TARGET_CHANNEL, T.ENTRY_PATH " +
            "FROM SYS_SCREEN_DEFINITION D " +
            "LEFT JOIN SYS_SCREEN_TARGET T ON T.UI_ID = D.UI_ID " +
            "WHERE D.UI_ID = @uiId";
        command.Parameters.AddWithValue("@uiId", uiId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredScreen(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private long CountStored(string uiId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={_factory.DbPath};Foreign Keys=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SYS_SCREEN_DEFINITION WHERE UI_ID = @uiId";
        command.Parameters.AddWithValue("@uiId", uiId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed record StoredScreen(
        string Title,
        string DefinitionJson,
        string TargetChannel,
        string EntryPath);

    [Fact]
    public async Task List_exposes_importability_and_capability_counts()
    {
        var client = AuthedClient("sys:manage");
        var valid = RegisterSeed(ScreenPurpose.Manage, includeWritePath: true);
        var invalid = RegisterSeed(ScreenPurpose.Manage);
        var advisory = RegisterSeed();
        await UpsertAsync(
            client,
            valid.UiId,
            "Stored",
            ScreenDefinitionJson.Serialize(valid));

        var response = await client.GetAsync("/api/v1/sys/screen-seeds");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<SeedSummary>>();
        rows.Should().NotBeNull();
        rows!.Should().ContainSingle(row =>
            row.UiId == valid.UiId && row.DatabaseExists && !row.CanImport && row.ErrorCount == 0);
        rows.Should().ContainSingle(row =>
            row.UiId == invalid.UiId && !row.DatabaseExists && !row.CanImport && row.ErrorCount > 0);
        rows.Should().ContainSingle(row =>
            row.UiId == advisory.UiId && row.CanImport && row.AdvisoryCount > 0);
    }

    [Fact]
    public async Task Preview_returns_canonical_json_diagnostics_and_mes_defaults()
    {
        var client = AuthedClient("sys:manage");
        var definition = RegisterSeed();

        var response = await client.GetAsync($"/api/v1/sys/screen-seeds/{definition.UiId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<SeedPreview>();
        preview.Should().NotBeNull();
        preview!.UiId.Should().Be(definition.UiId);
        preview.DefinitionJson.Should().Be(ScreenDefinitionJson.Serialize(definition));
        preview.TargetChannel.Should().Be("MES");
        preview.EntryPath.Should().Be($"/meta/{definition.UiId}");
        preview.DatabaseExists.Should().BeFalse();
        preview.CanImport.Should().BeTrue("Auto purpose is an advisory, not a blocking error");
        preview.Diagnostics.Should().ContainSingle(item => item.Severity == "Advisory");
    }

    [Fact]
    public async Task Import_persists_seed_and_target_with_server_defaults()
    {
        var client = AuthedClient("sys:manage");
        var definition = RegisterSeed(ScreenPurpose.Manage, includeWritePath: true);

        var response = await client.PostAsync(
            $"/api/v1/sys/screen-seeds/{definition.UiId}/import", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<SeedPreview>();
        preview.Should().NotBeNull();
        preview!.DatabaseExists.Should().BeTrue();
        preview.CanImport.Should().BeFalse();
        ReadStored(definition.UiId).Should().Be(new StoredScreen(
            definition.Title,
            ScreenDefinitionJson.Serialize(definition),
            "MES",
            $"/meta/{definition.UiId}"));
    }

    [Fact]
    public async Task Import_rejects_capability_errors_without_writing()
    {
        var client = AuthedClient("sys:manage");
        var invalid = RegisterSeed(ScreenPurpose.Manage);

        var response = await client.PostAsync(
            $"/api/v1/sys/screen-seeds/{invalid.UiId}/import", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        CountStored(invalid.UiId).Should().Be(0);
    }

    [Fact]
    public async Task Contextual_binding_errors_block_seed_import_and_are_exposed_in_preview()
    {
        var client = AuthedClient("sys:manage");
        var uiId = "T_SEED_BINDING_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var definition = new ScreenDefinition(
            uiId,
            "Invalid binding seed",
            Array.Empty<FieldDefinition>(),
            Columns: [new GridColumnDefinition("ID", "ID")],
            QueryId: "UNKNOWN.ReadBinding");
        _factory.Services.GetRequiredService<TestCodeScreenDefinitionCatalog>().Register(definition);

        var previewResponse = await client.GetAsync($"/api/v1/sys/screen-seeds/{uiId}");
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await previewResponse.Content.ReadFromJsonAsync<SeedPreview>();
        preview.Should().NotBeNull();
        preview!.CanImport.Should().BeFalse();
        preview.Diagnostics.Should().ContainSingle(item =>
            item.Code == NexaOne.Server.Gateway.ScreenDefinitionBindingValidator.ReadBindingMissing
            && item.Severity == "Error");

        var importResponse = await client.PostAsync(
            $"/api/v1/sys/screen-seeds/{uiId}/import", content: null);
        importResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        CountStored(uiId).Should().Be(0);
    }

    [Fact]
    public async Task Existing_definition_returns_conflict_and_is_never_overwritten()
    {
        var client = AuthedClient("sys:manage");
        var definition = RegisterSeed(ScreenPurpose.Manage, includeWritePath: true);
        const string storedTitle = "Designer-owned definition";
        var storedJson = ScreenDefinitionJson.Serialize(definition with { Title = storedTitle });
        await UpsertAsync(
            client,
            definition.UiId,
            storedTitle,
            storedJson,
            "POP",
            $"/POP/{definition.UiId}");

        var response = await client.PostAsync(
            $"/api/v1/sys/screen-seeds/{definition.UiId}/import", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ReadStored(definition.UiId).Should().Be(new StoredScreen(
            storedTitle,
            storedJson,
            "POP",
            $"/POP/{definition.UiId}"));
    }

    [Fact]
    public async Task Concurrent_imports_create_one_definition_and_one_conflict()
    {
        var client = AuthedClient("sys:manage");
        var definition = RegisterSeed(ScreenPurpose.Manage, includeWritePath: true);
        var path = $"/api/v1/sys/screen-seeds/{definition.UiId}/import";

        var responses = await Task.WhenAll(
            client.PostAsync(path, content: null),
            client.PostAsync(path, content: null));

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        CountStored(definition.UiId).Should().Be(1);
        ReadStored(definition.UiId)!.DefinitionJson.Should().Be(ScreenDefinitionJson.Serialize(definition));
    }

    [Fact]
    public async Task Every_seed_endpoint_requires_sys_manage()
    {
        var client = AuthedClient("sys:read");
        var definition = RegisterSeed();

        var list = await client.GetAsync("/api/v1/sys/screen-seeds");
        var preview = await client.GetAsync($"/api/v1/sys/screen-seeds/{definition.UiId}");
        var import = await client.PostAsync(
            $"/api/v1/sys/screen-seeds/{definition.UiId}/import", content: null);

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        preview.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        import.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        CountStored(definition.UiId).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_seed_returns_not_found()
    {
        var client = AuthedClient("sys:manage");
        var uiId = "UNKNOWN_" + Guid.NewGuid().ToString("N");

        (await client.GetAsync($"/api/v1/sys/screen-seeds/{uiId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/v1/sys/screen-seeds/{uiId}/import", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Legacy_alias_is_listed_and_imported_only_under_canonical_ui_id()
    {
        const string alias = "EES_EPT_OVERALL_EQUIPMENT_EFFECIVENESS";
        const string canonical = "EES_EPT_OVERALL_EQUIPMENT_EFFECTIVENESS";
        var client = AuthedClient("sys:manage");

        var listResponse = await client.GetAsync("/api/v1/sys/screen-seeds");
        listResponse.EnsureSuccessStatusCode();
        var summaries = await listResponse.Content.ReadFromJsonAsync<List<SeedSummary>>();
        summaries.Should().NotBeNull();
        summaries!.Count(item => item.UiId == canonical).Should().Be(1);
        summaries.Should().NotContain(item => item.UiId == alias);

        var previewResponse = await client.GetAsync($"/api/v1/sys/screen-seeds/{alias}");
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<SeedPreview>();
        preview.Should().NotBeNull();
        preview!.UiId.Should().Be(canonical);
        preview.EntryPath.Should().Be($"/meta/{canonical}");

        var importResponse = await client.PostAsync(
            $"/api/v1/sys/screen-seeds/{alias}/import", content: null);
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        CountStored(canonical).Should().Be(1);
        CountStored(alias).Should().Be(0);
    }

    [Fact]
    public void Import_command_is_registered_as_sys_manage_write()
    {
        _ = _factory.CreateClient();
        var registry = _factory.Services.GetRequiredService<IQueryRegistry>();

        registry.TryGet("SYS.ImportSeedScreenDefinition", out var query).Should().BeTrue();
        query.Should().NotBeNull();
        query!.IsWrite.Should().BeTrue();
        query.RequiredPermission.Should().Be("sys:manage");
    }

    [Fact]
    public async Task Import_command_rolls_back_definition_when_target_insert_fails()
    {
        _ = _factory.CreateClient();
        var definition = RegisterSeed(ScreenPurpose.Manage, includeWritePath: true);
        var registry = _factory.Services.GetRequiredService<IQueryRegistry>();
        var dispatcher = _factory.Services.GetRequiredService<IRuleDispatcher>();
        registry.TryGet("SYS.ImportSeedScreenDefinition", out var query).Should().BeTrue();
        var parameters = new Dictionary<string, object>
        {
            ["uiId"] = definition.UiId,
            ["title"] = definition.Title,
            ["definitionJson"] = ScreenDefinitionJson.Serialize(definition),
            ["targetChannel"] = "MOBILE",
            ["entryPath"] = $"/POP/{definition.UiId}",
            ["currentUser"] = "seed-admin",
            ["utcNow"] = DateTime.UtcNow,
        };

        var act = () => dispatcher.ExecuteAsync(query!.Sql, parameters);

        await act.Should().ThrowAsync<Exception>();
        CountStored(definition.UiId).Should().Be(0,
            "definition and target inserts must share one transaction");
    }
}
