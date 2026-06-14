using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FdcController 제어/평가/그룹배정의 미검증 경로 HTTP 통합 테스트 — 기존 FdcControllerIntegrationTests(읽기
/// 스모크)·FdcWriteIntegrationTests(쓰기 해피패스)·FdcHistoryIntegrationTests(이력 GET)가 아직 덮지 않은
/// 잔여 GET/평가/배정/설비제어 엔드포인트를 end-to-end로 검증한다.
///
/// 대상 엔드포인트:
///   GET  interlock-rules?equipmentId           ([Authorize] — 미인증 401 + ADMIN 200 빈배열)
///   POST interlock/evaluate                     ([Authorize] — 규칙 등록 후 임계 초과/이하로 IsTriggered 분기)
///   PUT  parameters/{parameterId}/group         (perm:fdc:manage — 배정/해제 라운드트립: GroupId 되읽기)
///   POST equipment/start | stop | abort         (perm:fdc:control — 미인증 401 + 권한없음 403 + ADMIN 비-500)
///
/// 인가 컨벤션(검증된 사실):
///   - JwtService.GenerateAccessToken은 permissions를 Permissions.ClaimType 클레임으로 싣고,
///     PermissionPolicyProvider가 "perm:fdc:control" 정책 → Permission="fdc:control" 요건을 생성하며,
///     PermissionAuthorizationHandler가 정확 일치 또는 와일드카드("*")만 통과시킨다.
///   - 따라서 CreateAuthenticatedClient()는 "*"(ADMIN)로 모든 정책 통과, CreateAuthenticatedClient("fdc:read")는
///     fdc:control/fdc:manage 요건에 대해 403(Forbidden) — 인증은 되지만 인가 실패(WorkflowAuth 패턴과 동일).
///
/// 설비 제어(start/stop/abort)는 PlantController(싱글톤)를 구동한다. 테스트 환경은 수집기 비활성
/// (Fdc:Collector:Enabled=false 기본)이라 머신이 등록되지 않고 StateMachine이 미초기화 상태로 시작한다.
/// 컨트롤러는 미초기화 시 InitializeAsync로 멱등 초기화한다. 동일 클래스 테스트가 싱글톤 PlantController를
/// 공유하고 xUnit이 클래스 내 메서드 순서를 보장하지 않으므로, start/stop은 상태머신 전이 가능 여부에 따라
/// 200(전이 성공) 또는 400(InvalidOperationException→BadRequest)일 수 있다. 본 테스트의 핵심은
/// (1) 인가가 라우팅/핸들러보다 먼저 적용되는지, (2) ADMIN 호출이 누락 의존성/예외 누수로 500이 되지 않는지
/// (outbox 적재는 best-effort로 try/catch됨)이며, 따라서 ADMIN 응답은 "500이 아니고 2xx 또는 400"으로 단언한다.
/// abort는 상태머신 null-safe(TryTransition?.)·빈 머신 스냅샷이라 항상 Ok(200)다.
///
/// FK(FDC_*→MDM_EQUIPMENT)는 테스트 하니스가 PRAGMA foreign_keys=OFF로 부트스트랩하므로 부모(설비) 선행
/// 시드가 불필요하다. request record(EvaluateRequest / AssignParameterGroupRequest / AbortRequest /
/// CreateRuleRequest / CreateParameterRequest / CreateParameterGroupRequest)와 필드명·형 1:1 정합.
/// </summary>
public sealed class FdcControlIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FdcControlIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── GET interlock-rules (FDC_INTERLOCK_RULE, V004 — [Authorize]만, 추가 정책 없음) ──

    [Fact]
    public async Task GetInterlockRules_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/v1/fdc/interlock-rules?equipmentId=EQ-X");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET interlock-rules는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetInterlockRules_returns_ok_empty_for_admin()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/fdc/interlock-rules?equipmentId=FDC-CTL-RULES-NONE");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC_INTERLOCK_RULE SELECT(WHERE EQUIPMENT_ID ORDER BY PARAMETER_ID, PRIORITY)가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<RuleDto>>();
        list.Should().NotBeNull("interlock-rules 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 규칙 목록이어야 한다");
    }

    // ── POST interlock/evaluate (FDC_INTERLOCK_RULE GetActiveRulesAsync, WHERE IS_ACTIVE=1) ──

    [Fact]
    public async Task Evaluate_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/fdc/interlock/evaluate", new
        {
            equipmentId = "EQ-X",
            parameterId = "P-X",
            value = 1m
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] POST interlock/evaluate는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task Evaluate_triggers_when_value_exceeds_threshold_and_passes_when_below()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — interlock-rules 등록(perm:fdc:manage)
        const string equipmentId = "FDC-CTL-EVAL-EQ";
        const string parameterId = "FDC-CTL-EVAL-PARAM";
        const string ruleId = "FDC-CTL-EVAL-RULE";

        // 0) 인터록 규칙 등록 — GT 100 (값 > 100이면 발동). ACTION은 ALARM(허용: STOP/ALARM/NOTIFY).
        var ruleResp = await client.PostAsJsonAsync("/api/v1/fdc/interlock-rules", new
        {
            ruleId,
            ruleName = "Evaluate Threshold Rule",
            equipmentId,
            parameterId,
            @operator = "GT",
            thresholdValue = 100m,
            action = "ALARM",
            priority = 1
        });
        var ruleBody = await ruleResp.Content.ReadAsStringAsync();
        ruleResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"평가 전 인터록 규칙 등록(FDC_INTERLOCK_RULE INSERT)이 성공해야 한다. 응답 본문: {ruleBody}");

        // 1) 임계 초과 — value 150 > 100 → IsTriggered=true, Action=ALARM, RuleId 반환.
        //    EvaluateAsync는 GetActiveRulesAsync(WHERE EQUIPMENT_ID AND PARAMETER_ID AND IS_ACTIVE=1)로
        //    SQLite에서 활성 규칙을 읽어 도메인 Evaluate(value)로 판정한다.
        var overResp = await client.PostAsJsonAsync("/api/v1/fdc/interlock/evaluate", new
        {
            equipmentId,
            parameterId,
            value = 150m
        });
        var overBody = await overResp.Content.ReadAsStringAsync();
        overResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"임계 초과 평가는 200이어야 한다(FDC_INTERLOCK_RULE 활성규칙 SELECT 성공). 응답 본문: {overBody}");

        var triggered = await overResp.Content.ReadFromJsonAsync<InterlockDto>();
        triggered.Should().NotBeNull("evaluate 응답은 InterlockResult여야 한다");
        triggered!.IsTriggered.Should().BeTrue("value 150 > 100(GT)이므로 인터록이 발동해야 한다");
        triggered.Action.Should().Be("ALARM", "발동한 규칙의 ACTION이 반영되어야 한다");
        triggered.RuleId.Should().Be(ruleId, "발동한 규칙의 RULE_ID가 반환되어야 한다(영속화된 규칙 회수 증명)");

        // 2) 임계 이하 — value 50 <= 100 → 미발동(Pass). Action/RuleId는 비어있다.
        var underResp = await client.PostAsJsonAsync("/api/v1/fdc/interlock/evaluate", new
        {
            equipmentId,
            parameterId,
            value = 50m
        });
        var underBody = await underResp.Content.ReadAsStringAsync();
        underResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"임계 이하 평가는 200이어야 한다. 응답 본문: {underBody}");

        var passed = await underResp.Content.ReadFromJsonAsync<InterlockDto>();
        passed.Should().NotBeNull();
        passed!.IsTriggered.Should().BeFalse("value 50은 GT 100을 만족하지 않으므로 미발동(Pass)이어야 한다");
        passed.RuleId.Should().BeNull("미발동(Pass)이면 RuleId는 null이어야 한다");
    }

    // ── PUT parameters/{parameterId}/group (FDC_PARAMETER.GROUP_ID 쓰기 — perm:fdc:manage) ──

    [Fact]
    public async Task AssignParameterGroup_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsJsonAsync(
            "/api/v1/fdc/parameters/P-X/group", new { groupId = "G-X" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]+perm:fdc:manage PUT parameters/{id}/group은 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task AssignParameterGroup_forbidden_without_manage_permission()
    {
        // fdc:read만 보유한 인증 토큰 — perm:fdc:manage 요건 불충족 → 403(인가가 핸들러보다 우선).
        var client = _factory.CreateAuthenticatedClient("fdc:read");
        var resp = await client.PutAsJsonAsync(
            "/api/v1/fdc/parameters/P-X/group", new { groupId = "G-X" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "perm:fdc:manage 권한이 없으면 그룹 배정은 403이어야 한다");
    }

    [Fact]
    public async Task AssignParameterGroup_assigns_then_clears_and_round_trips()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:fdc:manage 통과
        const string equipmentId = "FDC-CTL-GROUP-EQ";
        const string parameterId = "FDC-CTL-GROUP-PARAM";
        const string groupId = "FDC-CTL-GROUP-1";

        // 0) 파라미터 등록 — AssignParameterToGroupAsync는 GetByIdAsync로 선조회 후 UPDATE한다. 미존재면 NotFound→400.
        var paramResp = await client.PostAsJsonAsync("/api/v1/fdc/parameters", new
        {
            parameterId,
            parameterName = "Group Assign Param",
            equipmentId,
            unit = "degC",
            lowerLimit = 0m,
            upperLimit = 100m
        });
        var paramBody = await paramResp.Content.ReadAsStringAsync();
        paramResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"배정 전 파라미터 등록(FDC_PARAMETER INSERT)이 성공해야 한다. 응답 본문: {paramBody}");

        // 1) 그룹 등록(배정 대상). FK는 비강제이나 도메인 일관성을 위해 실제 그룹을 만든다.
        var groupResp = await client.PostAsJsonAsync("/api/v1/fdc/parameter-groups", new
        {
            groupId,
            groupName = "Assign Target Group",
            equipmentId,
            description = "round-trip target",
            displayOrder = 1
        });
        var groupBody = await groupResp.Content.ReadAsStringAsync();
        groupResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"배정 대상 그룹 등록(FDC_PARAMETER_GROUP INSERT)이 성공해야 한다. 응답 본문: {groupBody}");

        // 2) 그룹 배정 — PUT parameters/{id}/group { groupId } → FDC_PARAMETER UPDATE SET GROUP_ID=@groupId.
        var assignResp = await client.PutAsJsonAsync(
            $"/api/v1/fdc/parameters/{parameterId}/group", new { groupId });
        var assignBody = await assignResp.Content.ReadAsStringAsync();
        assignResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"그룹 배정(FDC_PARAMETER GROUP_ID UPDATE)이 성공해야 한다. 응답 본문: {assignBody}");

        // 3) GET parameters로 GroupId 반영 확인(영속화된 GROUP_ID round-trip — UPDATE→SELECT).
        var afterAssign = await client.GetFromJsonAsync<List<ParameterDto>>(
            $"/api/v1/fdc/parameters?equipmentId={equipmentId}");
        afterAssign.Should().NotBeNull();
        afterAssign!.Should().ContainSingle(p => p.Id == parameterId)
            .Which.GroupId.Should().Be(groupId, "배정 후 GET parameters에 GROUP_ID가 반영되어야 한다");

        // 4) 그룹 해제 — PUT { groupId: null } → FDC_PARAMETER UPDATE SET GROUP_ID=NULL.
        var clearResp = await client.PutAsJsonAsync(
            $"/api/v1/fdc/parameters/{parameterId}/group", new { groupId = (string?)null });
        var clearBody = await clearResp.Content.ReadAsStringAsync();
        clearResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"그룹 해제(GROUP_ID=NULL UPDATE)가 성공해야 한다. 응답 본문: {clearBody}");

        // 5) GET parameters로 GroupId null 되읽기 확인.
        var afterClear = await client.GetFromJsonAsync<List<ParameterDto>>(
            $"/api/v1/fdc/parameters?equipmentId={equipmentId}");
        afterClear.Should().NotBeNull();
        afterClear!.Should().ContainSingle(p => p.Id == parameterId)
            .Which.GroupId.Should().BeNull("해제 후 GET parameters에 GROUP_ID가 null로 되읽혀야 한다");
    }

    // ── POST equipment/start | stop | abort (PlantController 수동 제어 — perm:fdc:control) ──

    [Fact]
    public async Task EquipmentStart_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsync("/api/v1/fdc/equipment/start", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]+perm:fdc:control POST equipment/start는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task EquipmentStop_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsync("/api/v1/fdc/equipment/stop", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]+perm:fdc:control POST equipment/stop는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task EquipmentAbort_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/fdc/equipment/abort", new { reason = "anon" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]+perm:fdc:control POST equipment/abort는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task EquipmentControl_forbidden_without_control_permission()
    {
        // fdc:read만 보유 — perm:fdc:control 요건 불충족 → start/stop/abort 모두 403.
        var client = _factory.CreateAuthenticatedClient("fdc:read");

        var startResp = await client.PostAsync("/api/v1/fdc/equipment/start", content: null);
        startResp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "perm:fdc:control 권한이 없으면 equipment/start는 403이어야 한다");

        var stopResp = await client.PostAsync("/api/v1/fdc/equipment/stop", content: null);
        stopResp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "perm:fdc:control 권한이 없으면 equipment/stop은 403이어야 한다");

        var abortResp = await client.PostAsJsonAsync("/api/v1/fdc/equipment/abort", new { reason = "no-perm" });
        abortResp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "perm:fdc:control 권한이 없으면 equipment/abort는 403이어야 한다");
    }

    [Fact]
    public async Task EquipmentAbort_returns_ok_for_admin()
    {
        // abort는 StateMachine null-safe(TryTransition?.)·빈 머신 스냅샷이라 결정적으로 Ok(200).
        // outbox 적재(EnqueueAsync)는 best-effort try/catch이므로 EES_OUTBOX 방언 결함이 있어도 제어는 200을 반환한다.
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:fdc:control 통과
        var resp = await client.PostAsJsonAsync("/api/v1/fdc/equipment/abort", new { reason = "integration-test abort" });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ADMIN의 equipment/abort는 200이어야 한다(상태머신 null-safe, outbox best-effort). 응답 본문: {body}");
    }

    [Fact]
    public async Task EquipmentStart_does_not_500_for_admin()
    {
        // start는 미초기화면 InitializeAsync로 멱등 초기화 후 StartAsync 전이를 시도한다. 싱글톤 PlantController를
        // 공유하는 형제 테스트의 선행 호출로 현재 상태가 Running일 수 있어(Start 전이 불가) 200 또는 400일 수 있다.
        // 핵심: 누락 의존성/예외 누수로 500이 되지 않아야 하고(인가 통과 후), 응답은 2xx 또는 400(BadRequest)이어야 한다.
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:fdc:control 통과
        var resp = await client.PostAsync("/api/v1/fdc/equipment/start", content: null);
        var body = await resp.Content.ReadAsStringAsync();
        var code = (int)resp.StatusCode;

        resp.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            $"ADMIN의 equipment/start는 500(예외 누수)이 아니어야 한다. 응답 본문: {body}");
        ((code >= 200 && code < 300) || code == 400).Should().BeTrue(
            $"equipment/start는 전이 성공(2xx) 또는 상태전이 불가(400 BadRequest)여야 한다. 실제: {code}, 본문: {body}");
    }

    [Fact]
    public async Task EquipmentStop_does_not_500_for_admin()
    {
        // stop은 미초기화/Idle 등에서 Stop 전이가 불가능하면 InvalidOperationException→BadRequest(400)이고,
        // Running 상태면 Stopping 전이 후 200이다(순서 비결정). 핵심은 500이 아니어야 한다는 것.
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:fdc:control 통과
        var resp = await client.PostAsync("/api/v1/fdc/equipment/stop", content: null);
        var body = await resp.Content.ReadAsStringAsync();
        var code = (int)resp.StatusCode;

        resp.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            $"ADMIN의 equipment/stop은 500(예외 누수)이 아니어야 한다. 응답 본문: {body}");
        ((code >= 200 && code < 300) || code == 400).Should().BeTrue(
            $"equipment/stop은 전이 성공(2xx) 또는 상태전이 불가(400 BadRequest)여야 한다. 실제: {code}, 본문: {body}");
    }

    // ── DTOs — 컨트롤러가 도메인 엔티티/InterlockResult를 그대로 Ok()로 직렬화(JSON camelCase).
    //    도메인 프로퍼티명과 일치시킨다(Id는 Entity<TId> 식별자).

    private sealed record RuleDto(
        string Id, string RuleName, string EquipmentId, string ParameterId,
        string Operator, decimal ThresholdValue, string Action, int Priority, bool IsActive);

    private sealed record InterlockDto(bool IsTriggered, string Action, string Message, string? RuleId);

    private sealed record ParameterDto(
        string Id, string ParameterName, string EquipmentId, string? GroupId, string Unit,
        decimal LowerLimit, decimal UpperLimit, bool IsActive);
}
