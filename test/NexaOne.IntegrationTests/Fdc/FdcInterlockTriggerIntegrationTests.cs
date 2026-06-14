using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FDC 인터록 발동 → EST 알람 자동기록 경로(미검증 핵심)의 HTTP end-to-end 통합 테스트.
///
/// FdcController.RecordData(POST /api/v1/fdc/collect-data)는 수집 후 인터록을 평가하고
/// (FdcInterlockService.EvaluateAsync), 발동 시 Action이 ALARM/STOP이면 EquipmentAlarmService.
/// RecordAlarmAsync로 EST_EQUIPMENT_ALARM에 알람을 자동 생성한다. 기존 FdcWriteIntegrationTests는
/// 인터록 규칙을 등록하지 않아 EvaluateAsync가 항상 Pass(미발동)를 반환 → trigger 분기(컨트롤러
/// 248~268행)를 한 번도 타지 않는다. 이 파일은 그 미검증 분기를 실제로 통과시켜:
///   (1) FDC_INTERLOCK_RULE(V004) ⋈ GetActiveRules SELECT + Evaluate 도메인 로직,
///   (2) trigger 시 EST_EQUIPMENT_ALARM(V003) INSERT(자동 알람),
///   (3) GET /api/v1/est/alarms?plantId — EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT(PLANT_ID) 조인으로
///       자동생성 알람이 활성 목록에 노출되는지
/// 를 SQLite에서 검증한다(테이블 존재 + 방언 + 인증/인가 + 직렬화 + 영속/되읽기).
///
/// 인가: POST collect-data는 perm:fdc:control, POST interlock-rules는 perm:fdc:manage 정책이다.
/// ADMIN("*") 토큰은 둘 다 통과. 미인증 401 / 무관 권한("fdc:read") 403을 collect-data에 단언한다.
///
/// 자동생성 알람 ID 규약(컨트롤러 258~261행): alarmId = $"FDC-{CollectId}",
/// alarmCode = $"INTERLOCK_{Action}", alarmName = $"[FDC] {Message}",
/// level = Action=="STOP" ? "CRITICAL" : "WARNING".
///
/// 활성 알람 조회는 MDM_EQUIPMENT와 PLANT_ID로 조인하므로, 자동 알람이 활성 목록에 노출되려면
/// 동일 설비가 MDM에 선행 등록되어야 한다(운영 FK 무결성과도 정합). 미등록 설비에서 인터록이
/// 발동하면 컨트롤러가 설비 존재를 확인(발동 시에만)해 자동 알람 기록을 생략한다 — 운영(FK ON)에서
/// FK 위반 500을 막는다(아래 unregistered 테스트로 '생략'을 검증).
/// </summary>
public sealed class FdcInterlockTriggerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FdcInterlockTriggerIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 인가: collect-data(perm:fdc:control) ─────────────────────────────────────

    [Fact]
    public async Task RecordCollectData_without_token_returns_401()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/fdc/collect-data", new
        {
            collectId = "ANON-TRIG",
            equipmentId = "EQ-X",
            parameterId = "P-X",
            value = 1m,
            quality = "Good"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "POST collect-data는 토큰 없이 401이어야 한다(perm:fdc:control 정책)");
    }

    [Fact]
    public async Task RecordCollectData_with_unrelated_permission_returns_403()
    {
        // "fdc:read"는 perm:fdc:control과도 "*"와도 불일치 → PermissionAuthorizationHandler가 403.
        var client = _factory.CreateAuthenticatedClient("fdc:read");
        var resp = await client.PostAsJsonAsync("/api/v1/fdc/collect-data", new
        {
            collectId = "FORBIDDEN-TRIG",
            equipmentId = "EQ-X",
            parameterId = "P-X",
            value = 1m,
            quality = "Good"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "perm:fdc:control이 없는 토큰의 POST collect-data는 403이어야 한다");
    }

    // ── 핵심: ALARM 규칙 발동 → EST 알람 자동기록 → 활성목록 노출 ────────────────────

    [Fact]
    public async Task CollectData_breaching_ALARM_rule_triggers_and_autocreates_active_est_alarm()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — control/manage 모두 통과
        const string plantId = "P-FDC-ITRIG";
        const string equipmentId = "FDC-ITRIG-EQ-ALARM";
        const string parameterId = "FDC-ITRIG-PARAM-ALARM";
        const string ruleId = "FDC-ITRIG-RULE-ALARM";
        const string collectId = "FDC-ITRIG-COLLECT-ALARM";

        // 0) 설비 등록 — GET est/alarms는 EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT(PLANT_ID)로 조인하므로
        //    자동 알람이 활성 목록에 노출되려면 동일 PLANT_ID의 설비가 선행 등록돼야 한다.
        await RegisterEquipmentAsync(client, equipmentId, plantId);

        // 1) FDC 파라미터 등록 — RecordDataAsync가 GetByIdAsync로 선조회(미존재면 400). 한도 [0,1000].
        await RegisterParameterAsync(client, parameterId, equipmentId, lower: 0m, upper: 1000m);

        // 2) 인터록 규칙 등록 — GT 100, action="ALARM"(FdcInterlockRule.Create 허용값). FDC_INTERLOCK_RULE INSERT.
        await CreateInterlockRuleAsync(
            client, ruleId, equipmentId, parameterId, op: "GT", threshold: 100m, action: "ALARM", priority: 1);

        // 3) 임계 초과(value=150 > 100) 수집 → 인터록 발동 분기 진입 → EST 알람 자동 INSERT.
        var record = await PostCollectDataAsync(client, collectId, equipmentId, parameterId, value: 150m);

        // 4) 응답의 Interlock 단언 — IsTriggered=true, Action="ALARM".
        record.Interlock.Should().NotBeNull();
        record.Interlock!.IsTriggered.Should().BeTrue(
            "value 150은 GT 100 규칙을 위반하므로 인터록이 발동해야 한다");
        record.Interlock.Action.Should().Be("ALARM",
            "발동 규칙의 Action은 ALARM이어야 한다");
        record.Interlock.RuleId.Should().Be(ruleId,
            "발동 결과는 위반된 규칙의 RuleId를 담아야 한다");
        // 수집값 150은 한도 [0,1000] 내 → OOS 아님(인터록과 spec은 독립).
        record.CollectedData.Should().NotBeNull();
        record.CollectedData!.Value.Should().Be(150m);
        record.CollectedData.IsOutOfSpec.Should().BeFalse();

        // 5) GET est/alarms?plantId — 자동생성 알람이 활성 목록에 노출돼야 한다.
        //    자동 알람 ID 규약: alarmId = "FDC-{collectId}", code = "INTERLOCK_ALARM", level = "WARNING".
        var expectedAlarmId = $"FDC-{collectId}";
        var active = await GetActiveAlarmsAsync(client, plantId);
        var auto = active.Should().ContainSingle(a => a.Id == expectedAlarmId,
            "ALARM 인터록 발동 시 EST_EQUIPMENT_ALARM에 자동 알람이 1건 INSERT되고 활성 목록에 노출돼야 한다")
            .Subject;
        auto.EquipmentId.Should().Be(equipmentId);
        auto.AlarmCode.Should().Be("INTERLOCK_ALARM",
            "ALARM 액션의 자동 알람 코드는 INTERLOCK_ALARM이어야 한다");
        auto.AlarmLevel.Should().Be("WARNING",
            "ALARM 액션의 자동 알람 레벨은 WARNING이어야 한다");
        auto.AlarmName.Should().StartWith("[FDC] ",
            "자동 알람 이름은 [FDC] 접두 + 인터록 메시지여야 한다");
        auto.IsActive.Should().BeTrue("해제 전 자동 알람은 활성 상태(CLEARED_AT IS NULL)여야 한다");
    }

    // ── STOP 분기: action="STOP" → level="CRITICAL", code="INTERLOCK_STOP" ─────────

    [Fact]
    public async Task CollectData_breaching_STOP_rule_autocreates_CRITICAL_est_alarm()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string plantId = "P-FDC-ITRIG-STOP";
        const string equipmentId = "FDC-ITRIG-EQ-STOP";
        const string parameterId = "FDC-ITRIG-PARAM-STOP";
        const string ruleId = "FDC-ITRIG-RULE-STOP";
        const string collectId = "FDC-ITRIG-COLLECT-STOP";

        await RegisterEquipmentAsync(client, equipmentId, plantId);
        await RegisterParameterAsync(client, parameterId, equipmentId, lower: 0m, upper: 1000m);

        // GTE 80, action="STOP" — value 80은 GTE 80을 위반(>=).
        await CreateInterlockRuleAsync(
            client, ruleId, equipmentId, parameterId, op: "GTE", threshold: 80m, action: "STOP", priority: 1);

        var record = await PostCollectDataAsync(client, collectId, equipmentId, parameterId, value: 80m);

        record.Interlock!.IsTriggered.Should().BeTrue("value 80은 GTE 80 규칙을 위반하므로 발동해야 한다");
        record.Interlock.Action.Should().Be("STOP");

        var expectedAlarmId = $"FDC-{collectId}";
        var active = await GetActiveAlarmsAsync(client, plantId);
        var auto = active.Should().ContainSingle(a => a.Id == expectedAlarmId,
            "STOP 인터록 발동 시에도 EST_EQUIPMENT_ALARM에 자동 알람이 INSERT돼야 한다")
            .Subject;
        auto.AlarmCode.Should().Be("INTERLOCK_STOP",
            "STOP 액션의 자동 알람 코드는 INTERLOCK_STOP이어야 한다");
        auto.AlarmLevel.Should().Be("CRITICAL",
            "STOP 액션의 자동 알람 레벨은 CRITICAL이어야 한다");
    }

    // ── 미발동 회귀: 임계 미달이면 알람이 생성되지 않는다 ──────────────────────────────

    [Fact]
    public async Task CollectData_below_threshold_does_not_trigger_or_create_alarm()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string plantId = "P-FDC-ITRIG-PASS";
        const string equipmentId = "FDC-ITRIG-EQ-PASS";
        const string parameterId = "FDC-ITRIG-PARAM-PASS";
        const string ruleId = "FDC-ITRIG-RULE-PASS";
        const string collectId = "FDC-ITRIG-COLLECT-PASS";

        await RegisterEquipmentAsync(client, equipmentId, plantId);
        await RegisterParameterAsync(client, parameterId, equipmentId, lower: 0m, upper: 1000m);
        await CreateInterlockRuleAsync(
            client, ruleId, equipmentId, parameterId, op: "GT", threshold: 100m, action: "ALARM", priority: 1);

        // value 50 ≤ 100 → 규칙 미발동(GT는 strict). 알람 자동생성 경로를 타지 않아야 한다.
        var record = await PostCollectDataAsync(client, collectId, equipmentId, parameterId, value: 50m);

        record.Interlock!.IsTriggered.Should().BeFalse(
            "value 50은 GT 100 규칙을 위반하지 않으므로 미발동이어야 한다");

        var active = await GetActiveAlarmsAsync(client, plantId);
        active.Should().NotContain(a => a.Id == $"FDC-{collectId}",
            "미발동이면 EST 자동 알람이 생성되지 않아야 한다");
    }

    // ── 미등록 설비로 trigger — 자동 알람을 '생략'(기록 안 함) + 수집은 200 (FK 500 방지 회귀) ──

    [Fact]
    public async Task CollectData_triggering_for_unregistered_equipment_skips_alarm_and_returns_200()
    {
        // 회귀: 인터록 발동 시 컨트롤러가 설비(MDM) 존재를 확인하고, 미등록이면 EST 자동 알람 기록을 생략한다.
        // (예전엔 EquipmentAlarmService.RecordAlarmAsync가 FK 사전검증 없이 INSERT → 운영 FK ON에서
        //  FK_EPT_ALARM_EQUIPMENT 위반 500. 수정으로 '발동 시에만' 설비 확인 후 미등록이면 생략한다.)
        // '생략'을 FK-OFF 하니스에서 증명하기 위해: 미등록 상태로 발동시킨 뒤 설비를 '사후 등록'하고도
        // 활성 목록에 알람이 없어야 한다 — 만약 (수정 전처럼) 알람이 기록됐다면 사후 등록으로 조인이
        // 풀려 목록에 나타났을 것이다. 나타나지 않음 = 애초에 기록되지 않음(생략) 증명.
        var client = _factory.CreateAuthenticatedClient();
        const string plantId = "P-FDC-ITRIG-UNREG";
        const string equipmentId = "FDC-ITRIG-EQ-UNREG";   // ⚠ 발동 시점엔 MDM에 미등록
        const string parameterId = "FDC-ITRIG-PARAM-UNREG";
        const string ruleId = "FDC-ITRIG-RULE-UNREG";
        const string collectId = "FDC-ITRIG-COLLECT-UNREG";

        // 파라미터/규칙은 등록(FK OFF라 부모 설비 없이도 INSERT 가능; 도메인 검증만 충족). 설비는 미등록.
        await RegisterParameterAsync(client, parameterId, equipmentId, lower: 0m, upper: 1000m);
        await CreateInterlockRuleAsync(
            client, ruleId, equipmentId, parameterId, op: "GT", threshold: 100m, action: "ALARM", priority: 1);

        // 미등록 설비로 임계 초과 수집 → 인터록은 발동하나 자동 알람은 생략된다. 수집 자체는 200(500 아님).
        var record = await PostCollectDataAsync(client, collectId, equipmentId, parameterId, value: 150m);
        record.Interlock!.IsTriggered.Should().BeTrue(
            "규칙(GT 100)을 위반했으므로 인터록 발동 평가는 정상이어야 한다(알람 생략과 무관)");

        // 사후 등록 — 알람이 기록됐다면 이제 조인이 풀려 목록에 나타날 것이다.
        await RegisterEquipmentAsync(client, equipmentId, plantId);

        var active = await GetActiveAlarmsAsync(client, plantId);
        active.Should().NotContain(a => a.Id == $"FDC-{collectId}",
            "발동 시 설비가 미등록이었으므로 자동 알람은 '생략'됐어야 한다 — 사후 등록 후에도 목록에 없어야 한다" +
            "(있다면 알람이 실제로 기록된 것 = FK 위반 500 위험이 남은 것)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static async Task RegisterEquipmentAsync(HttpClient client, string equipmentId, string plantId)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = $"ITrig Equipment {equipmentId}",
            plantId,
            areaId = "A-ITRIG",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-ITRIG"
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다. 응답 본문: {body}");
    }

    private static async Task RegisterParameterAsync(
        HttpClient client, string parameterId, string equipmentId, decimal lower, decimal upper)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/fdc/parameters", new
        {
            parameterId,
            parameterName = $"ITrig Param {parameterId}",
            equipmentId,
            unit = "unit",
            lowerLimit = lower,
            upperLimit = upper
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC 파라미터 등록(FDC_PARAMETER INSERT)이 성공해야 한다(수집 선조회 대상). 응답 본문: {body}");
    }

    private static async Task CreateInterlockRuleAsync(
        HttpClient client, string ruleId, string equipmentId, string parameterId,
        string op, decimal threshold, string action, int priority)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/fdc/interlock-rules", new
        {
            ruleId,
            ruleName = $"ITrig Rule {ruleId}",
            equipmentId,
            parameterId,
            @operator = op,
            thresholdValue = threshold,
            action,
            priority
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"인터록 규칙 등록(FDC_INTERLOCK_RULE INSERT)이 성공해야 한다. 응답 본문: {body}");
    }

    private static async Task<RecordResultDto> PostCollectDataAsync(
        HttpClient client, string collectId, string equipmentId, string parameterId, decimal value)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/fdc/collect-data", new
        {
            collectId,
            equipmentId,
            parameterId,
            value,
            quality = "Good"
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"수집 데이터 기록 + 인터록 평가(+발동 시 EST 알람 자동기록)가 성공해야 한다. 응답 본문: {body}");
        var record = await resp.Content.ReadFromJsonAsync<RecordResultDto>();
        record.Should().NotBeNull("응답은 { CollectedData, Interlock } 형이어야 한다");
        return record!;
    }

    private static async Task<List<AlarmDto>> GetActiveAlarmsAsync(HttpClient client, string plantId)
    {
        var resp = await client.GetAsync($"/api/v1/est/alarms?plantId={plantId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"활성 알람 조회(EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT SELECT)가 성공해야 한다. 응답 본문: {body}");
        var alarms = await resp.Content.ReadFromJsonAsync<List<AlarmDto>>();
        alarms.Should().NotBeNull();
        return alarms!;
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────────
    // collect-data 응답: { CollectedData, Interlock } 익명형. est/alarms 응답: EquipmentAlarm 도메인(camelCase).

    private sealed record CollectDataDto(
        string Id, string EquipmentId, string ParameterId, decimal Value,
        string Quality, decimal LowerLimit, decimal UpperLimit, bool IsOutOfSpec);

    private sealed record InterlockDto(bool IsTriggered, string Action, string Message, string? RuleId);

    private sealed record RecordResultDto(CollectDataDto? CollectedData, InterlockDto? Interlock);

    private sealed record AlarmDto(
        string Id, string EquipmentId, string AlarmCode, string AlarmName,
        string AlarmLevel, DateTime OccurredAt, DateTime? ClearedAt, long? ElapsedSeconds, bool IsActive);
}
