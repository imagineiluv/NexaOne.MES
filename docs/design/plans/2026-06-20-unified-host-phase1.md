# 통합 호스트 Phase 1 (호스트 셸) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** NexaOne.Server를 Generic Host(Exe)에서 `WebApplication`(Sdk.Web)으로 전환하되, 기존 Spring.NET 플러그인 부트스트랩(Spring/server.xml + app.xml의 9개 모듈 plugin ALC 로드)과 IHostedService 워커 발견·구동을 그대로 유지하고, ASP.NET 파이프라인(HealthChecks·Swagger·JWT)과 진단 엔드포인트(/diag)를 한 프로세스에 추가한다.

**Architecture:** 모듈/플러그인 부트스트랩을 `Server:Modules:Enabled` config 게이트(기본 ON) 뒤에 두어, 웹 셸을 플러그인 없이도 띄울 수 있게 한다(진단/자동 테스트). 컨트롤러·모듈 HTTP·Blazor·SPA는 후속 Phase. 정적 `ApplicationServer` 싱글톤 + plugin ALC가 WebApplicationFactory와 충돌하므로, 자동 테스트는 모듈 OFF(순수 웹 셸)로 `/health`·`/diag` 인증 파이프라인을 검증하고, 플러그인·9개 서비스·SQLite 스키마 경로는 문서화된 수동 기동으로 검증한다.

**Tech Stack:** C#/.NET 8, ASP.NET Core(`Microsoft.NET.Sdk.Web`), Spring.NET(NexusFramework ApplicationServer), JwtBearer, Swashbuckle, xUnit + FluentAssertions + WebApplicationFactory(스모크).

**Scope:** Phase 1만(호스트 셸). 승인 스펙 §8: docs/design/specs/2026-06-20-unified-host-design.md. 현재 브랜치 `feat/unified-host`(xml 정리 커밋 7267fc4 완료). 후속 Phase 2~5(브리지·컨트롤러·Blazor/SPA·/designer)는 별도 플랜.

---

## 파일 구조

- **수정** `src/00.Main/NexaOne.Server/NexaOne.Server.csproj` — `Microsoft.NET.Sdk` → `Microsoft.NET.Sdk.Web`, `OutputType=Exe` 제거, 웹 패키지 추가. 단일 책임: 통합 호스트 프로젝트 정의.
- **수정** `src/00.Main/NexaOne.Server/Program.cs` — Generic Host → `WebApplication`(top-level), config-게이트 Spring 부트스트랩 + 워커 + ASP.NET 파이프라인 + /diag.
- **생성** `test/NexaOne.ServerTests/NexaOne.ServerTests.csproj` — Server 호스트 스모크 테스트 프로젝트(API 참조와 분리해 `Program` 타입 모호성 회피).
- **생성** `test/NexaOne.ServerTests/ServerHostSmokeTests.cs` — 모듈 OFF 웹 셸 스모크(/health 200, /diag 401).

---

## Task 1: csproj를 Sdk.Web로 전환

**Files:**
- Modify: `src/00.Main/NexaOne.Server/NexaOne.Server.csproj`

- [ ] **Step 1: SDK·OutputType·패키지 변경**

`NexaOne.Server.csproj`의 상단을 아래로 교체(첫 `<Project>`~`</PropertyGroup>` + 첫 `<ItemGroup>` 패키지 블록):

기존:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="8.*" />
    <!-- .NET Generic Host — 비웹 background 워커(모듈 소유 + Server 실행)의 호스트(ADR-006 Phase 1).
         Spring.NET 빈 컨테이너(ApplicationServer)와 공존: 컨테이너는 서비스 빈, Generic Host는 워커 수명주기. -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
  </ItemGroup>
```
교체:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="8.*" />
    <!-- 통합 호스트(접근 A) — WebApplication. Spring.NET 빈 컨테이너(ApplicationServer)와 공존:
         컨테이너는 모듈 서비스/워커, WebApplication은 HTTP + 워커 수명주기. Hosting은 Sdk.Web 프레임워크에 포함. -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.*" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="6.*" />
  </ItemGroup>
```
나머지(ProjectReference·CopyDomainModulePlugins 타깃·Spring/*.xml·db/migrations Content)는 그대로 둔다.

- [ ] **Step 2: 빌드 확인(기존 Program.cs와 호환)**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj --nologo`
Expected: 빌드 성공, 0 오류. (기존 `Host.CreateApplicationBuilder` Program.cs는 Sdk.Web에서도 컴파일된다 — Hosting은 프레임워크 포함.)

- [ ] **Step 3: 커밋** (PowerShell, BOM-free 메시지 파일 사용: `[IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false)))` 후 `git commit -F $f`)

```
git add src/00.Main/NexaOne.Server/NexaOne.Server.csproj
git commit -m "build(server): NexaOne.Server를 Sdk.Web로 전환(통합 호스트 Phase 1, 패키지 추가)"
```

---

## Task 2: Program.cs를 WebApplication으로 전환 (Spring 게이트 + 워커 + 파이프라인 + /diag)

**Files:**
- Modify: `src/00.Main/NexaOne.Server/Program.cs`

- [ ] **Step 1: Program.cs 전체 교체**

`src/00.Main/NexaOne.Server/Program.cs` 전체를 아래로 교체:

```csharp
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Infrastructure.Persistence;
using NexusFramework;
using NexusFramework.Utils;

// 통합 호스트(접근 A, Phase 1) — WebApplication 위에서 Spring.NET 플러그인 컨텍스트 + IHostedService 워커 +
// ASP.NET 파이프라인(HealthChecks·Swagger·JWT)을 한 프로세스로 구동한다. 컨트롤러·UI는 후속 Phase.
var builder = WebApplication.CreateBuilder(args);

var server = ApplicationServer.GetInstance();
var loadedServices = new List<string>();
var workerCount = 0;

// 모듈/플러그인 부트스트랩 — 기본 ON. 웹 셸만 띄우려면(진단/테스트) Server:Modules:Enabled=false 로 끈다.
var modulesEnabled = builder.Configuration.GetValue("Server:Modules:Enabled", true);
if (modulesEnabled)
{
    // SQLite 모드면 컨텍스트 생성 전에 스키마를 부트스트랩한다(빈 DB일 때만, idempotent). server.xml의
    // eesDataSource Provider 타입으로 판별 — XML만 바꾸면 자동 적용(MSSQL이면 아무 일도 안 함).
    EnsureSqliteSchemaIfConfigured("Spring/server.xml");

    server.CreateServer(new[] { "Spring/server.xml" });
    Console.WriteLine("[NexaOne.Server] Server context initialized.");

    var workers = new List<IHostedService>();
    // 부모(server.xml) 컨텍스트의 IHostedService(예: scheduledOutboxDispatchWorker) 자동발견.
    foreach (IHostedService w in server.GetObjectsOfType(typeof(IHostedService)).Values.Cast<IHostedService>())
        workers.Add(w);

    var doc = XDomUtility.Load("Spring/app.xml");
    var root = XDomUtility.GetRoot(doc);
    var services = XDomUtility.GetElement(root, "Services");
    var splitOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
    foreach (var service in XDomUtility.GetElements(services, "Service"))
    {
        var name = service.Attribute("name")?.Value
            ?? throw new InvalidOperationException("Service element missing 'name' attribute.");
        var configFiles = (service.Attribute("configFiles")?.Value
            ?? throw new InvalidOperationException($"Service '{name}' missing 'configFiles' attribute."))
            .Split(';', splitOptions);
        var classPaths = (service.Attribute("classPaths")?.Value
            ?? throw new InvalidOperationException($"Service '{name}' missing 'classPaths' attribute."))
            .Split(';', splitOptions);

        var ctx = server.AddService(name, configFiles, classPaths);
        loadedServices.Add(name);
        Console.WriteLine($"[NexaOne.Server] Service '{name}' registered ({classPaths.Length} module(s)).");

        foreach (IHostedService w in ctx.GetObjectsOfType(typeof(IHostedService)).Values.Cast<IHostedService>())
            workers.Add(w);
    }

    // 자식 컨텍스트의 GetObjectsOfType은 상속된 부모 빈도 포함 → 인스턴스(참조) 기준 중복 제거.
    var distinctWorkers = workers.Distinct().ToList();
    workerCount = distinctWorkers.Count;
    builder.Services.AddSingleton(server);
    foreach (var w in distinctWorkers)
        builder.Services.AddSingleton<IHostedService>(w);
    Console.WriteLine($"[NexaOne.Server] {distinctWorkers.Count} background worker(s) discovered and registered.");
}
else
{
    Console.WriteLine("[NexaOne.Server] Server:Modules:Enabled=false — 웹 셸만 기동(플러그인/워커 비활성).");
}

// ===== ASP.NET 파이프라인 =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// JWT 인증 — API와 동일 규약(강한 비밀키 강제, §18.7). 토큰 발급/컨트롤러는 후속 Phase, 여기선 인증 파이프라인만 활성.
var jwtSection = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSection["SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey is required");
if (secretKey.StartsWith("CHANGE_ME", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(secretKey) < 32)
    throw new InvalidOperationException(
        "Jwt:SecretKey must be a strong secret (>= 32 bytes) supplied via environment variable or user-secrets; "
        + "the committed placeholder is rejected.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseSwagger();
if (app.Environment.IsDevelopment())
    app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

// /health — 익명(모니터링/k8s liveness). 의존성 체크 없는 기본 생존 체크.
app.MapHealthChecks("/health").AllowAnonymous();

// /diag — 통합 호스트 진단(로드된 Service·워커 수). 인증 필요(인증 파이프라인 활성 입증). 민감정보 없음.
app.MapGet("/diag", () => Results.Ok(new
{
    modulesEnabled,
    services = loadedServices,
    workerCount
})).RequireAuthorization();

// 종료 시 Spring 컨텍스트 정리.
app.Lifetime.ApplicationStopped.Register(() =>
{
    if (modulesEnabled) server.Dispose();
});

Console.WriteLine("[NexaOne.Server] Ready (web host). Press Ctrl+C to stop.");
await app.RunAsync();

// server.xml의 eesDataSource가 SQLite 공급자를 가리키면 해당 ConnectionString에 스키마를 부트스트랩한다.
// MSSQL 모드면 아무 일도 하지 않는다(운영은 마이그레이션 외부 적용). XML 파싱만으로 판별(Spring 컨텍스트와 분리).
static void EnsureSqliteSchemaIfConfigured(string serverXmlPath)
{
    XNamespace ns = "http://www.springframework.net";
    var doc = XDocument.Load(serverXmlPath);
    var objects = doc.Root?.Elements(ns + "object").ToList() ?? new List<XElement>();

    var dataSource = objects.FirstOrDefault(o => (string?)o.Attribute("id") == "eesDataSource");
    if (dataSource is null) return;

    var props = dataSource.Elements(ns + "property").ToList();
    var connStr = props.FirstOrDefault(p => (string?)p.Attribute("name") == "ConnectionString")?.Attribute("value")?.Value;
    var providerRef = props.FirstOrDefault(p => (string?)p.Attribute("name") == "Provider")?.Attribute("ref")?.Value;

    var providerType = objects
        .FirstOrDefault(o => (string?)o.Attribute("id") == providerRef)?
        .Attribute("type")?.Value ?? string.Empty;

    if (!providerType.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return;
    if (string.IsNullOrWhiteSpace(connStr))
        throw new InvalidOperationException("SQLite 공급자가 설정됐으나 eesDataSource ConnectionString이 비어 있습니다.");

    Console.WriteLine($"[NexaOne.Server] SQLite mode — ensuring schema ({connStr})...");
    SqliteSchemaInitializer.EnsureSchema(connStr);
    Console.WriteLine("[NexaOne.Server] Schema ready.");
}

// WebApplicationFactory<Program> 진입점 노출(스모크 테스트용).
public partial class Program { }
```

- [ ] **Step 2: 기존 ScheduledOutboxDispatchWorker.cs 영향 없음 확인**

`src/00.Main/NexaOne.Server/ScheduledOutboxDispatchWorker.cs`는 그대로 둔다(IHostedService 구현, server.xml이 빈으로 등록). 변경 없음.

- [ ] **Step 3: 빌드 확인**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj --nologo`
Expected: 빌드 성공, 0 오류/0 경고.

- [ ] **Step 4: 커밋**

```
git add src/00.Main/NexaOne.Server/Program.cs
git commit -m "feat(server): WebApplication 전환 — Spring 게이트 부트스트랩+워커+헬스/Swagger/JWT+/diag(통합 호스트 Phase 1)"
```

---

## Task 3: 웹 셸 스모크 테스트(모듈 OFF)

정적 `ApplicationServer` 싱글톤 + plugin ALC는 WebApplicationFactory와 충돌하므로, 자동 테스트는 `Server:Modules:Enabled=false`(순수 웹 셸)로 ASP.NET 파이프라인만 검증한다. 별도 테스트 프로젝트로 둬 NexaOne.IntegrationTests의 `WebApplicationFactory<Program>`(API의 Program)과 타입 모호성을 피한다.

**Files:**
- Create: `test/NexaOne.ServerTests/NexaOne.ServerTests.csproj`
- Create: `test/NexaOne.ServerTests/ServerHostSmokeTests.cs`

- [ ] **Step 1: 테스트 프로젝트 csproj 생성**

`test/NexaOne.ServerTests/NexaOne.ServerTests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\00.Main\NexaOne.Server\NexaOne.Server.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 솔루션에 추가**

Run: `dotnet sln add test/NexaOne.ServerTests/NexaOne.ServerTests.csproj`
Expected: "프로젝트가 추가되었습니다" (또는 영문 동등 메시지).

- [ ] **Step 3: 실패 스모크 테스트 작성**

`test/NexaOne.ServerTests/ServerHostSmokeTests.cs`:
```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>통합 호스트(Phase 1) 웹 셸 스모크 — 모듈/플러그인 OFF로 ASP.NET 파이프라인만 검증한다.
/// /health는 익명 200, /diag는 인증 요구(토큰 없으면 401)로 인증 파이프라인 활성을 입증한다.
/// 플러그인 로드·9개 서비스·SQLite 스키마는 정적 ApplicationServer 싱글톤 제약으로 수동 기동 검증한다(플랜 Task 4).</summary>
public sealed class ServerHostSmokeTests : IClassFixture<ServerHostSmokeTests.ShellFactory>
{
    private readonly ShellFactory _factory;
    public ServerHostSmokeTests(ShellFactory factory) => _factory = factory;

    public sealed class ShellFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");   // 순수 웹 셸(플러그인/워커 OFF)
            builder.UseSetting("Jwt:SecretKey", "phase1-smoke-only-jwt-secret-key-at-least-32-bytes-long");
            builder.UseSetting("Jwt:Issuer", "nexaone-test");
            builder.UseSetting("Jwt:Audience", "nexaone-test");
        }
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous_and_healthy()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Diag_requires_authentication_without_token()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/diag");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "/diag는 RequireAuthorization으로 인증 파이프라인이 활성임을 입증한다");
    }
}
```

- [ ] **Step 4: 실패 확인**

Run: `dotnet test test/NexaOne.ServerTests --nologo`
Expected: FAIL — 빌드/실행 전이라면 컴파일·실행 실패. (Program.cs Task 2 미적용 시 빌드 실패; 적용 후 이 단계 도달 시 통과해야 함.) 만약 Task 2가 이미 적용돼 바로 통과하면 그대로 진행.

- [ ] **Step 5: 통과 확인**

Run: `dotnet test test/NexaOne.ServerTests --nologo`
Expected: PASS (2 tests). 만약 `WebApplicationFactory<Program>`가 plugin ALC/정적 싱글톤과 무관하게 모듈 OFF로 부팅에 성공하지 못하면(예: 콘텐츠 루트에서 정적 자원 탐색 실패), 원인을 기록하고 BLOCKED로 보고하라 — 모듈 OFF 경로는 Spring을 전혀 건드리지 않으므로 부팅해야 한다.

- [ ] **Step 6: 커밋**

```
git add test/NexaOne.ServerTests
git commit -m "test(server): 통합 호스트 웹 셸 스모크(모듈 OFF, /health 200·/diag 401)"
```

---

## Task 4: 수동 기동 검증(플러그인·9개 서비스·SQLite) + 회귀

자동 스모크가 못 다루는 plugin ALC 로드·9개 서비스·SQLite 스키마 부트스트랩을 수동 기동으로 검증하고, 기존 스위트 회귀를 확인한다. (코드 변경 없음 — 검증·기록 단계.)

**Files:** (없음 — 검증 단계)

- [ ] **Step 1: SQLite 모드로 server.xml 토글(검증용)**

`src/00.Main/NexaOne.Server/Spring/server.xml`에서 [MSSQL] 3개 객체(dbProvider/eesDialect/eesDataSource)를 주석 처리하고 [SQLite] 블록 주석을 해제한다(파일에 이미 [SQLite] 블록이 주석으로 존재). ConnectionString은 `Data Source=nexaone_server_dev.db;Foreign Keys=False`로 둔다. **이 토글은 검증 후 되돌린다(커밋하지 않음)** — DB 전환은 운영 구성이며 Phase 1 산출물이 아니다.

- [ ] **Step 2: 모듈 ON 기동(플러그인+SQLite)**

Run (PowerShell):
```
$env:ASPNETCORE_ENVIRONMENT='Development'; $env:ASPNETCORE_URLS='http://localhost:5179'; $env:Jwt__SecretKey='unified-host-phase1-dev-secret-key-0123456789-abcd'; dotnet run --project src/00.Main/NexaOne.Server/NexaOne.Server.csproj
```
Expected 콘솔 로그: `SQLite mode — ensuring schema (...)` → `Schema ready.` → `Service 'Mdm' registered (1 module(s))` … `Service 'Sys' registered` (총 9개) → `N background worker(s) discovered and registered.` → `Ready (web host).` (plugin ALC가 ./Modules/의 9개 모듈 DLL을 로드함을 9개 Service 로그로 확인. 워커 enabled=false 기본이라 등록되되 기동 OFF.)

- [ ] **Step 3: /health 확인(다른 터미널)**

Run: `(Invoke-WebRequest http://localhost:5179/health -UseBasicParsing).StatusCode`
Expected: `200`. 이후 기동한 dotnet 프로세스를 Ctrl+C(또는 Stop-Process)로 종료한다.

- [ ] **Step 4: server.xml 토글 되돌리기**

Step 1의 server.xml 변경을 원복한다([MSSQL] 활성, [SQLite] 재주석). `git diff src/00.Main/NexaOne.Server/Spring/server.xml`가 비어 있어야 한다(검증용 임시 변경 미커밋).

- [ ] **Step 5: 전체 회귀(기존 스위트 무영향)**

Run: `dotnet test test/NexaOne.UnitTests --nologo`
Expected: 기존 단위 전부 통과(예: 1090).
Run: `dotnet test test/NexaOne.IntegrationTests --nologo`
Expected: 기존 통합 전부 통과(OPC-UA 1 skip 가능). NexaOne.API·NexaOne.Web 빌드·동작 무영향(Server 단독 변경).

- [ ] **Step 6: 검증 결과 기록(커밋 메시지)**

수동 검증 결과를 요약해 빈 커밋 또는 다음 작업 커밋 메시지에 남긴다(코드 변경 없음). 예:
```
git commit --allow-empty -m "chore(server): Phase 1 수동 검증 — SQLite 스키마 부트스트랩+9개 서비스 plugin ALC 로드+/health 200 확인"
```

---

## Self-Review (작성자 점검)

**1. 스펙 커버리지:** §8.1 csproj=Task1; §8.2 Program.cs(WebApplication·Spring 부트스트랩·워커·헬스/Swagger/JWT·/diag·Dispose)=Task2; §8.3 포트 5179·SQLite=Task4 Step2; §8.4 빌드0/0=Task1·2, /health·/diag 스모크=Task3, 9개 서비스·plugin 로드·SQLite=Task4 수동, 회귀=Task4 Step5; §8.5 하위호환(Server 단독)=Task4 Step5. **유예 명시(무자르기 아님):** 자동 WebApplicationFactory 테스트는 정적 ApplicationServer 싱글톤+plugin ALC 제약으로 모듈 OFF 웹 셸만 검증; 플러그인·9서비스·SQLite는 수동 기동(Task4)으로 검증하고 그 사실을 Task3 주석·이 절에 명시.

**2. 플레이스홀더 스캔:** TBD/TODO 없음. 모든 코드 단계 실제 코드 포함. EnsureSqliteSchemaIfConfigured는 기존 Program.cs 본문을 로컬 static 함수로 보존(동작 동일).

**3. 타입 일관성:** `Server:Modules:Enabled`(config 키)·`modulesEnabled`·`loadedServices`·`workerCount`가 Program.cs와 /diag 응답에서 일관. 스모크 테스트의 `WebApplicationFactory<Program>`는 ServerTests가 NexaOne.Server만 참조하므로 Server의 `public partial class Program`으로 해소(모호성 없음). Jwt 키 검증 규약은 API와 동일.

---

## 실행 핸드오프

이 플랜 완료 시 "웹+플러그인+워커가 한 프로세스에서 기동"하는 통합 호스트 셸을 얻는다. 후속 Phase 2(계약 추출+Spring 풀 배선+DI 브리지, MDM E2E)부터 별도 플랜으로 진행한다.
