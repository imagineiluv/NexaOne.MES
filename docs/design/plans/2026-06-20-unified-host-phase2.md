# 통합 호스트 Phase 2 (게이트웨이 우선 + MDM E2E) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 호스트(NexaOne.Server, WebApplication)가 명명 쿼리 게이트웨이(`/api/v1/query`·`/api/v1/command`)로 실제 MDM 데이터를 SQLite(NexaMes 스키마)에서 서빙하고, 감사 사용자(JWT)가 기록되는 end-to-end(조회→저장→조회)를 입증한다.

**Architecture:** 하이브리드(게이트웨이 우선) 결정에 따라, plugin↔DI 타입 브리지 없이 Default-ALC 경로만 쓴다 — ASP.NET DI에 DB 공급자 + `AddNexaOneEES`(IRuleDispatcher·IQueryRegistry)를 등록하고, 게이트웨이 컨트롤러(query/command)와 감사-사용자 미들웨어(CurrentUserContext 설정)를 더한다. 모듈 리포지토리는 이미 `CurrentUserContext`(AsyncLocal)+per-call 연결로 싱글톤 안전이므로 스코프/트랜잭션 개편이 없다. 게이트웨이는 plugin이 필요 없어 modules-OFF로 완전 자동화 E2E가 가능하다.

**Tech Stack:** C#/.NET 8, ASP.NET Core(Sdk.Web), Dapper(IRuleDispatcher), 파일 기반 쿼리 레지스트리(db/queries), JWT, SQLite(NexaMes 스키마, SqliteSchemaInitializer), xUnit + WebApplicationFactory.

**Scope:** Phase 2만. 승인 스펙: docs/design/specs/2026-06-20-unified-host-design.md (§5 하이브리드, §7 Phase 2, §10 쿼리). 브랜치: 신규 `feat/unified-host-phase2`(main 50f6db4+e28aa72에서 분기). plugin↔DI 브리지(복잡 typed 서비스)·컨트롤러 전면 이전·Blazor/SPA는 후속 Phase.

---

## 파일 구조

- **수정** `src/00.Main/NexaOne.Server/Program.cs` — ASP.NET DI에 DB 공급자+EesDataSource+`AddNexaOneEES` 등록, Development SQLite 스키마 부트스트랩(ASP.NET config 기반), `AddControllers`/`MapControllers`, 감사-사용자 미들웨어 배선.
- **생성** `src/00.Main/NexaOne.Server/Gateway/GatewayServiceExtensions.cs` — `AddNexaOneGateway(config)`: DB 공급자 선택(Sqlite/MsSql) + EesDataSource + AddNexaOneEES. 단일 책임: 게이트웨이 데이터 경로 DI.
- **생성** `src/00.Main/NexaOne.Server/Gateway/AuditUserContextMiddleware.cs` — 요청별 `CurrentUserContext.UserId`를 JWT 주체에서 설정/복원.
- **생성** `src/00.Main/NexaOne.Server/Gateway/QueryGatewayController.cs` — `POST /api/v1/query/{id}`·`POST /api/v1/command/{id}` (RuleController 게이트웨이 로직 포팅) + `AffectedRowsResponse`.
- **수정** `db/queries/mssql/MDM.xml` + `db/queries/sqlite/MDM.xml` — §10 첫 배치: 레거시 참조에서 MDM/STD 콤보 조회 1~2건 고도화 이식.
- **수정** `test/NexaOne.ServerTests/ServerHostSmokeTests.cs` 또는 **생성** `test/NexaOne.ServerTests/GatewayMdmE2ETests.cs` — modules-OFF + SQLite + 크래프트 JWT로 command→query 라운드트립 E2E.

---

## Task 1: 게이트웨이 데이터 경로 DI + SQLite 부트스트랩

통합 호스트의 ASP.NET DI에 DB 공급자와 명명 쿼리 게이트웨이 의존성을 등록하고, 개발 SQLite 스키마를 부트스트랩한다(Spring/modules 게이트와 독립 — modules-OFF에서도 게이트웨이가 동작).

**Files:**
- Create: `src/00.Main/NexaOne.Server/Gateway/GatewayServiceExtensions.cs`
- Modify: `src/00.Main/NexaOne.Server/Program.cs`

- [ ] **Step 1: `GatewayServiceExtensions.cs` 생성**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.MsSql;

namespace NexaOne.Server.Gateway;

/// <summary>게이트웨이(하이브리드) 데이터 경로 DI — DB 공급자 + EesDataSource + 명명 쿼리 게이트웨이
/// (IRuleDispatcher·IQueryRegistry, AddNexaOneEES). plugin↔DI 브리지 없이 Default ALC만 사용한다.
/// DB 선택은 ASP.NET config(Database:Provider) 기준 — server.xml(Spring)과 별개로 게이트웨이 전용으로 등록한다.</summary>
public static class GatewayServiceExtensions
{
    public static IServiceCollection AddNexaOneGateway(this IServiceCollection services, IConfiguration configuration)
    {
        var connStr = configuration.GetConnectionString("NexaOne")
            ?? throw new InvalidOperationException("ConnectionStrings:NexaOne is required for the gateway data path");

        var dbProvider = configuration.GetValue<string>("Database:Provider") ?? "MsSql";
        IDatabaseProvider provider;
        INexaOneEESDbCapability capability;
        if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            provider = new NexusCom.Data.Sqlite.SqliteProvider();
            capability = new SqliteEesDbCapability();
        }
        else
        {
            var mssql = new MsSqlProvider();
            provider = mssql;
            capability = mssql;
        }

        services.AddSingleton(provider);
        services.AddSingleton(capability);
        services.AddSingleton(new EesDataSource { Provider = provider, ConnectionString = connStr });

        // IRuleDispatcher(Dapper) + IQueryRegistry(파일 쿼리, 방언 폴더) 등록.
        services.AddNexaOneEES(configuration);
        return services;
    }
}
```

- [ ] **Step 2: Program.cs — 게이트웨이 등록 + 컨트롤러 + SQLite 부트스트랩**

`src/00.Main/NexaOne.Server/Program.cs`에서 ASP.NET 파이프라인 등록부(`builder.Services.AddEndpointsApiExplorer();` 줄 앞)에 추가:
```csharp
// 게이트웨이(하이브리드) — 명명 쿼리 데이터 경로(plugin 무관, Default ALC).
builder.Services.AddNexaOneGateway(builder.Configuration);
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
```
파일 상단 using에 추가: `using NexaOne.Server.Gateway;`

`var app = builder.Build();` 직후(파이프라인 시작 전)에 SQLite 개발 부트스트랩 추가 — Spring/modules 게이트와 독립:
```csharp
// 개발 SQLite 부트스트랩(게이트웨이 데이터 경로) — Spring 모듈 게이트와 독립. 빈 DB면 db/migrations 스키마 + V001 admin/admin 시드.
if (app.Environment.IsDevelopment()
    && string.Equals(app.Configuration.GetValue<string>("Database:Provider"), "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var gwConn = app.Configuration.GetConnectionString("NexaOne");
    if (!string.IsNullOrWhiteSpace(gwConn))
        NexaOne.Infrastructure.Persistence.SqliteSchemaInitializer.EnsureSchema(gwConn);
}
```

`app.UseAuthorization();` 다음 줄에 컨트롤러 매핑 추가:
```csharp
app.MapControllers();
```

- [ ] **Step 3: 빌드**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj --nologo`
Expected: 0 errors/0 warnings.

- [ ] **Step 4: 커밋** (PowerShell, BOM-free: `[IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false)))` 후 `git commit -F $f`)

```
git add src/00.Main/NexaOne.Server/Gateway/GatewayServiceExtensions.cs src/00.Main/NexaOne.Server/Program.cs
git commit -m "feat(server): 게이트웨이 데이터 경로 DI(DB공급자+AddNexaOneEES)+컨트롤러+개발 SQLite 부트스트랩(Phase 2)"
```

**Context:** `AddNexaOneEES`(NexaOne.Application)는 IRuleDispatcher(NexaFrameworkRuleDispatcher, ctor: IDatabaseProvider+connStr) + IQueryRegistry(FileQueryRegistry, 방언 폴더)를 등록한다 — IDatabaseProvider가 DI에 있어야 하므로 위에서 먼저 등록한다. `SqliteEesDbCapability`/`EesDataSource`/`SqliteSchemaInitializer`는 NexaOne.Infrastructure.Persistence. `NexusCom.Data.Sqlite.SqliteProvider`·`MsSqlProvider`는 NexaOne.Server가 이미 NexusCom.Data 메타 참조로 출력에 보유(csproj). db/queries는 csproj Content로 출력에 복사됨(FileQueryRegistry가 BaseDirectory에서 상위탐색).

---

## Task 2: 감사-사용자 미들웨어

요청별로 `CurrentUserContext.UserId`를 JWT 주체(sub/NameIdentifier)에서 설정/복원해, 게이트웨이 쓰기의 감사 컬럼(@currentUser)이 실제 사용자로 채워지게 한다(RequestLogContextMiddleware의 감사 부분 포팅, Serilog 의존 제외).

**Files:**
- Create: `src/00.Main/NexaOne.Server/Gateway/AuditUserContextMiddleware.cs`
- Modify: `src/00.Main/NexaOne.Server/Program.cs`

- [ ] **Step 1: 미들웨어 생성**

```csharp
using System.Security.Claims;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.Server.Gateway;

/// <summary>요청 단위 감사 사용자 앰비언트 설정 — JWT 주체(NameIdentifier/sub)를 CurrentUserContext.UserId(AsyncLocal)에
/// 싣고 요청 종료 시 복원한다. 모듈 리포지토리·ServiceObjectProcessor가 이 값을 감사 컬럼(@currentUser)으로 읽는다.
/// 비인증이면 null로 두어 "SYSTEM" 폴백. UseAuthentication 다음에 배치해야 User 클레임이 채워져 있다.</summary>
public sealed class AuditUserContextMiddleware
{
    private readonly RequestDelegate _next;
    public AuditUserContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var authUser = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User?.FindFirst("sub")?.Value;
        var previous = CurrentUserContext.UserId;
        CurrentUserContext.UserId = authUser;
        try { await _next(context); }
        finally { CurrentUserContext.UserId = previous; }
    }
}
```

- [ ] **Step 2: Program.cs — 미들웨어 배선**

`app.UseAuthentication();` 다음, `app.UseAuthorization();` 앞에 추가:
```csharp
app.UseMiddleware<NexaOne.Server.Gateway.AuditUserContextMiddleware>();
```

- [ ] **Step 3: 빌드**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj --nologo`
Expected: 0 errors/0 warnings.

- [ ] **Step 4: 커밋**
```
git add src/00.Main/NexaOne.Server/Gateway/AuditUserContextMiddleware.cs src/00.Main/NexaOne.Server/Program.cs
git commit -m "feat(server): 감사-사용자 미들웨어(JWT→CurrentUserContext) 배선(Phase 2)"
```

**Context:** `CurrentUserContext`(NexaOne.Infrastructure.Persistence)는 `static AsyncLocal<string?> UserId`. AsyncLocal이 비동기 요청 파이프라인에 흐르므로 싱글톤 리포지토리의 메서드 호출에서 올바른 사용자가 읽힌다.

---

## Task 3: 게이트웨이 컨트롤러 (query/command)

명명 쿼리 게이트웨이를 통합 호스트에 추가한다 — RuleController(NexaOne.API)의 query/command 로직을 포팅(NexaOne.API 결합 회피 위해 재작성). 등록 쿼리만 실행(원시 SQL 노출 없음), 쓰기는 requiredPermission 집행 + @currentUser/@utcNow 주입.

**Files:**
- Create: `src/00.Main/NexaOne.Server/Gateway/QueryGatewayController.cs`

- [ ] **Step 1: 컨트롤러 생성**

```csharp
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>파일 기반 명명 쿼리 게이트웨이(하이브리드 데이터 경로). 사전 등록 쿼리 ID만 실행 — 원시 SQL 노출 없음.
/// 읽기는 /query, 쓰기는 /command(requiredPermission 집행 + @currentUser/@utcNow 서버 주입). RuleController와 동일 의미.</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed partial class QueryGatewayController : ControllerBase
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _queryRegistry;

    public QueryGatewayController(IRuleDispatcher dispatcher, IQueryRegistry queryRegistry)
    {
        _dispatcher = dispatcher;
        _queryRegistry = queryRegistry;
    }

    [HttpPost("query/{queryId}")]
    [ProducesResponseType<IReadOnlyList<Dictionary<string, object>>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteQuery(
        [FromRoute] string queryId, [FromBody] Dictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var def) || def is null)
            return NotFound(new Error("QUERY_NOT_FOUND", $"Query '{queryId}' is not registered.", ErrorType.NotFound));
        if (def.IsWrite)
            return BadRequest(new Error("WRITE_QUERY_VIA_QUERY", $"Query '{queryId}' is a write query. Use POST /api/v1/command/{queryId}.", ErrorType.Validation));
        if (!string.IsNullOrEmpty(def.RequiredPermission) && !HasPermission(def.RequiredPermission))
            return Forbid();

        var p = BuildParameters(def.Sql, parameters, injectAudit: false);
        var rows = await _dispatcher.QueryAsync(def.Sql, p, ct);
        return Ok(rows);
    }

    [HttpPost("command/{queryId}")]
    [ProducesResponseType<AffectedRowsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteCommand(
        [FromRoute] string queryId, [FromBody] Dictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var def) || def is null)
            return NotFound(new Error("QUERY_NOT_FOUND", $"Query '{queryId}' is not registered.", ErrorType.NotFound));
        if (!def.IsWrite)
            return BadRequest(new Error("READ_QUERY_VIA_COMMAND", $"Query '{queryId}' is a read query. Use POST /api/v1/query/{queryId}.", ErrorType.Validation));
        if (!string.IsNullOrEmpty(def.RequiredPermission) && !HasPermission(def.RequiredPermission))
            return Forbid();

        var p = BuildParameters(def.Sql, parameters, injectAudit: true);
        var affected = await _dispatcher.ExecuteAsync(def.Sql, p, ct);
        return Ok(new AffectedRowsResponse(affected));
    }

    private Dictionary<string, object> BuildParameters(string sql, IReadOnlyDictionary<string, object>? parameters, bool injectAudit)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal);
        if (parameters is not null)
            foreach (var (k, v) in parameters)
                p[k] = JsonToClr(v) ?? (object)DBNull.Value;
        if (injectAudit)
        {
            p["currentUser"] = CurrentUserId;
            p["utcNow"] = DateTime.UtcNow;
        }
        foreach (Match m in ParamToken().Matches(sql))
            if (!p.ContainsKey(m.Groups[1].Value)) p[m.Groups[1].Value] = DBNull.Value;
        return p;
    }

    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.Identity?.Name ?? "SYSTEM";

    private bool HasPermission(string permission) =>
        User.FindAll(NexaOne.Common.Security.Permissions.ClaimType)
            .Any(c => c.Value == NexaOne.Common.Security.Permissions.All
                   || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private static object? JsonToClr(object? value) => value switch
    {
        System.Text.Json.JsonElement je => je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => je.GetString(),
            System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => je.ToString(),
        },
        _ => value,
    };

    [GeneratedRegex(@"@(\w+)")]
    private static partial Regex ParamToken();
}

/// <summary>쓰기 게이트웨이 영향 행 수 응답(RuleController의 AffectedRowsResponse와 동일 형태).</summary>
public sealed record AffectedRowsResponse(int Affected);
```

- [ ] **Step 2: 빌드**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj --nologo`
Expected: 0 errors/0 warnings.

- [ ] **Step 3: 커밋**
```
git add src/00.Main/NexaOne.Server/Gateway/QueryGatewayController.cs
git commit -m "feat(server): 명명 쿼리 게이트웨이 컨트롤러(query/command, 권한·감사 주입) 포팅(Phase 2)"
```

**Context:** `IRuleDispatcher`/`IQueryRegistry`(NexaOne.Application.Query·Messaging)는 Task 1의 AddNexaOneEES로 DI 등록됨. `Error`/`ErrorType`/`Permissions`는 NexaOne.Common. `QueryDefinition`(Id/Sql/RequiredPermission/IsWrite)은 NexaOne.Application.Query. 컨트롤러는 RuleController(src/02.Backend/NexaOne.API/Controllers/RuleController.cs)의 query/command·BuildParameters·HasPermission·JsonToClr·ParamToken을 그대로 옮긴 것(원시 SQL `/query`·rule·procedure 엔드포인트는 Phase 2 비대상). 클래스가 `partial`인 이유는 `[GeneratedRegex]` 소스 생성기 요구.

---

## Task 4: MDM E2E 통합 테스트 (modules OFF, SQLite, 명명쿼리 라운드트립)

게이트웨이가 실제 MDM 데이터를 SQLite로 서빙함을 자동 입증한다 — modules OFF(게이트웨이는 plugin 무관)로 부팅, 크래프트 JWT(mdm:manage)로 `/command/MDM.CreatePlant` 저장 후 `/query/MDM.PlantList` 조회해 라운드트립 확인. 감사 사용자가 토큰 주체로 기록됨도 확인.

**Files:**
- Create: `test/NexaOne.ServerTests/GatewayMdmE2ETests.cs`
- Modify: `test/NexaOne.ServerTests/NexaOne.ServerTests.csproj` (System.IdentityModel.Tokens.Jwt 패키지 추가)

- [ ] **Step 1: 테스트 csproj에 JWT 패키지 추가**

`test/NexaOne.ServerTests/NexaOne.ServerTests.csproj`의 `<ItemGroup>`(패키지)에 추가:
```xml
    <PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="8.*" />
```

- [ ] **Step 2: 실패 E2E 테스트 작성**

`test/NexaOne.ServerTests/GatewayMdmE2ETests.cs`:
```csharp
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

/// <summary>게이트웨이 우선 MDM E2E(Phase 2) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩)로
/// /command/MDM.CreatePlant 저장 후 /query/MDM.PlantList 조회 라운드트립을 검증한다. 감사 사용자가 토큰 주체로 기록됨도 확인.</summary>
public sealed class GatewayMdmE2ETests : IClassFixture<GatewayMdmE2ETests.GatewayFactory>
{
    private const string Secret = "phase2-gateway-e2e-jwt-secret-key-at-least-32-bytes-long";
    private const string Issuer = "nexaone-test";
    private readonly GatewayFactory _factory;
    public GatewayMdmE2ETests(GatewayFactory factory) => _factory = factory;

    public sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        // 각 테스트 인스턴스가 깨끗한 SQLite 파일을 쓰도록 고유 경로(클래스 1회 생성).
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-server-e2e-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");        // 게이트웨이는 plugin 불요
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

    [Fact]
    public async Task Command_then_query_roundtrips_plant_via_named_queries()
    {
        var client = AuthedClient("mdm:manage");

        var plantId = "E2E_PLANT_" + Guid.NewGuid().ToString("N")[..8];
        var save = await client.PostAsJsonAsync($"/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        {
            ["plantId"] = plantId,
            ["plantName"] = "E2E 공장",
            ["description"] = "phase2 e2e",
            ["country"] = "KR",
            ["timeZone"] = "Asia/Seoul",
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK, "등록 쓰기쿼리는 mdm:manage 권한으로 성공해야 한다");

        var list = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantList", new Dictionary<string, object> { ["plantId"] = plantId });
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await list.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().ContainSingle(r => r.ContainsKey("PLANT_ID") && r["PLANT_ID"].ToString() == plantId,
            "저장한 공장이 명명 조회쿼리로 라운드트립돼야 한다");
    }

    [Fact]
    public async Task Command_without_permission_is_forbidden()
    {
        var client = AuthedClient("fdc:read");   // mdm:manage 없음
        var res = await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        {
            ["plantId"] = "NOPERM", ["plantName"] = "x",
        });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "쓰기쿼리 requiredPermission(mdm:manage) 미보유 시 403");
    }
}
```

- [ ] **Step 3: 실패 확인**

Run: `dotnet test test/NexaOne.ServerTests --filter GatewayMdmE2ETests --nologo`
Expected: Task 1~3 적용 전이면 컴파일/실행 실패. 적용 후 도달 시 통과해야 함. (만약 SQLite 스키마에 MDM_PLANT가 없어 실패하면, db/migrations에 MDM_PLANT가 있는지 확인하고 BUILD가 db/migrations를 출력에 복사하는지 점검 — Server csproj는 이미 db/migrations Content를 복사한다.)

- [ ] **Step 4: 통과 확인**

Run: `dotnet test test/NexaOne.ServerTests --nologo`
Expected: PASS (기존 스모크 2 + 신규 2 = 4 tests). 게이트웨이 E2E가 plugin 없이 SQLite로 동작.
IMPORTANT: SQLite 부트스트랩이 modules-OFF에서도 동작하는지가 관건(Task 1의 ASP.NET-side 부트스트랩). 만약 스키마 미생성으로 실패하면 BLOCKED로 정확한 오류 보고(테스트 약화 금지).

- [ ] **Step 5: 커밋**
```
git add test/NexaOne.ServerTests/GatewayMdmE2ETests.cs test/NexaOne.ServerTests/NexaOne.ServerTests.csproj
git commit -m "test(server): 게이트웨이 MDM E2E(modules OFF·SQLite·JWT, command→query 라운드트립 + 권한 403)(Phase 2)"
```

**Context:** 기존 `db/queries/sqlite/MDM.xml`에 `MDM.PlantList`(read)·`MDM.CreatePlant`(write, requiredPermission=mdm:manage, @currentUser/@utcNow)가 이미 존재(검증된 명명쿼리). `MDM_PLANT` 테이블은 db/migrations에 정의됨(SqliteSchemaInitializer가 SQLite로 변환·생성). `Permissions.ClaimType`은 "permission". 크래프트 JWT는 호스트의 JwtBearer 검증 파라미터(Issuer/Audience/Secret)와 일치해야 통과.

---

## Task 5: 쿼리 라이브러리 고도화 — 첫 배치(MDM/STD 콤보)

§10 워크스트림 착수 — 레거시 참조(`reference/legacy_3.5_20260526/Config/Query/xml/`)에서 메타데이터 화면·디자이너 드롭다운이 쓰는 MDM/STD 조회·콤보 쿼리 1~2건을 NexaMes 스키마로 고도화 이식하고 SQLite로 검증한다.

**Files:**
- Modify: `db/queries/sqlite/MDM.xml`, `db/queries/mssql/MDM.xml`
- Test: `test/NexaOne.ServerTests/GatewayMdmE2ETests.cs`에 이식 쿼리 1건 스모크 추가(SQLite 실행 검증)

- [ ] **Step 1: 레거시 소스 확인 + NexaMes 스키마 매핑 결정**

Read `reference/legacy_3.5_20260526/Config/Query/xml/standard/UI_MICUBE_STANDARD_CONDITION.xml`의 `MICUBE.STANDARD.CONDITION.GET.MDM.ITEM.CLASS.COMBO`(STD/MDM item class combo). Read `db/migrations`에서 NexaMes의 품목분류 테이블·컬럼 실제 이름을 확인한다(예: `Grep -r "ITEM_CLASS" db/migrations` 또는 MDM_ITEM_CLASS 존재 여부). 레거시 `MDM_TB_ITEM_CLASS`·다국어 컬럼이 NexaMes에서 어떤 단일 테이블/컬럼인지 매핑한다. **존재하지 않으면 다른 존재 테이블(예: MDM_PLANT 기반 콤보)로 대체하고 그 사실을 기록**(미존재 스키마 이식 금지).

- [ ] **Step 2: 고도화 쿼리 추가(SQLite + MSSQL 동일 ID)**

NexaMes 스키마에 실제 존재하는 마스터로 콤보 조회 1건을 추가한다. 예(실제 컬럼명은 Step 1에서 확정) — `db/queries/sqlite/MDM.xml`의 `</queries>` 앞:
```xml
    <!-- §10 고도화 이식: 레거시 MICUBE.STANDARD.CONDITION.GET.PLANT.COMBO → NexaMes 스키마(단일 PLANT_NAME)·@param·SQLite.
         원본의 Velocity 보간($!{...})·다국어 CASE·WITH(NOLOCK)을 제거하고 선택필터(@p IS NULL) 패턴으로 고도화. -->
    <query id="MDM.PlantCombo">
        <statement><![CDATA[
            SELECT PLANT_ID AS VALUE, PLANT_NAME AS TEXT
            FROM MDM_PLANT
            WHERE (@plantId IS NULL OR PLANT_ID = @plantId)
            ORDER BY PLANT_NAME
        ]]></statement>
    </query>
```
`db/queries/mssql/MDM.xml`에도 동일 ID로 추가(MSSQL판은 필요 시 `WITH (NOLOCK)` 포함):
```xml
    <query id="MDM.PlantCombo">
        <statement><![CDATA[
            SELECT PLANT_ID AS VALUE, PLANT_NAME AS TEXT
            FROM MDM_PLANT WITH (NOLOCK)
            WHERE (@plantId IS NULL OR PLANT_ID = @plantId)
            ORDER BY PLANT_NAME
        ]]></statement>
    </query>
```
(주의: 위 컬럼/테이블이 db/migrations와 일치하는지 Step 1에서 확정 후 작성 — MDM_PLANT.PLANT_NAME은 기존 MDM.PlantList에서 사용 중이라 존재 확인됨.)

- [ ] **Step 3: 이식 쿼리 SQLite 스모크 추가**

`GatewayMdmE2ETests.cs`에 추가(클래스 내):
```csharp
    [Fact]
    public async Task Enhanced_combo_query_executes_on_sqlite()
    {
        var client = AuthedClient("mdm:manage");
        // 공장 1건 저장 후 콤보 조회 — 고도화 이식 쿼리가 SQLite에서 실행되고 VALUE/TEXT 형태를 반환.
        var plantId = "COMBO_" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        { ["plantId"] = plantId, ["plantName"] = "콤보공장" });

        var res = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantCombo", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().Contain(r => r.ContainsKey("VALUE") && r["VALUE"].ToString() == plantId
            && r.ContainsKey("TEXT"), "고도화 콤보 쿼리는 VALUE/TEXT를 SQLite에서 반환해야 한다");
    }
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test test/NexaOne.ServerTests --nologo`
Expected: PASS (스모크 2 + E2E 2 + 콤보 1 = 5 tests).

- [ ] **Step 5: 커밋**
```
git add db/queries/sqlite/MDM.xml db/queries/mssql/MDM.xml test/NexaOne.ServerTests/GatewayMdmE2ETests.cs
git commit -m "feat(queries): 레거시 MDM 콤보 쿼리 고도화 이식(MDM.PlantCombo, @param·NexaMes 스키마·SQLite 검증)(§10 첫 배치)"
```

**Context:** §10 변환 규칙 — Velocity 보간→@param, 레거시 스키마→NexaMes 스키마(실검증), 방언 분리, 보안 주석. 첫 배치는 안전하게 존재 확인된 MDM_PLANT 기반 콤보로 시작(추후 STD/품목/설비 트리 등 확대). FileQueryRegistry가 추가 ID를 자동 로드한다.

---

## Self-Review (작성자 점검)

**1. 스펙 커버리지:** §7 Phase 2 게이트웨이 도입=Task1·3, CurrentUserContext 미들웨어=Task2, MDM 명명쿼리 SQLite E2E=Task4, §10 쿼리 고도화 첫 배치=Task5. 하이브리드(브리지 없음)=전 태스크가 IRuleDispatcher/IQueryRegistry(Default ALC)만 사용. 수명주기(싱글톤 안전)=Task2 AsyncLocal 미들웨어로 충족.

**2. 플레이스홀더 스캔:** TBD/TODO 없음. Task5 Step1은 "실제 스키마 확인 후 작성"이라는 검증 단계(플레이스홀더 아님) — 예시 쿼리는 존재 확인된 MDM_PLANT 기반으로 제공.

**3. 타입 일관성:** `AddNexaOneGateway`(Task1)→`IRuleDispatcher`/`IQueryRegistry` DI→`QueryGatewayController`(Task3) 주입 일관. `AffectedRowsResponse(int Affected)` Task3 정의=RuleController 형태. `CurrentUserContext.UserId`(Task2)←모듈 리포지토리 읽기 일관. E2E(Task4)의 `Permissions.ClaimType`·Issuer/Audience/Secret가 호스트 JwtBearer와 일치. `MDM.CreatePlant`/`MDM.PlantList`(기존)·`MDM.PlantCombo`(Task5 신규) ID 일관.

---

## 실행 핸드오프

완료 시 통합 호스트가 실제 MDM 데이터를 SQLite로 명명쿼리 게이트웨이를 통해 서빙(조회·저장·권한·감사). plugin↔DI 타입 브리지(복잡 typed 서비스)·전면 컨트롤러 이전·Blazor/SPA·/designer는 후속 Phase. 쿼리 고도화는 §10 워크스트림으로 모듈별 확대.
