# 통합 호스트 modules-ON 부팅 자동검증 (Phase 6) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 빌드된 통합 호스트(NexaOne.Server)를 자식 프로세스로 SQLite·modules-ON 기동해, 9개 도메인 모듈 + 백그라운드 워커 + EST/RMS `GetBean→캐스트` 브리지가 한 프로세스에서 실제로 올라오는지 **자동화 테스트로 고정**한다(현재 modules-ON은 Phase 1 수동 1회 기동만 검증됨).

**Architecture:** 정적 `ApplicationServer.GetInstance()` 싱글톤 제약으로 in-proc `WebApplicationFactory`는 modules-ON을 modules-OFF 테스트와 한 프로세스에서 돌릴 수 없다. 따라서 **자식 프로세스 black-box 스모크**로 접근한다 — 빌드된 호스트 DLL을 별 프로세스로 띄우고 Kestrel stdout의 리슨 포트를 파싱해 `/health`·`/diag`(JWT)를 HTTP로 검증한다. 운영 기본(server.xml=MSSQL)을 깨지 않도록 **Spring 설정 경로를 config로 파라미터화**(`Server:SpringConfig`, 기본 `Spring/server.xml`)하고 SQLite 변형(`Spring/server.sqlite.xml`)을 커밋해 테스트가 그것을 가리킨다.

**Tech Stack:** C#/.NET 8, xUnit, `System.Diagnostics.Process`, `System.Text.Json`, JWT(System.IdentityModel.Tokens.Jwt). 백엔드 무변경 외 Program.cs 소폭(설정 경로 1개) + 신규 SQLite Spring 변형 + 신규 테스트 1파일.

---

## 검증된 사실 (이 계획 작성 전 직접 확인, 2026-06-21)

구현자는 다음 사실에 의존해도 된다 — 컨트롤러가 실측했다.

- **modules-ON SQLite 부팅은 실제로 성공한다.** bin 출력의 `Spring/server.xml`을 SQLite 데이터소스로 바꿔 호스트를 자식 프로세스로 기동한 결과: `[NexaOne.Server] Service '<X>' registered`가 **9개**(Mdm·Est·Fdc·Rms·Qms·Cmms·Pom·Shp·Sys), `5 background worker(s) discovered and registered`, **EST/RMS `GetBean→캐스트` 브리지 예외 없음**(stderr 공백), `Now listening on: http://127.0.0.1:5191`, `GET /health → 200`, 기동 ~2초. 즉 ADR-006 plugin ALC 로드 + ADR-008 브리지 fail-fast 경로가 정상 동작한다.
- **운영 기본 server.xml은 MSSQL이다.** [Spring/server.xml:94-100](../../../src/00.Main/NexaOne.Server/Spring/server.xml#L94-L100)가 MSSQL 활성, SQLite 블록(102-111)은 주석. 자동 SQLite 부팅엔 SQLite 데이터소스 변형이 필요하다.
- **설정은 환경변수로 넘겨야 안전하다.** `--ConnectionStrings:NexaOne=Data Source=x.db`처럼 공백 포함 값을 명령행 인자로 주면 깨진다(부팅 시 `SqliteSchemaInitializer`가 "Format of the initialization string ... index 0"). 환경변수(`ConnectionStrings__NexaOne` 등, `__`=섹션 구분)는 공백을 정확히 전달한다.
- **부팅 경로**(읽기 확인): [Program.cs:32](../../../src/00.Main/NexaOne.Server/Program.cs#L32) `modulesEnabled` 게이트 → :37 `EnsureSqliteSchemaIfConfigured("Spring/server.xml")` → :39 `server.CreateServer(new[]{"Spring/server.xml"})` → :51-68 9개 서비스 로드 → :71-76 워커 발견 → :78-91 EST/RMS `GetBean→캐스트→AddSingleton`(캐스트 실패 시 throw=부팅 폭발). 두 곳(`EnsureSqliteSchemaIfConfigured`, `CreateServer`)이 `"Spring/server.xml"`을 **하드코딩**한다 — 이 두 곳만 config로 바꾼다.
- **`/diag`**(읽기 확인): [Program.cs:217-223](../../../src/00.Main/NexaOne.Server/Program.cs#L217-L223) `app.MapGet("/diag", () => Results.Ok(new { modulesEnabled, services = loadedServices, workerCount })).RequireAuthorization()`. 인증만 요구(특정 권한 불요). `services`=로드된 서비스명 리스트, `workerCount`=발견 워커 수.
- **빌드 산출물은 자족적이다.** [NexaOne.Server.csproj:83-96](../../../src/00.Main/NexaOne.Server/NexaOne.Server.csproj#L83-L96) `CopyDomainModulePlugins`(AfterTargets=Build)가 9개 모듈 DLL을 `$(OutDir)Modules`로 복사, Spring xml·db/migrations·db/queries도 Content로 복사. ServerTests가 NexaOne.Server를 참조하므로 ServerTests 빌드 시 호스트 bin(`bin/Debug/net8.0/{NexaOne.Server.dll, Modules/*.dll, Spring/*.xml, db/migrations/*.sql}`)이 갖춰진다(실측: 9 DLL·11 xml·34 sql 존재).
- **기존 ServerTests는 전부 modules-OFF**다([ServerHostSmokeTests.cs:22](../../../test/NexaOne.ServerTests/ServerHostSmokeTests.cs#L22), [EstBridgeControllerTests.cs:33,40](../../../test/NexaOne.ServerTests/EstBridgeControllerTests.cs#L33-L40)는 `FakeBridge` 주입). 실제 plugin/ALC/브리지 부팅은 어떤 자동 테스트도 안 탄다 — 이 계획이 그 공백을 메운다. 현재 ServerTests 44 passed(실측).

## File Structure

- 수정: `src/00.Main/NexaOne.Server/Program.cs` — Spring 설정 경로를 `Server:SpringConfig`(기본 `Spring/server.xml`)로 읽어 두 하드코딩 지점에 적용.
- 생성: `src/00.Main/NexaOne.Server/Spring/server.sqlite.xml` — server.xml과 동일하되 데이터소스만 SQLite 활성(테스트·로컬용). 운영 기본(server.xml)은 불변.
- 수정: `src/00.Main/NexaOne.Server/NexaOne.Server.csproj` — `server.sqlite.xml`을 출력으로 Content 복사.
- 생성: `test/NexaOne.ServerTests/HostModulesBootSmokeTests.cs` — 자식 프로세스 부팅 스모크(긍정: 9서비스·워커·/health·/diag, 음성: 부팅 실패 검출).

---

## Task 1: Spring 설정 경로 파라미터화 + SQLite 변형(운영 기본 불변)

**Files:**
- Modify: `src/00.Main/NexaOne.Server/Program.cs:32-39`
- Create: `src/00.Main/NexaOne.Server/Spring/server.sqlite.xml`
- Modify: `src/00.Main/NexaOne.Server/NexaOne.Server.csproj` (Spring Content 블록에 1줄 추가)

- [ ] **Step 1: Program.cs — Spring 설정 경로를 config로 파라미터화**

[Program.cs](../../../src/00.Main/NexaOne.Server/Program.cs)의 `if (modulesEnabled)` 진입부에서 설정 경로를 한 번 읽어 두 하드코딩 지점에 쓴다. 현재:
```csharp
var modulesEnabled = builder.Configuration.GetValue("Server:Modules:Enabled", true);
if (modulesEnabled)
{
    // SQLite 모드면 ...
    EnsureSqliteSchemaIfConfigured("Spring/server.xml");

    var serverCtx = server.CreateServer(new[] { "Spring/server.xml" });
```
를 다음으로(2곳의 `"Spring/server.xml"` 리터럴을 변수로 치환, 그 외 무변경):
```csharp
var modulesEnabled = builder.Configuration.GetValue("Server:Modules:Enabled", true);
if (modulesEnabled)
{
    // Spring 부모 컨텍스트 설정 경로 — 기본은 운영 server.xml(MSSQL). 테스트/로컬은 Server:SpringConfig로
    // SQLite 변형(Spring/server.sqlite.xml)을 가리켜 외부 DB 없이 modules-ON 부팅을 검증한다(데이터소스만 다른 동일 빈 집합).
    var springConfig = builder.Configuration.GetValue("Server:SpringConfig", "Spring/server.xml")!;

    // SQLite 모드면 컨텍스트 생성 전에 스키마를 부트스트랩한다(빈 DB일 때만, idempotent). server.xml의
    // eesDataSource Provider 타입으로 판별 — XML만 바꾸면 자동 적용(MSSQL이면 아무 일도 안 함).
    EnsureSqliteSchemaIfConfigured(springConfig);

    var serverCtx = server.CreateServer(new[] { springConfig });
```
주의: `EnsureSqliteSchemaIfConfigured`와 `CreateServer` 호출의 인자만 바뀐다. 나머지 부팅 로직(app.xml 로드, 워커 발견, EST/RMS 브리지)은 절대 손대지 마라.

- [ ] **Step 2: server.sqlite.xml 생성 (server.xml의 SQLite 변형)**

`src/00.Main/NexaOne.Server/Spring/server.sqlite.xml`을 만든다 — [Spring/server.xml](../../../src/00.Main/NexaOne.Server/Spring/server.xml)을 그대로 복제하되 Database 섹션만 SQLite 활성으로 바꾼다. 즉 WorkflowManager/공통 빈/Outbox/messageBus 블록(1-82행)은 **server.xml과 동일**하게 두고, Database 섹션(84행 이하)만 아래로:

```xml
  <!-- ===== Database (SQLite 변형 — 테스트/로컬 전용) =====
       운영 기본은 server.xml(MSSQL). 이 파일은 Server:SpringConfig=Spring/server.sqlite.xml 로 명시 지정될 때만 사용된다.
       동일 빈 id(dbProvider/eesDialect/eesDataSource)라 nexaone.xml 참조는 그대로 유지된다. NexaOne.Server 기동 시
       db/migrations를 SQLite 방언으로 변환해 스키마를 자동 생성한다(SqliteSchemaInitializer, 빈 DB일 때만). -->
  <object id="dbProvider" type="NexusCom.Data.Sqlite.SqliteProvider, NexusCom.Data.Sqlite" />
  <object id="eesDialect" type="NexaOne.Infrastructure.Persistence.SqliteEesDbCapability, NexaOne.Infrastructure" />
  <object id="eesDataSource" type="NexaOne.Infrastructure.Persistence.EesDataSource, NexaOne.Infrastructure">
    <property name="Provider" ref="dbProvider" />
    <property name="ConnectionString" value="Data Source=nexaone-modules-test.db;Foreign Keys=False" />
  </object>

</objects>
```

구현자는 server.xml을 Read해 1-82행(`<!-- ===== Database` 직전까지)을 그대로 복사하고, Database 섹션부터 `</objects>`까지를 위 SQLite 블록으로 대체해 파일을 완성한다. (WorkflowManager·opcUaDriver·plantController·cacheService·quartzScheduler·outboxRepository·scheduledOutboxDispatchWorker·messageBus 빈은 server.xml과 1:1 동일해야 한다 — 그래야 부모 컨텍스트가 데이터소스만 빼고 동형이다.)

- [ ] **Step 3: csproj — server.sqlite.xml을 출력으로 복사**

[NexaOne.Server.csproj](../../../src/00.Main/NexaOne.Server/NexaOne.Server.csproj)의 Spring Content `<ItemGroup>`(server.xml 항목 근처)에 추가:
```xml
    <Content Include="Spring\server.sqlite.xml">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
```
(기존 `<Content Include="Spring\server.xml">` 항목 바로 다음에 둔다.)

- [ ] **Step 4: 빌드 확인**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug --nologo`
Expected: 0 errors. 출력 `bin/Debug/net8.0/Spring/`에 `server.sqlite.xml` 존재.

- [ ] **Step 5: 기존 ServerTests 회귀 확인 (modules-OFF 무영향)**

Run: `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo`
Expected: 44 passed(현 베이스라인 불변). Spring 경로 파라미터화는 기본값이 server.xml이라 기존 동작 무변경, 신규 SQLite 변형은 명시 지정 시에만 사용된다.

- [ ] **Step 6: 커밋 (PowerShell BOM-free)**

```powershell
git add src/00.Main/NexaOne.Server/Program.cs src/00.Main/NexaOne.Server/Spring/server.sqlite.xml src/00.Main/NexaOne.Server/NexaOne.Server.csproj
$m = "feat(server): Spring 설정 경로 파라미터화(Server:SpringConfig) + SQLite 부팅 변형(server.sqlite.xml)`n`n운영 기본 server.xml(MSSQL) 불변. modules-ON 자동검증 선결.`n`nCo-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
$f=[IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```
주의: `git add -A` 금지(submodules/NexusLogic 더티). 명시 경로만.

---

## Task 2: modules-ON 자식 프로세스 부팅 스모크 테스트

**Files:**
- Create: `test/NexaOne.ServerTests/HostModulesBootSmokeTests.cs`

**스코프 메모:** 빌드된 호스트 DLL을 자식 프로세스로 띄운다(in-proc WAF는 정적 싱글톤 제약으로 불가). 긍정 테스트는 SQLite·modules-ON 부팅이 9서비스·워커·브리지를 올리고 `/health`·`/diag`가 응답함을 검증한다. 음성 테스트는 잘못된 Spring 설정으로 부팅 실패가 프로세스 종료로 **검출됨**을 검증한다(무음 hang 방지=안전망 자체 검증). `/diag`가 9 services로 200을 주면 EST/RMS `GetBean→캐스트`가 통과했다는 뜻이다(실패 시 부팅이 리슨 전에 throw).

- [ ] **Step 1: 실패 테스트 작성**

`test/NexaOne.ServerTests/HostModulesBootSmokeTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>통합 호스트 modules-ON 부팅 자동검증(Phase 6) — 빌드된 호스트를 자식 프로세스로 SQLite·modules-ON
/// 기동해 9개 모듈 + 워커 + EST/RMS GetBean→캐스트 브리지가 한 프로세스에서 실제로 올라오는지 검증한다.
/// 정적 ApplicationServer 싱글톤 제약으로 in-proc WebApplicationFactory 불가 → 자식 프로세스 black-box 스모크.
/// 기존 ServerTests(전부 modules-OFF)가 못 타는 실제 plugin/ALC/브리지 부팅 경로의 단일 안전망.</summary>
public sealed class HostModulesBootSmokeTests
{
    private readonly ITestOutputHelper _o;
    public HostModulesBootSmokeTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public async Task Host_boots_all_nine_modules_workers_and_bridges_in_one_process()
    {
        using var host = await HostProcess.StartAsync(_o, springConfig: "Spring/server.sqlite.xml", expectListening: true);

        host.Listening.Should().BeTrue(
            $"modules-ON 호스트가 SQLite로 기동해 Kestrel이 리슨해야 한다 — 로그:\n{host.Log}");

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        (await http.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK, "기동된 호스트의 /health는 200");

        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostProcess.MintToken());
        var res = await http.GetAsync("/diag");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "인증 토큰이면 /diag 200");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("modulesEnabled").GetBoolean().Should().BeTrue();
        root.GetProperty("services").GetArrayLength().Should().Be(9,
            "9개 도메인 모듈(Mdm·Est·Fdc·Rms·Qms·Cmms·Pom·Shp·Sys)이 모두 로드돼야 한다 — "
            + "성공적 /diag = EST/RMS GetBean→캐스트 브리지 fail-fast 통과(부팅이 리슨에 도달)");
        root.GetProperty("workerCount").GetInt32().Should().BeGreaterThanOrEqualTo(1,
            "백그라운드 워커가 1개 이상 발견돼야 한다(실측 5)");
    }

    [Fact]
    public async Task Host_boot_failure_surfaces_as_process_exit_not_silent_hang()
    {
        // 존재하지 않는 Spring 설정 → CreateServer/스키마 부팅이 throw → 프로세스 비정상 종료.
        // 안전망이 부팅 실패를 '리슨 미도달 + 프로세스 종료'로 검출하는지(거짓 녹색 아님) 검증한다.
        using var host = await HostProcess.StartAsync(_o, springConfig: "Spring/__does_not_exist__.xml", expectListening: false);

        host.Listening.Should().BeFalse("부팅 실패 호스트는 리슨에 도달하지 못해야 한다");
        host.Exited.Should().BeTrue("부팅 실패는 프로세스 종료로 드러나야 한다(무음 hang 아님)");
        host.ExitCode.Should().NotBe(0, "부팅 throw는 비정상 종료코드여야 한다");
    }
}

/// <summary>빌드된 NexaOne.Server를 자식 프로세스로 기동/감시하는 테스트 헬퍼.</summary>
internal sealed class HostProcess : IDisposable
{
    internal const string Secret = "modules-boot-smoke-jwt-secret-key-at-least-32-bytes!!";
    internal const string Issuer = "nexaone-modulesboot";

    public int Port { get; private set; }
    public bool Listening { get; private set; }
    public bool Exited => _proc.HasExited;
    public int ExitCode => _proc.HasExited ? _proc.ExitCode : 0;
    public string Log { get { lock (_sb) return _sb.ToString(); } }

    private readonly Process _proc;
    private readonly StringBuilder _sb = new();
    private readonly string _gwDb;

    private HostProcess(Process proc, string gwDb) { _proc = proc; _gwDb = gwDb; }

    public static async Task<HostProcess> StartAsync(ITestOutputHelper o, string springConfig, bool expectListening)
    {
        var hostDir = ResolveHostBinDir();
        var dll = Path.Combine(hostDir, "NexaOne.Server.dll");
        File.Exists(dll).Should().BeTrue($"호스트가 빌드돼 있어야 한다: {dll} (없으면 `dotnet build` 후 재시도)");

        var gwDb = Path.Combine(Path.GetTempPath(), $"nexaone-boot-gw-{Guid.NewGuid():N}.db");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = hostDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("NexaOne.Server.dll");
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");   // 임의 빈 포트 — stdout의 "Now listening on"에서 실제 포트 파싱
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["Server__Modules__Enabled"] = "true";
        psi.Environment["Server__SpringConfig"] = springConfig;
        psi.Environment["Database__Provider"] = "Sqlite";
        psi.Environment["ConnectionStrings__NexaOne"] = $"Data Source={gwDb};Foreign Keys=False";
        psi.Environment["Jwt__SecretKey"] = Secret;
        psi.Environment["Jwt__Issuer"] = Issuer;
        psi.Environment["Jwt__Audience"] = Issuer;
        psi.Environment["RateLimiting__Enabled"] = "false";

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var hp = new HostProcess(proc, gwDb);
        var listenTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var portRegex = new Regex(@"Now listening on:\s*https?://[\d.]+:(\d+)", RegexOptions.IgnoreCase);

        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (hp._sb) hp._sb.AppendLine(e.Data);
            var m = portRegex.Match(e.Data);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) listenTcs.TrySetResult(p);
        }
        proc.OutputDataReceived += OnData;
        proc.ErrorDataReceived += OnData;
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exitTask = proc.WaitForExitAsync();
        var timeout = Task.Delay(TimeSpan.FromSeconds(expectListening ? 90 : 45));
        var done = await Task.WhenAny(listenTcs.Task, exitTask, timeout);
        if (done == listenTcs.Task)
        {
            hp.Port = await listenTcs.Task;
            hp.Listening = true;
        }
        else
        {
            // 음성/타임아웃 — 종료 대기(짧게)해 ExitCode를 안정화하고 진단 로그를 남긴다.
            if (!proc.HasExited) { try { await Task.WhenAny(exitTask, Task.Delay(2000)); } catch { } }
            o.WriteLine($"[host did not listen] springConfig={springConfig}\n{hp.Log}");
        }
        return hp;
    }

    public static string MintToken()
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Issuer,
            new[] { new Claim(ClaimTypes.NameIdentifier, "modulesboot-test") },
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string ResolveHostBinDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexaOne.sln"))) dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("리포 루트(NexaOne.sln) 미발견 — 호스트 bin 해석 실패.");
        var sep = Path.DirectorySeparatorChar;
        var config = AppContext.BaseDirectory.Contains($"{sep}Release{sep}") ? "Release" : "Debug";
        return Path.Combine(dir.FullName, "src", "00.Main", "NexaOne.Server", "bin", config, "net8.0");
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) { _proc.Kill(entireProcessTree: true); _proc.WaitForExit(5000); } } catch { }
        try { _proc.Dispose(); } catch { }
        try { if (File.Exists(_gwDb)) File.Delete(_gwDb); } catch { }
    }
}
```

- [ ] **Step 2: 빌드 + 테스트 실행 (긍정·음성)**

Run: `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo`
Expected: 46 passed(기존 44 + 신규 2). 긍정 테스트가 9 services·workerCount≥1·/health 200·/diag 200을 통과, 음성 테스트가 부팅 실패를 프로세스 종료로 검출.
주의(타임아웃): 자식 부팅은 SQLite 스키마(34 migrations) + 9모듈 Spring 컨텍스트 로드로 수 초 걸린다(실측 ~2-6초). 긍정 타임아웃 90초·음성 45초로 여유를 뒀다. CI가 느려 간헐 실패하면 타임아웃을 상향(원인 진단 우선 — `host.Log` 출력 확인).
주의(빌드 의존): 이 테스트는 `bin/Debug/net8.0/{NexaOne.Server.dll, Modules/*.dll, Spring/server.sqlite.xml}`이 존재해야 한다. `dotnet test`가 ServerTests를 빌드하면 NexaOne.Server(+모듈+CopyDomainModulePlugins)도 빌드돼 갖춰진다. dll 미발견 시 테스트가 명확한 메시지로 실패한다.

- [ ] **Step 3: 음성 테스트 결정성 확인**

음성 테스트가 안정적으로 '프로세스 종료 + 비정상 코드'를 보는지 확인한다. 만약 존재하지 않는 Spring 설정에서 `EnsureSqliteSchemaIfConfigured`가 조용히 통과하고 `CreateServer`가 throw하는지 의심되면, 실제로 음성 테스트가 `Exited=true && ExitCode!=0`를 통과하는지 로그(`host.Log`)로 확인하라. (부팅이 throw하지 않고 리슨에 도달하면 음성 테스트가 실패하므로, 그 경우 음성 조건을 '리슨 미도달'로 좁히고 원인을 보고하라.)

- [ ] **Step 4: 커밋 (PowerShell BOM-free)**

```powershell
git add test/NexaOne.ServerTests/HostModulesBootSmokeTests.cs
$m = "test(server): modules-ON 부팅 자동검증 — 자식 프로세스로 9모듈·워커·EST/RMS 브리지 한 프로세스 기동 + 부팅실패 검출`n`nin-proc WAF 불가(정적 ApplicationServer 싱글톤) → black-box 스모크. 기존 modules-OFF 테스트가 못 타는 실제 plugin/ALC/브리지 경로 안전망.`n`nCo-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
$f=[IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 3 (컨트롤러 직접 수행): 회귀 + 최종 리뷰 + ff-merge

- [ ] **Step 1: 전체 ServerTests 회귀**

Run: `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo`
Expected: 46 passed(44 기존 + 2 신규), 0 실패.

- [ ] **Step 2: 브로더 회귀 (선택, 변경 영향 범위 확인)**

Program.cs 변경은 modules-ON 경로의 설정 소스만 바꾼다(기본값 동일). 영향은 NexaOne.Server·ServerTests에 한정. 필요 시 `dotnet build NexaOne.sln -c Debug`로 솔루션 빌드 0 errors 확인.

- [ ] **Step 3: 최종 통합 리뷰 + ff-merge**

전체 변경(Program.cs 파라미터화 + server.sqlite.xml + 스모크 테스트)에 홀리스틱 리뷰(서브에이전트) 후, `superpowers:finishing-a-development-branch`로 main에 ff-merge. sln 아티팩트 가드: `git checkout main` 시 NexaOne.sln 더티면 `git checkout -- NexaOne.sln`(2>&1 금지 — PowerShell이 git stderr를 오류로 오인). push는 사용자 미요청 → 안 함.

---

## Self-Review (계획 검토)

**1. 목표 커버리지:** modules-ON 부팅(9모듈+워커+EST/RMS 브리지)의 자동검증 = Task 2 긍정 테스트(/diag 9 services), 부팅실패 검출 = Task 2 음성 테스트. 운영 기본 불변 = Task 1(기본 server.xml 유지, SQLite는 명시 지정 시만). ✓

**2. Placeholder 스캔:** 전 스텝 실제 코드/명령. server.sqlite.xml은 "server.xml 1-82행 복사 + Database 블록 교체"로 구체 지시(전체를 재기술하지 않은 이유는 1-82행이 server.xml과 1:1 동일해야 하므로 복사가 정확). 테스트는 완전한 컴파일 가능 코드. ✓

**3. 타입/계약 일관성:** `Server:SpringConfig`(Program.cs) ↔ `Server__SpringConfig`(테스트 env, `__`=`:`). `/diag` JSON 키 `modulesEnabled/services/workerCount`(Program.cs:218-222) ↔ 테스트 파싱 키 일치. JWT Secret/Issuer/Audience가 HostProcess env와 MintToken에서 동일 상수(HostProcess.Secret/Issuer). 호스트 bin 경로 해석은 기존 SpaStaticServingTests.ResolveServerProjectDir 패턴과 동형(NexaOne.sln 상위탐색). ✓

**4. 알려진 한계(명시):** 자식 부팅은 무겁다(수 초) — CI 시간 영향, 타임아웃 여유 부여. 모듈 SQLite db(server.sqlite.xml의 `nexaone-modules-test.db`)는 호스트 bin에 생성·잔존(gitignored, idempotent 스키마라 무해). 게이트웨이 db는 temp·테스트별 격리·Dispose 삭제. 음성 테스트는 '부팅 throw=프로세스 종료' 가정에 의존 — Step 3에서 결정성 확인.
