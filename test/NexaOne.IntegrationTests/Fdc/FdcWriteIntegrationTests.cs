using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FdcController 쓰기 경로 HTTP 통합 테스트 — 마이그레이션으로 생성되는 FDC 마스터/시계열 테이블
/// (FDC_INTERLOCK_RULE V004 / FDC_PARAMETER V016 / FDC_COLLECT_DATA V017 / FDC_ALARM_CONFIG V020 /
///  FDC_PARAMETER_GROUP V022)이 SQLite에서 실제로 INSERT/SELECT 되는지(테이블 존재 + 방언 + 인가 +
/// 라우팅 + 직렬화)를 end-to-end로 검증한다. 기존 FdcControllerIntegrationTests(읽기 스모크)와 별도 파일.
///
/// 인가: 쓰기 엔드포인트는 perm:fdc:manage(규칙/파라미터/그룹/알람설정) 또는 perm:fdc:control(수집)
/// 정책으로 보호된다. ADMIN("*") 토큰이면 모든 정책을 통과하므로 CreateAuthenticatedClient()를 쓴다.
/// 각 POST 본문은 FdcController의 요청 record(CreateRuleRequest / CreateParameterRequest /
/// CreateParameterGroupRequest / CreateAlarmConfigRequest / RecordDataRequest)와 필드명·형 1:1 정합.
///
/// FK(FDC_*→MDM_EQUIPMENT, FDC_COLLECT_DATA→FDC_PARAMETER)는 테스트 하니스가 PRAGMA foreign_keys=OFF로
/// 부트스트랩하므로 부모(설비) 선행 시드가 불필요하다. 단, 도메인 Create 검증(허용 Operator/Action/
/// AlarmLevel/Quality, LowerLimit&lt;UpperLimit)은 충족해야 하며, collect-data는 서비스가 파라미터를
/// 선조회(RecordDataAsync→GetByIdAsync)하므로 파라미터를 먼저 등록한다.
/// </summary>
public sealed class FdcWriteIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FdcWriteIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── Interlock Rules (FDC_INTERLOCK_RULE, V004 — perm:fdc:manage) ─────────────

    [Fact]
    public async Task CreateInterlockRule_requires_auth()
    {
        // 미인증 → 401: [Authorize] + perm:fdc:manage가 라우팅/모델바인딩보다 먼저 적용되는지 검증.
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/fdc/interlock-rules", new
        {
            ruleId = "ANON-RULE",
            ruleName = "anon",
            equipmentId = "EQ-X",
            parameterId = "P-X",
            @operator = "GT",
            thresholdValue = 1m,
            action = "ALARM",
            priority = 1
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "POST interlock-rules는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task CreateInterlockRule_persists_and_is_retrievable()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:fdc:manage 통과
        const string equipmentId = "FDC-IT-EQ-RULE";
        const string ruleId = "FDC-IT-RULE-1";

        // OPERATOR는 GT/LT/GTE/LTE/EQ, ACTION은 STOP/ALARM/NOTIFY만 도메인 검증 통과(FdcInterlockRule.Create).
        var createResp = await client.PostAsJsonAsync("/api/v1/fdc/interlock-rules", new
        {
            ruleId,
            ruleName = "FDC Test Rule",
            equipmentId,
            parameterId = "FDC-IT-PARAM-RULE",
            @operator = "GT",
            thresholdValue = 100.5m,
            action = "ALARM",
            priority = 1
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"인터록 규칙 등록(FDC_INTERLOCK_RULE INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<RuleDto>();
        created.Should().NotBeNull("POST 응답은 등록된 규칙(Result.Value)이어야 한다");
        created!.Id.Should().Be(ruleId);
        created.Operator.Should().Be("GT");
        created.Action.Should().Be("ALARM");

        // GET interlock-rules?equipmentId — FDC_INTERLOCK_RULE SELECT로 영속화 회수 확인.
        var list = await client.GetFromJsonAsync<List<RuleDto>>(
            $"/api/v1/fdc/interlock-rules?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(r => r.Id == ruleId)
            .Which.ThresholdValue.Should().Be(100.5m);
    }

    // ── Parameters (FDC_PARAMETER, V016 — perm:fdc:manage) ───────────────────────

    [Fact]
    public async Task CreateParameter_persists_and_is_retrievable()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string equipmentId = "FDC-IT-EQ-PARAM";
        const string parameterId = "FDC-IT-PARAM-1";

        // LowerLimit < UpperLimit 불변식(FdcParameter.Create)을 충족해야 한다.
        var createResp = await client.PostAsJsonAsync("/api/v1/fdc/parameters", new
        {
            parameterId,
            parameterName = "FDC Test Parameter",
            equipmentId,
            unit = "degC",
            lowerLimit = 10m,
            upperLimit = 90m
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"파라미터 등록(FDC_PARAMETER INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<ParameterDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(parameterId);
        created.LowerLimit.Should().Be(10m);
        created.UpperLimit.Should().Be(90m);

        var list = await client.GetFromJsonAsync<List<ParameterDto>>(
            $"/api/v1/fdc/parameters?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == parameterId)
            .Which.Unit.Should().Be("degC");
    }

    // ── Parameter Groups (FDC_PARAMETER_GROUP, V022 — perm:fdc:manage) ───────────

    [Fact]
    public async Task CreateParameterGroup_persists_and_is_retrievable()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string equipmentId = "FDC-IT-EQ-GROUP";
        const string groupId = "FDC-IT-GROUP-1";

        // DisplayOrder >= 0(FdcParameterGroup.Create). Description은 nullable.
        var createResp = await client.PostAsJsonAsync("/api/v1/fdc/parameter-groups", new
        {
            groupId,
            groupName = "FDC Test Group",
            equipmentId,
            description = "temperature sensors",
            displayOrder = 1
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"파라미터 그룹 등록(FDC_PARAMETER_GROUP INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<GroupDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(groupId);
        created.GroupName.Should().Be("FDC Test Group");

        var list = await client.GetFromJsonAsync<List<GroupDto>>(
            $"/api/v1/fdc/parameter-groups?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(g => g.Id == groupId)
            .Which.DisplayOrder.Should().Be(1);
    }

    // ── Alarm Configs (FDC_ALARM_CONFIG, V020 — perm:fdc:manage) ─────────────────

    [Fact]
    public async Task CreateAlarmConfig_persists_and_is_retrievable()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string equipmentId = "FDC-IT-EQ-ALARM";
        const string alarmConfigId = "FDC-IT-ALARM-1";

        // AlarmLevel은 Warning/Critical(대소문자 무시 후 표준화), Operator는 GT/LT/GTE/LTE/EQ(FdcAlarmConfig.Create).
        var createResp = await client.PostAsJsonAsync("/api/v1/fdc/alarm-configs", new
        {
            alarmConfigId,
            equipmentId,
            parameterId = "FDC-IT-PARAM-ALARM",
            alarmLevel = "Warning",
            @operator = "GTE",
            threshold = 75m
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"알람 설정 등록(FDC_ALARM_CONFIG INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<AlarmConfigDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(alarmConfigId);
        created.AlarmLevel.Should().Be("Warning");
        created.Operator.Should().Be("GTE");
        // 도메인은 THRESHOLD를 ThresholdValue 프로퍼티로 보관한다(요청 record 필드명은 Threshold).
        created.ThresholdValue.Should().Be(75m);

        var list = await client.GetFromJsonAsync<List<AlarmConfigDto>>(
            $"/api/v1/fdc/alarm-configs?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(c => c.Id == alarmConfigId)
            .Which.ParameterId.Should().Be("FDC-IT-PARAM-ALARM");
    }

    // ── Collect Data (FDC_COLLECT_DATA, V017 — perm:fdc:control) ─────────────────

    [Fact]
    public async Task RecordCollectData_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/fdc/collect-data", new
        {
            collectId = "ANON-DATA",
            equipmentId = "EQ-X",
            parameterId = "P-X",
            value = 1m,
            quality = "Good"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "POST collect-data는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task RecordCollectData_persists_and_is_retrievable()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:fdc:control + perm:fdc:manage 통과
        const string equipmentId = "FDC-IT-EQ-DATA";
        const string parameterId = "FDC-IT-PARAM-DATA";
        const string collectId = "FDC-IT-COLLECT-1";

        // 0) 파라미터 선행 등록 — RecordDataAsync는 파라미터를 GetByIdAsync로 선조회해 한도(LowerLimit/
        //    UpperLimit)를 가져온다. 미존재면 NotFound→400이므로 수집 전에 등록한다.
        var paramResp = await client.PostAsJsonAsync("/api/v1/fdc/parameters", new
        {
            parameterId,
            parameterName = "FDC Collect Param",
            equipmentId,
            unit = "kPa",
            lowerLimit = 0m,
            upperLimit = 200m
        });
        paramResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "수집 전 파라미터 등록(선조회 대상)이 성공해야 한다");

        // 1) 수집 기록 — Quality는 Good/Bad/Uncertain만 허용(FdcCollectData.Create). 인터록 규칙이 없어
        //    EvaluateAsync는 Pass(미발동)를 반환하므로 알람 자동생성 경로는 타지 않는다.
        var recordResp = await client.PostAsJsonAsync("/api/v1/fdc/collect-data", new
        {
            collectId,
            equipmentId,
            parameterId,
            value = 123.45m,
            quality = "Good"
        });
        var recordBody = await recordResp.Content.ReadAsStringAsync();
        recordResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"수집 데이터 기록(FDC_COLLECT_DATA INSERT)이 성공해야 한다. 응답 본문: {recordBody}");

        // POST 응답은 { CollectedData, Interlock } 익명형 — 수집 데이터와 인터록 평가 결과.
        var result = await recordResp.Content.ReadFromJsonAsync<RecordResultDto>();
        result.Should().NotBeNull();
        result!.CollectedData.Should().NotBeNull("응답은 수집된 데이터(Result.Value)를 담아야 한다");
        result.CollectedData!.Id.Should().Be(collectId);
        result.CollectedData.Value.Should().Be(123.45m);
        result.CollectedData.Quality.Should().Be("Good");
        // 값 123.45는 [0,200] 범위 내 → 정상(OOS 아님).
        result.CollectedData.IsOutOfSpec.Should().BeFalse();
        result.Interlock.Should().NotBeNull();
        result.Interlock!.IsTriggered.Should().BeFalse("등록된 인터록 규칙이 없어 미발동이어야 한다");

        // 2) GET collect-data로 회수 확인(FDC_COLLECT_DATA SELECT WHERE PARAMETER_ID AND 시간범위).
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        var list = await client.GetFromJsonAsync<List<CollectDataDto>>(
            $"/api/v1/fdc/collect-data?parameterId={parameterId}&from={from}&to={to}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(d => d.Id == collectId)
            .Which.Value.Should().Be(123.45m);
    }

    // ── DTOs — 컨트롤러가 도메인 엔티티(Result.Value)를 그대로 Ok()로 직렬화하므로(JSON camelCase),
    //    도메인 프로퍼티명과 일치시킨다. Id는 Entity<TId> 식별자.

    private sealed record RuleDto(
        string Id, string RuleName, string EquipmentId, string ParameterId,
        string Operator, decimal ThresholdValue, string Action, int Priority, bool IsActive);

    private sealed record ParameterDto(
        string Id, string ParameterName, string EquipmentId, string? GroupId, string Unit,
        decimal LowerLimit, decimal UpperLimit, bool IsActive);

    private sealed record GroupDto(
        string Id, string GroupName, string EquipmentId, string? Description, int DisplayOrder, bool IsActive);

    private sealed record AlarmConfigDto(
        string Id, string EquipmentId, string ParameterId,
        string AlarmLevel, string Operator, decimal ThresholdValue, bool IsActive);

    private sealed record CollectDataDto(
        string Id, string EquipmentId, string ParameterId, decimal Value,
        string Quality, decimal LowerLimit, decimal UpperLimit, bool IsOutOfSpec);

    private sealed record InterlockDto(bool IsTriggered, string Action, string Message, string? RuleId);

    private sealed record RecordResultDto(CollectDataDto? CollectedData, InterlockDto? Interlock);
}
