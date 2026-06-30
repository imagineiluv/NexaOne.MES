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
        using var host = await HostProcess.StartAsync(_o, springConfig: "config/host/server.sqlite.xml", expectListening: true);

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
            "9개 도메인 모듈(Mdm·Est·Fdc·Rms·Qms·Ems·Pom·Shp·Sys)이 모두 로드돼야 한다 — "
            + "성공적 /diag = EST/RMS GetBean→캐스트 브리지 fail-fast 통과(부팅이 리슨에 도달)");
        root.GetProperty("workerCount").GetInt32().Should().BeGreaterThanOrEqualTo(1,
            "백그라운드 워커가 1개 이상 발견돼야 한다(실측 5)");
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
