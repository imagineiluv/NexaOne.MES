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

/// <summary>게이트웨이 우선 FDC read E2E(S8) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩).
/// FDC_PARAMETER_GROUP / FDC_PARAMETER / FDC_ALARM_CONFIG / FDC_INTERLOCK_RULE / FDC_ALARM_HISTORY /
/// FDC_INTERLOCK_HISTORY 를 SqliteConnection 직접 INSERT로 시드한 뒤 명명 read 쿼리 라운드트립을 검증한다. + 미인증 401.
/// 실재 컬럼/NOT NULL(감사 컬럼 명시)을 충족한다. 설정 생성(브리지)·실시간 수집/평가/이력 기록(워커)은 본 슬라이스 비대상.</summary>
public sealed class GatewayFdcQueryTests : IClassFixture<GatewayFdcQueryTests.FdcFactory>
{
    private const string Secret = "s8-fdc-gateway-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-fdc-test";
    private readonly FdcFactory _factory;
    public GatewayFdcQueryTests(FdcFactory factory) => _factory = factory;

    public sealed class FdcFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-fdc-e2e-{Guid.NewGuid():N}.db");
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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "fdc-e2e-user") };
        if (permissions.Length == 0)
            claims.Add(new Claim(NexaOne.Common.Security.Permissions.ClaimType, "fdc:read"));
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

    // FDC_PARAMETER_GROUP 1건 시드(실재 컬럼·NOT NULL 충족; 감사 컬럼 명시).
    private void SeedGroup(string groupId, string equipmentId, string groupName, int displayOrder)
        => Exec(@"INSERT INTO FDC_PARAMETER_GROUP
            (GROUP_ID, GROUP_NAME, EQUIPMENT_ID, DESCRIPTION, DISPLAY_ORDER, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @name, @eq, '설명', @ord, 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", groupId);
            cmd.Parameters.AddWithValue("@name", groupName);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@ord", displayOrder);
            cmd.Parameters.AddWithValue("@now", Now());
        });

    // FDC_PARAMETER 1건 시드(NOT NULL: UNIT/LOWER_LIMIT/UPPER_LIMIT/SAMPLING_INTERVAL_MS). 제어한도는 NULL 허용.
    private void SeedParameter(string parameterId, string equipmentId, string? groupId, string name)
        => Exec(@"INSERT INTO FDC_PARAMETER
            (PARAMETER_ID, PARAMETER_NAME, EQUIPMENT_ID, GROUP_ID, UNIT,
             LOWER_LIMIT, UPPER_LIMIT, SAMPLING_INTERVAL_MS, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @name, @eq, @grp, 'degC', 0, 100, 1000, 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", parameterId);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@grp", (object?)groupId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", Now());
        });

    // FDC_ALARM_CONFIG 1건 시드.
    private void SeedAlarmConfig(string alarmConfigId, string equipmentId, string parameterId, string level, string op, decimal threshold)
        => Exec(@"INSERT INTO FDC_ALARM_CONFIG
            (ALARM_CONFIG_ID, EQUIPMENT_ID, PARAMETER_ID, ALARM_LEVEL, OPERATOR, THRESHOLD_VALUE, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @eq, @pid, @lvl, @op, @th, 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", alarmConfigId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@pid", parameterId);
            cmd.Parameters.AddWithValue("@lvl", level);
            cmd.Parameters.AddWithValue("@op", op);
            cmd.Parameters.AddWithValue("@th", threshold);
            cmd.Parameters.AddWithValue("@now", Now());
        });

    // FDC_INTERLOCK_RULE 1건 시드.
    private void SeedRule(string ruleId, string equipmentId, string parameterId, string op, decimal threshold, string action, int priority)
        => Exec(@"INSERT INTO FDC_INTERLOCK_RULE
            (RULE_ID, RULE_NAME, EQUIPMENT_ID, PARAMETER_ID, OPERATOR, THRESHOLD_VALUE, ACTION, PRIORITY, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, '규칙', @eq, @pid, @op, @th, @act, @pri, 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", ruleId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@pid", parameterId);
            cmd.Parameters.AddWithValue("@op", op);
            cmd.Parameters.AddWithValue("@th", threshold);
            cmd.Parameters.AddWithValue("@act", action);
            cmd.Parameters.AddWithValue("@pri", priority);
            cmd.Parameters.AddWithValue("@now", Now());
        });

    // FDC_ALARM_HISTORY 1건 시드. occurredAt은 'yyyy-MM-dd HH:mm:ss'(SQLite TEXT 비교 호환).
    private void SeedAlarmHistory(string alarmId, string alarmConfigId, string equipmentId, string parameterId, DateTime occurredAt)
        => Exec(@"INSERT INTO FDC_ALARM_HISTORY
            (ALARM_ID, ALARM_CONFIG_ID, EQUIPMENT_ID, PARAMETER_ID, ALARM_LEVEL, TRIGGER_VALUE, MESSAGE,
             OCCURRED_AT, IS_CLEARED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @cfg, @eq, @pid, 'Warning', 123.4, '임계 초과', @occ, 0, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", alarmId);
            cmd.Parameters.AddWithValue("@cfg", alarmConfigId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@pid", parameterId);
            cmd.Parameters.AddWithValue("@occ", occurredAt.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@now", Now());
        });

    // FDC_INTERLOCK_HISTORY 1건 시드.
    private void SeedInterlockHistory(string historyId, string ruleId, string equipmentId, string parameterId, DateTime triggeredAt)
        => Exec(@"INSERT INTO FDC_INTERLOCK_HISTORY
            (HISTORY_ID, RULE_ID, EQUIPMENT_ID, PARAMETER_ID, TRIGGER_VALUE, ACTION, MESSAGE,
             TRIGGERED_AT, IS_RESOLVED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @rule, @eq, @pid, 250.0, 'STOP', '인터락 발동', @trg, 0, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", historyId);
            cmd.Parameters.AddWithValue("@rule", ruleId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@pid", parameterId);
            cmd.Parameters.AddWithValue("@trg", triggeredAt.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@now", Now());
        });

    // FDC_COLLECT_DATA 1건 시드(수집 시계열, 감사 컬럼 없음 — COLLECTED_AT만). QUALITY는 DEFAULT 'Good'.
    private void SeedUserParameter(string userId, string equipmentId, string parameterId)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO FDC_USER_PARAMETER (USER_ID, EQUIPMENT_ID, PARAMETER_ID, DISPLAY_SEQUENCE, CREATED_BY, CREATED_AT)
            VALUES (@u, @e, @p, 0, @u, @now)";
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@e", equipmentId);
        cmd.Parameters.AddWithValue("@p", parameterId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private void SeedCollectData(string collectId, string equipmentId, string parameterId, decimal value, DateTime collectedAt)
        => Exec(@"INSERT INTO FDC_COLLECT_DATA
            (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY, LOWER_LIMIT, UPPER_LIMIT)
            VALUES (@id, @eq, @pid, @val, @at, 'Good', 0, 100)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", collectId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@pid", parameterId);
            cmd.Parameters.AddWithValue("@val", value);
            cmd.Parameters.AddWithValue("@at", collectedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        });

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/FDC.ParameterGroupsByEquipment",
            new Dictionary<string, object> { ["equipmentId"] = "ANY" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task ParameterGroupsByEquipment_returns_seeded_groups()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedGroup($"G_{Suffix()}", eq, "온도그룹", 1);
        SeedGroup($"G_{Suffix()}", eq, "압력그룹", 0);

        var rows = await Query("FDC.ParameterGroupsByEquipment", new() { ["equipmentId"] = eq });
        rows.Should().HaveCount(2, "해당 설비의 파라미터그룹 2건이 라운드트립돼야 한다");
        rows.Should().OnlyContain(r => r.ContainsKey("GROUP_NAME"));
        rows[0]["GROUP_NAME"].ToString().Should().Be("압력그룹", "DISPLAY_ORDER 오름차순 정렬(0이 먼저)");
    }

    [Fact]
    public async Task ParametersByEquipment_group_filter_narrows()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var grp = $"G_{Suffix()}";
        SeedParameter($"P_{Suffix()}", eq, grp, "온도");
        SeedParameter($"P_{Suffix()}", eq, null, "압력");

        var all = await Query("FDC.ParametersByEquipment", new() { ["equipmentId"] = eq });
        all.Should().HaveCount(2, "설비의 파라미터 2건 모두 반환");

        var inGroup = await Query("FDC.ParametersByEquipment", new() { ["equipmentId"] = eq, ["groupId"] = grp });
        inGroup.Should().ContainSingle("그룹 필터는 그룹 소속 1건만 반환");
        inGroup[0]["GROUP_ID"].ToString().Should().Be(grp);
    }

    [Fact]
    public async Task AlarmConfigsByEquipment_returns_seeded_configs()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var pid = $"P_{Suffix()}";
        SeedAlarmConfig($"A_{Suffix()}", eq, pid, "Warning", "GT", 80m);
        SeedAlarmConfig($"A_{Suffix()}", eq, pid, "Critical", "GTE", 95m);

        var byEquip = await Query("FDC.AlarmConfigsByEquipment", new() { ["equipmentId"] = eq });
        byEquip.Should().HaveCount(2);
        byEquip.Should().OnlyContain(r => r.ContainsKey("THRESHOLD_VALUE"));

        var byParam = await Query("FDC.AlarmConfigsByEquipment", new() { ["equipmentId"] = eq, ["parameterId"] = pid });
        byParam.Should().HaveCount(2, "동일 파라미터 필터는 2건 모두 반환");
    }

    [Fact]
    public async Task InterlockRulesByEquipment_returns_seeded_rules()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedRule($"R_{Suffix()}", eq, $"P_{Suffix()}", "GT", 200m, "STOP", 1);

        var rows = await Query("FDC.InterlockRulesByEquipment", new() { ["equipmentId"] = eq });
        rows.Should().ContainSingle("해당 설비의 인터락규칙 1건이 라운드트립돼야 한다");
        rows[0]["ACTION"].ToString().Should().Be("STOP");
    }

    [Fact]
    public async Task AlarmHistoryByEquipment_within_range_roundtrip()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var inId = $"AH_{Suffix()}";
        SeedAlarmHistory(inId, "CFG1", eq, "P1", DateTime.UtcNow.AddHours(-1));     // 범위 내
        SeedAlarmHistory($"AH_{Suffix()}", "CFG1", eq, "P1", DateTime.UtcNow.AddDays(-10)); // 범위 밖

        var rows = await Query("FDC.AlarmHistoryByEquipment", new()
        {
            ["equipmentId"] = eq,
            ["from"] = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss"),
            ["to"] = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss"),
        });
        rows.Should().ContainSingle("기간 내 알람 이력 1건만 반환");
        rows[0]["ALARM_ID"].ToString().Should().Be(inId);
    }

    [Fact]
    public async Task InterlockHistoryByEquipment_within_range_roundtrip()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var inId = $"IH_{Suffix()}";
        SeedInterlockHistory(inId, "RULE1", eq, "P1", DateTime.UtcNow.AddHours(-2));     // 범위 내
        SeedInterlockHistory($"IH_{Suffix()}", "RULE1", eq, "P1", DateTime.UtcNow.AddDays(-10)); // 범위 밖

        var rows = await Query("FDC.InterlockHistoryByEquipment", new()
        {
            ["equipmentId"] = eq,
            ["from"] = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss"),
            ["to"] = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss"),
        });
        rows.Should().ContainSingle("기간 내 인터락 이력 1건만 반환");
        rows[0]["HISTORY_ID"].ToString().Should().Be(inId);
        rows[0]["ACTION"].ToString().Should().Be("STOP");
    }

    [Fact]
    public async Task ParameterGroupList_returns_all_without_equipment_filter()
    {
        EnsureSchemaReady();
        var idA = $"G_{Suffix()}";
        var idB = $"G_{Suffix()}";
        SeedGroup(idA, "EQ_" + Suffix(), "온도그룹", 1);   // 서로 다른 설비
        SeedGroup(idB, "EQ_" + Suffix(), "압력그룹", 0);

        var rows = await Query("FDC.ParameterGroupList", new());  // 파라미터 없음 → NULL-guard 전체조회
        var ids = rows.Select(r => r["GROUP_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { idA, idB }, "설비 필터 없이 전체 파라미터그룹이 조회돼야 한다(점등용 전체조회)");
    }

    [Fact]
    public async Task ParameterList_returns_all_without_equipment_filter()
    {
        EnsureSchemaReady();
        var idA = $"P_{Suffix()}";
        var idB = $"P_{Suffix()}";
        SeedParameter(idA, "EQ_" + Suffix(), null, "온도");   // 서로 다른 설비, 그룹 없음
        SeedParameter(idB, "EQ_" + Suffix(), null, "압력");

        var rows = await Query("FDC.ParameterList", new());
        var ids = rows.Select(r => r["PARAMETER_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { idA, idB }, "설비 필터 없이 전체 파라미터가 조회돼야 한다(점등용 전체조회)");
    }

    [Fact]
    public async Task InterlockHistoryList_returns_all_without_equipment_filter()
    {
        EnsureSchemaReady();
        var idA = $"IH_{Suffix()}";
        var idB = $"IH_{Suffix()}";
        SeedInterlockHistory(idA, "RULE1", "EQ_" + Suffix(), "P1", DateTime.UtcNow.AddHours(-2));
        SeedInterlockHistory(idB, "RULE1", "EQ_" + Suffix(), "P1", DateTime.UtcNow.AddDays(-10));

        var rows = await Query("FDC.InterlockHistoryList", new());  // 설비·기간 필터 없이 전체
        var ids = rows.Select(r => r["HISTORY_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { idA, idB }, "설비/기간 필터 없이 전체 인터락 이력이 조회돼야 한다(점등용 전체조회)");
    }

    // ===== SmartUX EES_FDC 점등(Phase 3) 신설 전체조회 — 수집 데이터 차트 / 인터락 규칙. =====

    [Fact]
    public async Task CollectDataList_returns_recent_rows_without_filter()
    {
        EnsureSchemaReady();
        var idA = $"C_{Suffix()}";
        var idB = $"C_{Suffix()}";
        SeedCollectData(idA, "EQ_" + Suffix(), "P_" + Suffix(), 42.0m, DateTime.UtcNow.AddMinutes(-1)); // 서로 다른 설비
        SeedCollectData(idB, "EQ_" + Suffix(), "P_" + Suffix(), 7.5m, DateTime.UtcNow.AddMinutes(-2));

        var rows = await Query("FDC.CollectDataList", new());  // 파라미터 없이 최근 수집 전체(NULL-guard)
        var ids = rows.Select(r => r["COLLECT_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { idA, idB }, "설비/파라미터 필터 없이 최근 수집 시계열이 조회돼야 한다(FDC 데이터 차트 점등)");
        rows.Should().OnlyContain(r => r.ContainsKey("VALUE") && r.ContainsKey("COLLECTED_AT"));
    }

    [Fact]
    public async Task UserParameterList_scopes_to_token_user_and_ignores_spoofed_current_user()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedUserParameter("fdc-e2e-user", eq, "P_MINE");
        SeedUserParameter("someone-else", eq, "P_OTHER");

        var mine = await Query("FDC.UserParameterList", new());
        mine.Select(r => r["PARAMETER_ID"]!.ToString()).Should().Contain("P_MINE")
            .And.NotContain("P_OTHER", "개인화 read는 토큰 사용자로 스코프돼야 한다");

        // 스푸핑 시도 — 클라이언트가 currentUser 파라미터를 보내도 서버 주입이 덮어쓴다.
        var spoof = await Query("FDC.UserParameterList", new() { ["currentUser"] = "someone-else" });
        spoof.Select(r => r["PARAMETER_ID"]!.ToString()).Should().NotContain("P_OTHER",
            "클라이언트 제공 @currentUser는 무시(서버 주입 우선)돼야 한다");
    }

    [Fact]
    public async Task EquipmentParameterStats_groups_per_equipment_for_tool_matching()
    {
        EnsureSchemaReady();
        var param = "P_" + Suffix();
        var eqA = "EQ_" + Suffix();
        var eqB = "EQ_" + Suffix();
        SeedCollectData($"C_{Suffix()}", eqA, param, 10m, DateTime.UtcNow.AddMinutes(-3));
        SeedCollectData($"C_{Suffix()}", eqA, param, 20m, DateTime.UtcNow.AddMinutes(-2));
        SeedCollectData($"C_{Suffix()}", eqB, param, 30m, DateTime.UtcNow.AddMinutes(-1));

        var rows = await Query("FDC.EquipmentParameterStats", new() { ["parameterId"] = param });

        rows.Should().HaveCount(2, "동일성 검정은 파라미터×설비 1행 집계(동종 설비 비교)");
        var a = rows.Single(r => r["EQUIPMENT_ID"]!.ToString() == eqA);
        // 게이트웨이 응답 값은 JsonElement — 문자열 경유 파싱(기존 테스트 관례).
        int.Parse(a["N"]!.ToString()!).Should().Be(2);
        decimal.Parse(a["AVG_VALUE"]!.ToString()!).Should().Be(15.0m);
        decimal.Parse(a["RANGE_VALUE"]!.ToString()!).Should().Be(10.0m);
        // 분산 = AVG(x^2) - AVG(x)^2 = (100+400)/2 - 225 = 25 (SQLite STDDEV 부재 우회식의 정확성 가드)
        decimal.Parse(a["VARIANCE_VALUE"]!.ToString()!).Should().Be(25.0m);
    }

    [Fact]
    public async Task InterlockRuleList_returns_all_without_filter()
    {
        EnsureSchemaReady();
        var eqA = "EQ_" + Suffix();
        var eqB = "EQ_" + Suffix();
        SeedRule($"R_{Suffix()}", eqA, $"P_{Suffix()}", "GT", 200m, "STOP", 1);   // 서로 다른 설비
        SeedRule($"R_{Suffix()}", eqB, $"P_{Suffix()}", "LT", 10m, "SLOWDOWN", 2);

        var rows = await Query("FDC.InterlockRuleList", new());  // 설비 필터 없이 전체 인터락 규칙(파라미터별 상태변경 관리)
        var eqs = rows.Select(r => r["EQUIPMENT_ID"].ToString()).ToList();
        eqs.Should().Contain(new[] { eqA, eqB }, "설비 필터 없이 전체 인터락 규칙이 조회돼야 한다(점등용 전체조회)");
        rows.Should().OnlyContain(r => r.ContainsKey("ACTION") && r.ContainsKey("PRIORITY"));
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
