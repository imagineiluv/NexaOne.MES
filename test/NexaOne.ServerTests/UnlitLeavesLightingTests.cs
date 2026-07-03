using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Abstractions.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>미점등 6잎 점등분 E2E — 배치 작업 정의(V066, SYS.*BatchProcess)와 가상 이벤트 정의(V067,
/// FDC.*VirtualEvent)의 게이트웨이 CRUD 왕복 + 권한 게이트를 실 SQLite(전 마이그레이션 적용)로 검증한다.
/// 화면(메타 정의)은 동일 쿼리 ID/@param을 바인딩하므로 이 왕복이 화면 데이터 경로의 계약 검증을 겸한다.
/// 실행/평가 엔진은 의도적 후속(마이그레이션 주석 참조) — 여기서는 정의 관리 범위만 검증한다.</summary>
public sealed class UnlitLeavesLightingTests : IClassFixture<UnlitLeavesLightingTests.HostFactory>
{
    private const string Secret = "unlit-leaves-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-unlit-test";
    private readonly HostFactory _factory;
    public UnlitLeavesLightingTests(HostFactory factory) => _factory = factory;

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-unlit-{Guid.NewGuid():N}.db");
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
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "unlit-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(HttpClient client, string queryId, object? p = null)
    {
        var res = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", p ?? new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 조회는 200이어야 한다");
        return (await res.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>())!;
    }

    private static string Col(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    [Fact]
    public async Task Batch_process_definition_crud_roundtrip_with_permission_gate()
    {
        // 권한 게이트 — sys:manage 없는 쓰기는 403(레지스트리 requiredPermission 집행).
        var noPerm = await Client("fdc:read").PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess",
            new Dictionary<string, object> { ["batchId"] = "B-DENY", ["batchName"] = "거부" });
        noPerm.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = Client("sys:manage");
        // 생성 → 목록 반영(V066 테이블이 실제로 존재해야 통과).
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess", new Dictionary<string, object>
        {
            ["batchId"] = "B-001", ["batchName"] = "야간 집계", ["batchType"] = "Schedule",
            ["batchRule"] = "OeeAggregateDay", ["batchOptions"] = "0 0 2 * * ?", ["batchInputData"] = "{}",
            ["description"] = "E2E",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await QueryAsync(admin, "SYS.BatchProcessList");
        rows.Count(r => Col(r, "BATCH_ID") == "B-001").Should().Be(1, "생성 후 목록 반영(V066 실존)");

        // 업서트(갱신) — 동일 ID 재저장은 갱신이어야 한다(중복 행 금지).
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess", new Dictionary<string, object>
        {
            ["batchId"] = "B-001", ["batchName"] = "야간 집계(수정)",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        rows = await QueryAsync(admin, "SYS.BatchProcessList");
        var updated = rows.Where(r => Col(r, "BATCH_ID") == "B-001").ToList();
        updated.Should().HaveCount(1, "업서트는 갱신(중복 행 금지)");
        Col(updated[0], "BATCH_NAME").Should().Be("야간 집계(수정)");

        // 소프트 삭제 → 목록 제외(VALID_STATE 게이트).
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.DeleteBatchProcess",
            new Dictionary<string, object> { ["batchId"] = "B-001" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await QueryAsync(admin, "SYS.BatchProcessList"))
            .Count(r => Col(r, "BATCH_ID") == "B-001").Should().Be(0, "소프트 삭제 후 목록 제외");
    }

    [Fact]
    public async Task Virtual_event_definition_crud_roundtrip_with_permission_gate()
    {
        var noPerm = await Client("sys:manage").PostAsJsonAsync("/api/v1/command/FDC.UpsertVirtualEvent",
            new Dictionary<string, object> { ["equipmentId"] = "EQ1", ["eventId"] = "VE-DENY" });
        noPerm.StatusCode.Should().Be(HttpStatusCode.Forbidden, "가상 이벤트 쓰기는 fdc:manage 전용");

        var fdc = Client("fdc:manage");
        (await fdc.PostAsJsonAsync("/api/v1/command/FDC.UpsertVirtualEvent", new Dictionary<string, object>
        {
            ["plantId"] = "P1", ["equipmentId"] = "EQ1", ["eventId"] = "VE-001",
            ["eventName"] = "과열 감지", ["eventOn"] = "1", ["eventOff"] = "0",
            ["conditionFormula"] = "TEMP > 80 AND PRESSURE > 3", ["description"] = "E2E",
        })).StatusCode.Should().Be(HttpStatusCode.OK, "V067 테이블+레지스트리 실왕복");

        var rows = await QueryAsync(fdc, "FDC.VirtualEventList", new Dictionary<string, object> { ["equipmentId"] = "EQ1" });
        rows.Count(r => Col(r, "EVENT_ID") == "VE-001").Should().Be(1, "생성 후 목록 반영(V067 실존)");
        Col(rows[0], "CONDITION_FORMULA").Should().Contain("TEMP > 80", "수식은 불투명 문자열로 보존(평가 엔진 후속)");

        (await fdc.PostAsJsonAsync("/api/v1/command/FDC.DeleteVirtualEvent",
            new Dictionary<string, object> { ["equipmentId"] = "EQ1", ["eventId"] = "VE-001" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await QueryAsync(fdc, "FDC.VirtualEventList", new Dictionary<string, object> { ["equipmentId"] = "EQ1" }))
            .Should().BeEmpty("소프트 삭제 후 목록 제외");
    }

    // ── 배치 실행 엔진(V068) — 수동 run API가 BATCH_RULE(명명 쓰기쿼리)을 실제 실행하고 이력을 남긴다. ──

    [Fact]
    public async Task Batch_manual_run_executes_named_command_and_records_history()
    {
        var admin = Client("sys:manage");

        // 정의: MDM.CreatePlant를 입력 파라미터(JSON)로 실행하는 배치.
        var plantId = $"BP{Guid.NewGuid():N}"[..10];
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess", new Dictionary<string, object>
        {
            ["batchId"] = "B-RUN", ["batchName"] = "공장 생성 배치", ["batchType"] = "Manual",
            ["batchRule"] = "MDM.CreatePlant",
            ["batchInputData"] = $"{{\"plantId\":\"{plantId}\",\"plantName\":\"배치 생성 공장\"}}",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // 권한 게이트 — run은 sys:manage 전용(CQ-3 선언 정책).
        (await Client("fdc:read").PostAsync("/api/v1/sys/admin/batch/B-RUN/run", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 실행 → 커맨드가 실제로 수행돼 MDM_PLANT 행이 생겨야 한다.
        var run = await admin.PostAsync("/api/v1/sys/admin/batch/B-RUN/run", content: null);
        run.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await run.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
        Col(result!, "success").Should().Be("True", $"실행 실패 사유: {Col(result!, "error")}");

        var plants = await QueryAsync(admin, "MDM.PlantList");
        plants.Count(r => Col(r, "PLANT_ID") == plantId).Should().Be(1, "배치가 명명 커맨드를 실제 실행해야 한다");

        // SAVE_HISTORY 기본 1 → 이력 1행(성공).
        var hist = await QueryAsync(admin, "SYS.BatchProcessHistoryList", new Dictionary<string, object> { ["batchId"] = "B-RUN" });
        hist.Should().HaveCount(1);
        Col(hist[0], "SUCCESS").Should().Be("1");
        Col(hist[0], "EXECUTED_BY").Should().Be("unlit-tester", "수동 실행 주체가 기록돼야 한다");

        // 실패 경로 — 조회 쿼리를 BATCH_RULE로 지정하면 실행 거부 + 실패 이력.
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess", new Dictionary<string, object>
        {
            ["batchId"] = "B-BAD", ["batchName"] = "잘못된 배치", ["batchRule"] = "SYS.BatchProcessList",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var bad = await admin.PostAsync("/api/v1/sys/admin/batch/B-BAD/run", content: null);
        bad.StatusCode.Should().Be(HttpStatusCode.OK, "정의는 존재 — 실행 결과로 실패 보고");
        var badResult = await bad.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
        Col(badResult!, "success").Should().Be("False");
        Col(badResult!, "error").Should().Contain("쓰기", "read 쿼리는 배치 실행 거부");

        // 미존재 배치 → 404.
        (await admin.PostAsync("/api/v1/sys/admin/batch/NO-SUCH/run", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 가상 이벤트 평가 엔진(V067→V069) — 실 모듈 서비스/리포를 게이트웨이 SQLite에 물려 전이 기록을 검증. ──

    [Fact]
    public async Task Virtual_event_engine_evaluates_formula_and_records_transitions_only()
    {
        var fdc = Client("fdc:manage");

        // 정의 + 수집 데이터(파라미터 최신값) 시드 — FDC_COLLECT_DATA FK 대비 FDC_PARAMETER 선행.
        (await fdc.PostAsJsonAsync("/api/v1/command/FDC.UpsertVirtualEvent", new Dictionary<string, object>
        {
            ["plantId"] = "P1", ["equipmentId"] = "EQ-ENG", ["eventId"] = "VE-HOT",
            ["eventName"] = "과열", ["conditionFormula"] = "TEMP > 80 AND PRESSURE > 3",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var service = BuildEngine(out var repo);
        SeedParameter("TEMP");
        SeedParameter("PRESSURE");
        SeedCollect("EQ-ENG", "TEMP", 85m, DateTime.UtcNow.AddSeconds(-10));
        SeedCollect("EQ-ENG", "PRESSURE", 3.5m, DateTime.UtcNow.AddSeconds(-9));

        // 1차 평가 → On + 전이 기록(첫 평가).
        var first = await service.EvaluateAsync("EQ-ENG", "VE-HOT");
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : "");
        first.Value.IsOn.Should().BeTrue();
        first.Value.Changed.Should().BeTrue("첫 평가는 전이로 기록");

        // 동일 상태 재평가 → 기록 없음(전이만 기록).
        var second = await service.EvaluateAsync("EQ-ENG", "VE-HOT");
        second.Value.IsOn.Should().BeTrue();
        second.Value.Changed.Should().BeFalse("동일 상태 반복 평가는 미기록");

        // 최신값 하락 → Off 전이 1회 기록.
        SeedCollect("EQ-ENG", "TEMP", 60m, DateTime.UtcNow);
        var third = await service.EvaluateAsync("EQ-ENG", "VE-HOT");
        third.Value.IsOn.Should().BeFalse();
        third.Value.Changed.Should().BeTrue();

        var history = await QueryAsync(fdc, "FDC.VirtualEventHistoryList",
            new Dictionary<string, object> { ["equipmentId"] = "EQ-ENG", ["eventId"] = "VE-HOT" });
        history.Should().HaveCount(2, "On 전이 1 + Off 전이 1(중간 동일 상태는 미기록)");
        Col(history[0], "EVENT_STATE").Should().Be("Off", "최신 이력이 마지막 전이");
        Col(history[1], "EVENT_STATE").Should().Be("On");

        // 값 없는 파라미터 참조 — 조용한 false가 아니라 실패 보고.
        (await fdc.PostAsJsonAsync("/api/v1/command/FDC.UpsertVirtualEvent", new Dictionary<string, object>
        {
            ["plantId"] = "P1", ["equipmentId"] = "EQ-ENG", ["eventId"] = "VE-GHOST",
            ["eventName"] = "유령", ["conditionFormula"] = "HUMIDITY > 50",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var ghost = await service.EvaluateAsync("EQ-ENG", "VE-GHOST");
        ghost.IsFailure.Should().BeTrue();
        ghost.Error.Description.Should().Contain("HUMIDITY");
        _ = repo; // 구성 확인용
    }

    private VirtualEventService BuildEngine(out VirtualEventRepository repository)
    {
        _ = _factory.CreateClient();   // 스키마 부트스트랩
        var ds = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = $"Data Source={_factory.DbPath};Foreign Keys=False",
        };
        repository = new VirtualEventRepository(ds, new SqliteEesDbCapability());
        return new VirtualEventService(repository);
    }

    private void SeedParameter(string parameterId)
        => ExecSql(@"INSERT OR IGNORE INTO FDC_PARAMETER
            (PARAMETER_ID, PARAMETER_NAME, EQUIPMENT_ID, UNIT, LOWER_LIMIT, UPPER_LIMIT, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ($id, $id, 'EQ-ENG', 'u', 0, 9999, 1, 'TEST', $now, 'TEST', $now)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", parameterId);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            });

    private void SeedCollect(string equipmentId, string parameterId, decimal value, DateTime at)
        => ExecSql(@"INSERT INTO FDC_COLLECT_DATA
            (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY, LOWER_LIMIT, UPPER_LIMIT)
            VALUES ($cid, $eq, $pid, $val, $at, 'Good', 0, 9999)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$cid", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$eq", equipmentId);
                cmd.Parameters.AddWithValue("$pid", parameterId);
                cmd.Parameters.AddWithValue("$val", value);
                cmd.Parameters.AddWithValue("$at", at.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            });

    private void ExecSql(string sql, Action<Microsoft.Data.Sqlite.SqliteCommand> bind)
    {
        _ = _factory.CreateClient();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_factory.DbPath};Foreign Keys=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }
}
