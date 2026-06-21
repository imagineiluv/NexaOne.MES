using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>게이트웨이 우선 SYS read E2E(S7) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩).
/// SYS_ROLE / SYS_MENU / SYS_USER 를 SqliteConnection 직접 INSERT로 시드한 뒤 명명 read 쿼리
/// (ListRoles / GetRole / ListMenus / ListUsers) 라운드트립을 검증한다. + 미인증 401.
/// 보안 가드(S7): 사용자 목록 응답에 PASSWORD_HASH(및 자격증명/잠금 컬럼)가 부재함을 단언한다 —
/// 공개 게이트웨이는 비민감 컬럼만 노출하고, 자격증명은 격리 인증 경로(db/queries-auth)가 단독 소유한다.</summary>
public sealed class GatewaySysQueryTests : IClassFixture<GatewaySysQueryTests.SysFactory>
{
    private const string Secret = "s7-sys-gateway-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-sys-test";
    private readonly SysFactory _factory;
    public GatewaySysQueryTests(SysFactory factory) => _factory = factory;

    public sealed class SysFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-sys-e2e-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
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

    private void EnsureSchemaReady() => _ = _factory.CreateClient();

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "sys-e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    // SYS_ROLE 1건 시드(실재 컬럼·NOT NULL 충족; 감사 컬럼 명시). PERMISSIONS는 '|' 조인 권한 문자열.
    private void SeedRole(string roleId, string roleName, string permissions)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_ROLE
            (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @name, '설명', @perms, 0, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", roleId);
        cmd.Parameters.AddWithValue("@name", roleName);
        cmd.Parameters.AddWithValue("@perms", permissions);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
    }

    // SYS_MENU 1건 시드. VALID_STATE='Valid'만 ListMenus가 반환한다.
    private void SeedMenu(string menuId, string menuName, string validState = "Valid")
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_MENU
            (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, IMAGE_ID, OPTIONS, UI_ID, VALID_STATE)
            VALUES (@id, @name, NULL, 1, 'Screen', 'PRG', NULL, NULL, @id, @state)";
        cmd.Parameters.AddWithValue("@id", menuId);
        cmd.Parameters.AddWithValue("@name", menuName);
        cmd.Parameters.AddWithValue("@state", validState);
        cmd.ExecuteNonQuery();
    }

    // SYS_USER 1건 시드. PASSWORD_HASH는 NOT NULL이라 채우되, 게이트웨이 read가 절대 노출하지 않음을 검증한다.
    private void SeedUser(string userId, string roleId, bool isDeleted = false)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_USER
            (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE, IS_ACTIVE, IS_DELETED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, '사용자', 'SECRET_HASH_SHOULD_NEVER_LEAK', @id || '@x.com', @role, 'KoKr', 1, @del,
             'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.Parameters.AddWithValue("@role", roleId);
        cmd.Parameters.AddWithValue("@del", isDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/SYS.ListRoles",
            new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task ListRoles_returns_seeded_roles()
    {
        EnsureSchemaReady();
        var r1 = "R_" + Suffix();
        var r2 = "R_" + Suffix();
        SeedRole(r1, "역할1", "sys:manage|mdm:manage");
        SeedRole(r2, "역할2", "");

        var rows = await Query("SYS.ListRoles", new());
        var ids = rows.Select(r => r["ROLE_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { r1, r2 });
        rows.Should().OnlyContain(r => r.ContainsKey("PERMISSIONS"));
    }

    [Fact]
    public async Task GetRole_returns_single_role()
    {
        EnsureSchemaReady();
        var rid = "R_" + Suffix();
        SeedRole(rid, "단일역할", "qms:manage");

        var rows = await Query("SYS.GetRole", new() { ["roleId"] = rid });
        rows.Should().ContainSingle("roleId 필터는 1건만 반환");
        rows[0]["ROLE_NAME"].ToString().Should().Be("단일역할");
        rows[0]["PERMISSIONS"].ToString().Should().Be("qms:manage");
    }

    [Fact]
    public async Task ListMenus_returns_only_valid_menus()
    {
        EnsureSchemaReady();
        var valid = "M_" + Suffix();
        var invalid = "M_" + Suffix();
        SeedMenu(valid, "유효메뉴", "Valid");
        SeedMenu(invalid, "무효메뉴", "Invalid");

        var rows = await Query("SYS.ListMenus", new());
        var ids = rows.Select(r => r["MENU_ID"].ToString()).ToList();
        ids.Should().Contain(valid, "Valid 메뉴는 반환돼야 한다");
        ids.Should().NotContain(invalid, "Invalid 메뉴는 제외돼야 한다");
    }

    [Fact]
    public async Task ListUsers_role_filter_narrows_and_excludes_deleted()
    {
        EnsureSchemaReady();
        var role = "R_" + Suffix();
        var u1 = "U_" + Suffix();
        var deleted = "U_" + Suffix();
        SeedUser(u1, role);
        SeedUser(deleted, role, isDeleted: true);

        var rows = await Query("SYS.ListUsers", new() { ["roleId"] = role });
        var ids = rows.Select(r => r["USER_ID"].ToString()).ToList();
        ids.Should().Contain(u1);
        ids.Should().NotContain(deleted, "삭제 사용자는 제외돼야 한다");
    }

    [Fact]
    public async Task ListUsers_response_never_exposes_password_hash()
    {
        EnsureSchemaReady();
        var role = "R_" + Suffix();
        var uid = "U_" + Suffix();
        SeedUser(uid, role);

        var rows = await Query("SYS.ListUsers", new() { ["roleId"] = role });
        rows.Should().NotBeEmpty();
        var user = rows.Single(r => r["USER_ID"].ToString() == uid);

        // 보안 가드(S7): 공개 게이트웨이는 자격증명/잠금 컬럼을 절대 노출하지 않는다.
        user.Keys.Should().NotContain(k => string.Equals(k, "PASSWORD_HASH", StringComparison.OrdinalIgnoreCase),
            "공개 SYS 게이트웨이 응답에 PASSWORD_HASH가 노출되면 안 된다");
        user.Keys.Should().NotContain(k => string.Equals(k, "PASSWORD_STATE", StringComparison.OrdinalIgnoreCase));
        user.Keys.Should().NotContain(k => string.Equals(k, "FAIL_COUNT", StringComparison.OrdinalIgnoreCase));
        user.Keys.Should().NotContain(k => string.Equals(k, "LOCKED_UNTIL", StringComparison.OrdinalIgnoreCase));
        // 시드한 비밀 해시 값이 응답 어디에도 직렬화되지 않았음을 추가로 단언한다.
        user.Values.Should().NotContain(v => v != null && v.ToString()!.Contains("SECRET_HASH_SHOULD_NEVER_LEAK"));
        // 비민감 컬럼은 정상 노출.
        user.Should().ContainKey("USER_NAME");
        user.Should().ContainKey("EMAIL");
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> p)
    {
        var res = await AuthedClient().PostAsJsonAsync($"/api/v1/query/{queryId}", p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
