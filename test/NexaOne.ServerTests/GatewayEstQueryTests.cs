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

/// <summary>게이트웨이 우선 EST(설비 상태 추적) read E2E — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩).
/// SmartUX EPT(설비성능관리) 화면 점등(Phase 3)용 신설 쿼리를 검증한다: 현재상태(EST.CurrentStateList) /
/// 상태이력(EST.StateHistoryList) / 설비알람(EST.EquipmentAlarmList) / WORST10 알람 집계(EST.WorstAlarmEquipment).
/// EST_EQUIPMENT_STATE / EST_EQUIPMENT_STATE_HISTORY / EST_EQUIPMENT_ALARM 를 SqliteConnection 직접 INSERT로
/// 시드한 뒤 명명 read 라운드트립을 검증한다(FK는 Foreign Keys=False로 비활성 — 부모행 없이 시드). + 미인증 401.</summary>
public sealed class GatewayEstQueryTests : IClassFixture<GatewayEstQueryTests.EstFactory>
{
    private const string Secret = "est-gateway-e2e-jwt-secret-key-at-least-32-bytes!!";
    private const string Issuer = "nexaone-est-test";
    private readonly EstFactory _factory;
    public GatewayEstQueryTests(EstFactory factory) => _factory = factory;

    public sealed class EstFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-est-e2e-{Guid.NewGuid():N}.db");
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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "est-e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    // EST_EQUIPMENT_STATE 1건 시드(현재 상태, 감사 컬럼 없음).
    private void SeedCurrentState(string equipmentId, string plantId, string stateId)
        => Exec(@"INSERT INTO EST_EQUIPMENT_STATE
            (EQUIPMENT_ID, PLANT_ID, CURRENT_STATE_ID, STATE_CHANGED_AT, STATE_VERSION)
            VALUES (@eq, @plant, @state, @now, 1)", cmd =>
        {
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@state", stateId);
            cmd.Parameters.AddWithValue("@now", Now());
        });

    // EST_EQUIPMENT_STATE_HISTORY 1건 시드(상태 변경 이력, 감사 컬럼 없음).
    private void SeedStateHistory(string histId, string equipmentId, string fromState, string toState, DateTime changedAt)
        => Exec(@"INSERT INTO EST_EQUIPMENT_STATE_HISTORY
            (HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE, CHANGED_AT, CHANGED_BY, REASON, SOURCE_TYPE)
            VALUES (@id, @eq, @from, @to, @to, @at, 'TEST', '사유', 'Manual')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", histId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@from", fromState);
            cmd.Parameters.AddWithValue("@to", toState);
            cmd.Parameters.AddWithValue("@at", changedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        });

    // EST_EQUIPMENT_ALARM 1건 시드(설비 알람; 감사 컬럼 CREATED_BY/UPDATED_BY NOT NULL 명시).
    private void SeedEquipmentAlarm(string alarmId, string equipmentId, string level, DateTime occurredAt)
        => Exec(@"INSERT INTO EST_EQUIPMENT_ALARM
            (ALARM_ID, EQUIPMENT_ID, ALARM_CODE, ALARM_NAME, ALARM_LEVEL, OCCURRED_AT,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @eq, 'ALM01', '과온 알람', @lvl, @occ, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", alarmId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@lvl", level);
            cmd.Parameters.AddWithValue("@occ", occurredAt.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@now", Now());
        });

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/EST.CurrentStateList",
            new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task CurrentStateList_returns_seeded_and_plant_filter_narrows()
    {
        EnsureSchemaReady();
        var plantA = "PL_" + Suffix();
        var plantB = "PL_" + Suffix();
        var eqA = "EQ_" + Suffix();
        var eqB = "EQ_" + Suffix();
        SeedCurrentState(eqA, plantA, "RUN");
        SeedCurrentState(eqB, plantB, "IDLE");

        var all = await Query("EST.CurrentStateList", new());  // 공장 필터 없이 전체(NULL-guard)
        all.Select(r => r["EQUIPMENT_ID"].ToString()).Should().Contain(new[] { eqA, eqB },
            "공장 필터 없이 전체 설비 현재 상태가 조회돼야 한다(설비 상태 현황/공장 모니터링 점등)");

        var inPlant = await Query("EST.CurrentStateList", new() { ["plantId"] = plantA });
        var ids = inPlant.Select(r => r["EQUIPMENT_ID"].ToString()).ToList();
        ids.Should().Contain(eqA);
        ids.Should().NotContain(eqB, "plantId 필터는 해당 공장 설비만 반환");
    }

    [Fact]
    public async Task StateHistoryList_returns_seeded_ordered_recent_first()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var older = $"H_{Suffix()}";
        var newer = $"H_{Suffix()}";
        SeedStateHistory(older, eq, "IDLE", "RUN", DateTime.UtcNow.AddHours(-3));
        SeedStateHistory(newer, eq, "RUN", "DOWN", DateTime.UtcNow.AddMinutes(-5));

        var rows = await Query("EST.StateHistoryList", new() { ["equipmentId"] = eq });
        var ids = rows.Select(r => r["HIST_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { older, newer });
        ids.IndexOf(newer).Should().BeLessThan(ids.IndexOf(older), "CHANGED_AT 내림차순(최근이 먼저)");
        rows.Should().OnlyContain(r => r.ContainsKey("FROM_STATE") && r.ContainsKey("TO_STATE"));
    }

    [Fact]
    public async Task EquipmentAlarmList_returns_seeded_and_level_filter_narrows()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var crit = $"A_{Suffix()}";
        var warn = $"A_{Suffix()}";
        SeedEquipmentAlarm(crit, eq, "Critical", DateTime.UtcNow.AddMinutes(-2));
        SeedEquipmentAlarm(warn, eq, "Warning", DateTime.UtcNow.AddMinutes(-10));

        var byEquip = await Query("EST.EquipmentAlarmList", new() { ["equipmentId"] = eq });
        byEquip.Select(r => r["ALARM_ID"].ToString()).Should().Contain(new[] { crit, warn },
            "설비 알람 이력이 조회돼야 한다(설비 알람 이력/알람 발생 이력 점등)");

        var byLevel = await Query("EST.EquipmentAlarmList", new() { ["equipmentId"] = eq, ["alarmLevel"] = "Critical" });
        var ids = byLevel.Select(r => r["ALARM_ID"].ToString()).ToList();
        ids.Should().Contain(crit);
        ids.Should().NotContain(warn, "alarmLevel 필터는 해당 등급만 반환");
    }

    [Fact]
    public async Task WorstAlarmEquipment_aggregates_counts_desc()
    {
        EnsureSchemaReady();
        var noisy = "EQ_NOISY_" + Suffix();
        var quiet = "EQ_QUIET_" + Suffix();
        SeedEquipmentAlarm($"A_{Suffix()}", noisy, "Critical", DateTime.UtcNow.AddMinutes(-1));
        SeedEquipmentAlarm($"A_{Suffix()}", noisy, "Warning", DateTime.UtcNow.AddMinutes(-2));
        SeedEquipmentAlarm($"A_{Suffix()}", noisy, "Warning", DateTime.UtcNow.AddMinutes(-3));
        SeedEquipmentAlarm($"A_{Suffix()}", quiet, "Warning", DateTime.UtcNow.AddMinutes(-4));

        var rows = await Query("EST.WorstAlarmEquipment", new());  // 상위 10 집계(파라미터 없음)
        var noisyRow = rows.Single(r => r["EQUIPMENT_ID"].ToString() == noisy);
        var quietRow = rows.Single(r => r["EQUIPMENT_ID"].ToString() == quiet);
        // 응답 값은 JsonElement(숫자) — 형제 테스트와 동일하게 문자열 경유로 파싱한다.
        int.Parse(noisyRow["ALARM_COUNT"].ToString()!).Should().Be(3, "설비별 알람 건수를 집계해야 한다");
        int.Parse(quietRow["ALARM_COUNT"].ToString()!).Should().Be(1);
        rows.Select(r => r["EQUIPMENT_ID"].ToString()).ToList()
            .IndexOf(noisy).Should().BeLessThan(rows.Select(r => r["EQUIPMENT_ID"].ToString()).ToList().IndexOf(quiet),
            "ALARM_COUNT 내림차순(WORST가 먼저)");
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
