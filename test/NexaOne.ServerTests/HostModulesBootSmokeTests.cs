using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[CollectionDefinition(ChildProcessSmokeCollection.Name, DisableParallelization = true)]
public sealed class ChildProcessSmokeCollection
{
    public const string Name = "Child-process host smoke";
}

/// <summary>통합 호스트 modules-ON 부팅 자동검증(Phase 6) — 빌드된 호스트를 자식 프로세스로 SQLite·modules-ON
/// 기동해 10개 모듈 + 워커 + 선언형 catalog의 전체 Bridge가 한 프로세스에서 실제로 올라오는지 검증한다.
/// 정적 ApplicationServer 싱글톤 제약으로 in-proc WebApplicationFactory 불가 → 자식 프로세스 black-box 스모크.
/// 기존 ServerTests(전부 modules-OFF)가 못 타는 실제 plugin/ALC/브리지 부팅 경로의 단일 안전망.</summary>
[Collection(ChildProcessSmokeCollection.Name)]
public sealed class HostModulesBootSmokeTests
{
    private readonly ITestOutputHelper _o;
    public HostModulesBootSmokeTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public async Task Host_boots_all_ten_modules_workers_and_bridges_in_one_process()
    {
        // Database:Provider alone must select the matching Spring parent context.
        // This guards against gateway=SQLite / module=SQL Server split-brain startup.
        using var host = await HostProcess.StartAsync(
            _o,
            springConfig: null,
            expectListening: true,
            concurrentServices: true,
            enableBatchWorker: true);

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
        root.GetProperty("services").GetArrayLength().Should().Be(10,
            "10개 도메인 모듈(Mdm·Est·Fdc·Ivt·Rms·Qms·Ems·Pom·Shp·Sys)이 모두 로드돼야 한다 — "
            + "성공적 /diag = 전체 선언형 Bridge의 Bean/계약 fail-fast 통과(부팅이 리슨에 도달)");
        root.GetProperty("workerCount").GetInt32().Should().BeGreaterThanOrEqualTo(1,
            "백그라운드 워커가 1개 이상 발견돼야 한다(실측 5)");

        var expectedBridgeContracts = NexaModuleBridgeCatalog
            .Discover(typeof(INexaModuleBridge).Assembly)
            .Descriptors
            .Select(descriptor => descriptor.ContractType.FullName)
            .ToHashSet(StringComparer.Ordinal);
        var bridges = root.GetProperty("bridges");
        bridges.GetArrayLength().Should().Be(expectedBridgeContracts.Count,
            "marker 계약 집합과 시작 시 검증된 Spring Bridge 집합이 정확히 같아야 한다");
        bridges.EnumerateArray()
            .Select(item => item.GetProperty("contract").GetString())
            .Should().BeEquivalentTo(expectedBridgeContracts);
        bridges.EnumerateArray().Should().OnlyContain(item =>
                !string.IsNullOrWhiteSpace(item.GetProperty("module").GetString())
                && !string.IsNullOrWhiteSpace(item.GetProperty("beanName").GetString())
                && !string.IsNullOrWhiteSpace(item.GetProperty("implementation").GetString()),
            "/diag는 연결 옵션 없이 검증된 module/bean/contract/implementation만 노출해야 한다");

        var dependencies = root.GetProperty("externalDependencies").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
        dependencies.Keys.Should().BeEquivalentTo(new[]
        {
            "nexaone.database", "nexaone.fdc.plc", "nexaone.messaging",
        }, "실제 DB·메시징·PLC readiness가 모두 제품 catalog에 등록돼야 한다");

        var database = dependencies["nexaone.database"];
        database.GetProperty("kind").GetString().Should().Be("database");
        database.GetProperty("status").GetString().Should().Be("Healthy");

        var messaging = dependencies["nexaone.messaging"];
        messaging.GetProperty("kind").GetString().Should().Be("messaging");
        messaging.GetProperty("status").GetString().Should().Be("Healthy");
        messaging.GetProperty("details").GetProperty("transport").GetString().Should().Be("in-memory");

        var plc = dependencies["nexaone.fdc.plc"];
        plc.GetProperty("id").GetString().Should().Be("nexaone.fdc.plc");
        plc.GetProperty("kind").GetString().Should().Be("plc");
        plc.GetProperty("status").GetString().Should().Be("Healthy");
        plc.GetProperty("details").GetProperty("registeredProtocols").GetString()
            .Should().ContainAll("OpcUa", "ModbusTcp", "SiemensS7", "MitsubishiMc", "EtherNetIp");
        var diagnosticJson = root.GetRawText();
        diagnosticJson.Should().NotContain(host.DatabasePath);
        diagnosticJson.Should().NotContain($"Data Source={host.DatabasePath};Foreign Keys=False");
        diagnosticJson.Should().NotContain(HostProcess.Secret);
        diagnosticJson.Should().NotContain("ConnectionString", "진단 응답에 DB 연결 구성을 노출하면 안 된다");
        diagnosticJson.Should().NotContain("Password", "진단 응답에 자격 증명 키를 노출하면 안 된다");

        // SignalR 허브 복원 회귀 검증(폐기 NexaOne.API → 통합 호스트 이식). SPA(createHub)가 /hubs/smartees에
        // access_token 쿼리로 연결하는 실경로를 그대로 재현한다. negotiate가 404면 MapHub 누락, 401이면 매핑됐고
        // [Authorize] 적용됨을 뜻한다(무음 미매핑 회귀 차단).
        using (var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") })
        {
            var noAuth = await anon.PostAsync("/hubs/smartees/negotiate?negotiateVersion=1", content: null);
            noAuth.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "허브는 매핑돼 있고 무인증 negotiate는 401이어야 한다(404면 MapHub 누락 회귀)");

            // JwtBearer OnMessageReceived 쿼리 토큰 경로 — WebSocket이 헤더를 못 실어 SPA가 쓰는 실제 인증 방식.
            var qToken = await anon.PostAsync(
                $"/hubs/smartees/negotiate?negotiateVersion=1&access_token={HostProcess.MintToken()}", content: null);
            qToken.StatusCode.Should().Be(HttpStatusCode.OK,
                "access_token 쿼리로 인증된 negotiate는 200 + connectionId를 반환해야 한다(SPA 연결 경로)");
            using var neg = JsonDocument.Parse(await qToken.Content.ReadAsStringAsync());
            neg.RootElement.TryGetProperty("connectionId", out _).Should().BeTrue(
                "negotiate 응답에 connectionId가 있어야 한다(허브 정상 협상)");
        }

        // OEE 수동 집계 브리지(ADR-008) — modules-ON에서 IOeeAggregationBridge(GetBean→캐스트) 배선 + 얇은 컨트롤러
        // 동작을 검증한다. est:manage 토큰으로 재집계 → 200 + affected(int) = 브리지가 정상 캐스트·등록됐고(미배선이면
        // 컨트롤러 생성 실패), 집계가 모듈 스키마에서 예외 없이 실행됐음을 뜻한다(마트 테이블 존재).
        // NOTE: SQLite 스모크는 모듈 eesDataSource(server.sqlite.xml: nexaone-modules-test.db)와 게이트웨이 dev 시드 DB가
        // 분리돼 있어 목표가 비어 affected=0일 수 있다. 실제 집계 정확도(가용성/성능/품질·작업조)는 OeeAggregationRepositoryTests가 담당.
        using (var oee = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") })
        {
            var noPerm = await oee.PostAsJsonAsync("/api/v1/oee/aggregate-day", new { date = "2026-06-01" });
            noPerm.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "무인증 OEE 집계는 401");

            // CQ-3 선언 정책 검증 — 인증됐지만 est:manage 없는 토큰은 [RequirePermission] 정책이 403으로 거부한다.
            oee.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostProcess.MintToken());
            var noPermAuthed = await oee.PostAsJsonAsync("/api/v1/oee/aggregate-day", new { date = "2026-06-01" });
            noPermAuthed.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "권한 없는 인증 토큰은 perm: 정책(PermissionAuthorizationHandler)이 403으로 거부해야 한다(CQ-3)");
            oee.DefaultRequestHeaders.Authorization = null;

            oee.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostProcess.MintToken(Permissions.EstManage));
            var agg = await oee.PostAsJsonAsync("/api/v1/oee/aggregate-day", new { date = "2026-06-01" });
            agg.StatusCode.Should().Be(HttpStatusCode.OK,
                $"est:manage 토큰이면 OEE 수동 집계 200이어야 한다(브리지 배선+집계 실행) — 로그:\n{host.Log}");
            using var aggDoc = JsonDocument.Parse(await agg.Content.ReadAsStringAsync());
            aggDoc.RootElement.GetProperty("affected").GetInt32().Should().BeGreaterThanOrEqualTo(0,
                "affected(int) 반환 = 집계가 모듈 스키마에서 예외 없이 실행됨");

            // 실브리지 전이 E2E(TEST-3 복원) — 상태 매트릭스 업서트→조회가 plugin-ALC EquipmentStateService를
            // 실제로 관통해 모듈 DB에 쓰고 읽음을 검증한다(설비 의존 없는 브리지 쓰기 경로).
            var upsert = await oee.PostAsJsonAsync("/api/v1/est/state-matrix",
                new { plantId = "SMOKEPL", fromStateId = "IDLE", toStateId = "RUN", allowFlag = true, setStateId = "RUN", requireReason = false });
            upsert.StatusCode.Should().Be(HttpStatusCode.OK,
                $"est:manage면 실브리지 매트릭스 업서트 200이어야 한다(plugin 쓰기 경로) — 로그:\n{host.Log}");
            var matrix = await oee.GetAsync("/api/v1/est/state-matrix?plantId=SMOKEPL");
            matrix.StatusCode.Should().Be(HttpStatusCode.OK);
            (await matrix.Content.ReadAsStringAsync()).Should().Contain("IDLE",
                "업서트한 전이(IDLE→RUN)가 실브리지 조회로 라운드트립돼야 한다(TEST-3)");
        }

        // 회원가입 신청→승인 풀사이클(§19.3) — 익명 신청이 plugin-ALC UserRegistrationService를 실제로 관통해
        // 모듈 DB에 기록되고, 승인이 역할 검증(게이트웨이 SYS_ROLE, SEC-1 재사용)→임시 비밀번호 발급→DATA-6 단일
        // 트랜잭션(SYS_USER 생성+신청 전환)까지 완주함을 검증한다. userId는 모듈 DB가 실행 간 재사용될 수 있어 유일화.
        using (var sys = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") })
        {
            var uid = $"smk{Guid.NewGuid():N}"[..12];
            var avail = await sys.GetAsync($"/api/v1/sys/admin/user-requests/availability?userId={uid}");
            avail.StatusCode.Should().Be(HttpStatusCode.OK, "아이디 중복확인은 익명 진입점(§19.3.2)");
            (await avail.Content.ReadAsStringAsync()).Should().Contain("true", "신규 ID는 사용 가능");

            var created = await sys.PostAsJsonAsync("/api/v1/sys/admin/user-requests",
                new { userId = uid, userName = "스모크신청", email = $"{uid}@smoke.test", department = "생산", position = "사원", plantId = "SMOKEPL", termsAccepted = true });
            created.StatusCode.Should().Be(HttpStatusCode.OK, $"익명 가입 신청은 200(실브리지 쓰기) — 로그:\n{host.Log}");
            using var reqDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var requestId = reqDoc.RootElement.GetProperty("requestId").GetString();

            sys.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostProcess.MintToken(Permissions.SysManage));
            var approved = await sys.PostAsJsonAsync($"/api/v1/sys/admin/user-requests/{requestId}/approve",
                new { roleId = "VIEWER" });   // V063 표준 역할 시드 — 게이트웨이 DB는 매 실행 신규라 항상 존재
            approved.StatusCode.Should().Be(HttpStatusCode.OK,
                $"승인 = 역할 검증 + DATA-6 단일 트랜잭션 완주여야 한다 — 로그:\n{host.Log}");
            using var apprDoc = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
            apprDoc.RootElement.GetProperty("request").GetProperty("status").GetString().Should().Be("Approved");
            var tempPassword = apprDoc.RootElement.GetProperty("tempPassword").GetString();
            tempPassword.Should().NotBeNullOrWhiteSpace(
                "임시 비밀번호는 승인 응답에 1회 노출(관리자 전달용, 최초 로그인 시 변경 강제)");

            // dev DB 통일 입증 — 모듈(plugin ALC)이 생성한 SYS_USER를 게이트웨이 인증 경로가 같은 SQLite에서
            // 즉시 읽는다: 승인 직후 임시 비밀번호 로그인 성공 + 최초 변경 강제 플래그. (통일 전에는 모듈 DB와
            // 게이트웨이 DB가 분리돼 이 로그인이 불가능했다 — 회귀 시 이 단언이 검출한다.)
            sys.DefaultRequestHeaders.Authorization = null;
            var login = await sys.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = uid, password = tempPassword, plantId = "SMOKEPL" });
            login.StatusCode.Should().Be(HttpStatusCode.OK,
                $"승인된 사용자는 임시 비밀번호로 즉시 로그인돼야 한다(모듈↔게이트웨이 단일 DB) — 로그:\n{host.Log}");
            var loginBody = await login.Content.ReadAsStringAsync();
            loginBody.Should().Contain("\"requirePasswordChange\":true",
                "PasswordState=Create — 최초 로그인 시 비밀번호 변경 강제");

            // §20.10 강제 변경 풀사이클 — pwdChange 토큰으로 change-password(자기해제) → 새 비밀번호
            // 재로그인은 강제 플래그가 꺼지고, 새 토큰으로 업무 API(query 게이트웨이)가 200이어야 한다.
            using var loginDoc = JsonDocument.Parse(loginBody);
            var tempToken = loginDoc.RootElement.GetProperty("accessToken").GetString();
            sys.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tempToken);
            var newPassword = $"Smoke#Pw{Random.Shared.Next(1000, 9999)}!";
            var changed = await sys.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = tempPassword, newPassword, confirmPassword = newPassword });
            changed.StatusCode.Should().Be(HttpStatusCode.OK,
                $"pwdChange 토큰도 auth 경로는 허용 — 변경으로 자기해제한다 — 로그:\n{host.Log}");

            sys.DefaultRequestHeaders.Authorization = null;
            var relogin = await sys.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = uid, password = newPassword, plantId = "SMOKEPL" });
            relogin.StatusCode.Should().Be(HttpStatusCode.OK, "변경한 새 비밀번호로 로그인돼야 한다");
            var reloginBody = await relogin.Content.ReadAsStringAsync();
            reloginBody.Should().Contain("\"requirePasswordChange\":false", "변경 후에는 강제 플래그가 꺼진다");

            using var reloginDoc = JsonDocument.Parse(reloginBody);
            sys.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", reloginDoc.RootElement.GetProperty("accessToken").GetString());
            // VIEWER 기본권한은 최소권한(fdc:read)만 유지한다. 로그인 직후 공통 UI 부트스트랩으로
            // 실제 사용하는 자기 역할 메뉴 조회는 access="public"인 MenuTreeForUser다.
            var business = await sys.PostAsJsonAsync(
                "/api/v1/query/SYS.MenuTreeForUser", new Dictionary<string, object>());
            business.StatusCode.Should().Be(HttpStatusCode.OK,
                "새 토큰(pwdChange 클레임 없음)은 업무 API 차단이 해제돼야 한다");
        }

        // 배포 풀사이클(§20.11, IDeployBridge 실브리지) — 업로드(SHA-256 저장)→latest 선정→다운로드 바이트
        // 일치→비활성 회수 후 latest 404까지 관통한다. 버전은 모듈 DB 재사용 대비 유일화(UNIQUE VERSION).
        using (var deploy = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") })
        {
            deploy.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostProcess.MintToken(Permissions.SysManage));
            var version = $"9.{Random.Shared.Next(1, 999)}.{Random.Shared.Next(1, 9999)}.0";
            var payload = System.Text.Encoding.UTF8.GetBytes($"smoke-deploy-{version}");

            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(payload), "file", "NexaMesClient.zip" },
                { new StringContent(version), "version" },
                { new StringContent("스모크 배포"), "description" },
                { new StringContent("false"), "forceUpdate" },
            };
            var uploaded = await deploy.PostAsync("/api/v1/deploy/files", form);
            uploaded.StatusCode.Should().Be(HttpStatusCode.OK, $"배포 업로드(실브리지+디스크 저장) — 로그:\n{host.Log}");
            using var upDoc = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            var fileId = upDoc.RootElement.GetProperty("fileId").GetString();
            upDoc.RootElement.GetProperty("hash").GetString().Should().NotBeNullOrWhiteSpace("SHA-256 스트리밍 계산");

            var latest = await deploy.GetAsync("/api/v1/deploy/latest");
            latest.StatusCode.Should().Be(HttpStatusCode.OK);
            (await latest.Content.ReadAsStringAsync()).Should().Contain(version,
                "System.Version 비교로 방금 올린 최고 버전이 latest여야 한다");

            var downloaded = await deploy.GetAsync($"/api/v1/deploy/files/{fileId}/download");
            downloaded.StatusCode.Should().Be(HttpStatusCode.OK);
            (await downloaded.Content.ReadAsByteArrayAsync()).Should().Equal(payload,
                "다운로드 바이트가 업로드 원본과 일치해야 한다(디스크 저장/읽기 무손실)");

            (await deploy.PostAsync($"/api/v1/deploy/files/{fileId}/deactivate", content: null))
                .StatusCode.Should().Be(HttpStatusCode.NoContent, "문제 버전 회수(비활성)");
            (await deploy.GetAsync($"/api/v1/deploy/files/{fileId}/download"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound, "비활성 버전은 다운로드 차단");
        }
    }

    [Fact]
    public void Host_smoke_defaults_to_the_current_test_build_output()
    {
        var currentOutput = Path.GetFullPath(AppContext.BaseDirectory);

        var resolved = HostProcess.ResolveHostBinDir(
            explicitHostDirectory: null,
            currentTestOutputDirectory: currentOutput);

        resolved.Should().Be(currentOutput,
            "an unset override must launch the output that produced this test assembly, not a reconstructed repo bin path");
        HostProcess.ValidateCurrentTestHostOutput(resolved);
    }

    [Fact]
    public async Task Host_boot_failure_surfaces_as_process_exit_not_silent_hang()
    {
        // 존재하지 않는 Spring 설정 → CreateServer/스키마 부팅이 throw → 프로세스 비정상 종료.
        // 안전망이 부팅 실패를 '리슨 미도달 + 프로세스 종료'로 검출하는지(거짓 녹색 아님) 검증한다.
        using var host = await HostProcess.StartAsync(_o, springConfig: "config/__does_not_exist__.xml", expectListening: false);

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
    public string DatabasePath => _gwDb;

    private readonly Process _proc;
    private readonly StringBuilder _sb = new();
    private readonly string _gwDb;
    private readonly string _runtimeTemp;

    private HostProcess(Process proc, string gwDb, string runtimeTemp)
    {
        _proc = proc;
        _gwDb = gwDb;
        _runtimeTemp = runtimeTemp;
    }

    public static async Task<HostProcess> StartAsync(
        ITestOutputHelper o,
        string? springConfig,
        bool expectListening,
        bool concurrentServices = false,
        bool enableBatchWorker = false)
    {
        var explicitHostDirectory = Environment.GetEnvironmentVariable("NEXAONE_TEST_HOST_BIN");
        var hostDir = ResolveHostBinDir(explicitHostDirectory, AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(explicitHostDirectory))
            ValidateCurrentTestHostOutput(hostDir);
        var dll = Path.Combine(hostDir, "NexaOne.Server.dll");
        File.Exists(dll).Should().BeTrue($"호스트가 빌드돼 있어야 한다: {dll} (없으면 `dotnet build` 후 재시도)");

        var runtimeTemp = Path.Combine(Path.GetTempPath(), $"nexaone-host-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeTemp);
        var gwDb = Path.Combine(runtimeTemp, "gateway.db");
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
        if (!string.IsNullOrWhiteSpace(springConfig))
            psi.Environment["Server__SpringConfig"] = springConfig;
        psi.Environment["Database__Provider"] = "Sqlite";
        psi.Environment["ConnectionStrings__NexaOne"] = $"Data Source={gwDb};Foreign Keys=False";
        psi.Environment["Jwt__SecretKey"] = Secret;
        psi.Environment["Jwt__Issuer"] = Issuer;
        psi.Environment["Jwt__Audience"] = Issuer;
        psi.Environment["RateLimiting__Enabled"] = "false";
        psi.Environment["Host__ServicesStartConcurrently"] = concurrentServices ? "true" : "false";
        psi.Environment["Host__ServicesStopConcurrently"] = concurrentServices ? "true" : "false";
        psi.Environment["Worker__Sys__BatchProcess__Enabled"] = enableBatchWorker ? "true" : "false";
        psi.Environment["TMP"] = runtimeTemp;
        psi.Environment["TEMP"] = runtimeTemp;

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var hp = new HostProcess(proc, gwDb, runtimeTemp);
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

    public static string MintToken(params string[] permissions)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "modulesboot-test") };
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims,
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal static string ResolveHostBinDir(
        string? explicitHostDirectory,
        string currentTestOutputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitHostDirectory))
            return Path.GetFullPath(explicitHostDirectory);

        if (string.IsNullOrWhiteSpace(currentTestOutputDirectory))
            throw new InvalidOperationException("현재 ServerTests 출력 경로가 비어 있습니다.");

        return Path.GetFullPath(currentTestOutputDirectory);
    }

    internal static void ValidateCurrentTestHostOutput(string hostDir)
    {
        var hostDll = Path.GetFullPath(Path.Combine(hostDir, "NexaOne.Server.dll"));
        var loadedHostDll = Path.GetFullPath(typeof(Program).Assembly.Location);
        if (!string.Equals(hostDll, loadedHostDll, PathComparison))
        {
            throw new InvalidOperationException(
                $"Host smoke would not launch the Server assembly loaded by this test run. "
                + $"expected={loadedHostDll}, selected={hostDll}");
        }

        var requiredHostFiles = new[]
        {
            "NexaOne.Server.deps.json",
            "NexaOne.Server.runtimeconfig.json",
            Path.Combine("config", "app.xml"),
            Path.Combine("config", "host", "server.sqlite.xml"),
        };
        foreach (var relativePath in requiredHostFiles)
        {
            var path = Path.Combine(hostDir, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Current host-smoke output is incomplete: {path}", path);
        }

        var moduleNames = new[]
        {
            "NexaOne.MDM", "NexaOne.EST", "NexaOne.FDC", "NexaOne.IVT", "NexaOne.RMS",
            "NexaOne.QMS", "NexaOne.EMS", "NexaOne.POM", "NexaOne.SHP", "NexaOne.SYS",
        };
        foreach (var moduleName in moduleNames)
        {
            var currentReference = Path.Combine(hostDir, moduleName + ".dll");
            var plugin = Path.Combine(hostDir, "Modules", moduleName + ".dll");
            if (!File.Exists(currentReference) || !File.Exists(plugin))
            {
                throw new FileNotFoundException(
                    $"Current host-smoke plugin pair is incomplete: reference={currentReference}, plugin={plugin}");
            }

            if (!File.ReadAllBytes(currentReference).AsSpan().SequenceEqual(File.ReadAllBytes(plugin)))
            {
                throw new InvalidOperationException(
                    $"Host-smoke plugin is stale relative to this test build: {moduleName}");
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void Dispose()
    {
        try { if (!_proc.HasExited) { _proc.Kill(entireProcessTree: true); _proc.WaitForExit(5000); } } catch { }
        try { _proc.Dispose(); } catch { }
        try { if (File.Exists(_gwDb)) File.Delete(_gwDb); } catch { }
        try { if (Directory.Exists(_runtimeTemp)) Directory.Delete(_runtimeTemp, recursive: true); } catch { }
    }
}
