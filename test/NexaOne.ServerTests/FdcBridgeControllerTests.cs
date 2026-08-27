using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Common;
using NexaOne.ServiceContracts.Fdc;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>FDC 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008, S8) — modules OFF + 가짜 IFdcBridge 주입으로
/// 권한(403/200)·생성(200·검증 400)을 Spring/ALC 없이 결정적으로 검증한다. 파라미터그룹·알람설정·인터락규칙
/// 3개 비-실시간 설정 생성 경로(fdc:manage 게이트)를 커버한다. 실시간 수집/평가/제어는 워커 소유라 비대상(ADR-006).</summary>
public sealed class FdcBridgeControllerTests : IClassFixture<FdcBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-fdc-bridge-e2e-jwt-secret-key-at-least-32b!";
    private const string Issuer = "nexaone-fdc-bridge-test";
    private readonly BridgeFactory _factory;
    public FdcBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-fdc-bridge-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.ConfigureTestServices(s => s.AddSingleton<IFdcBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // 가짜 브리지 — 생성은 도메인 검증 실패 입력(잘못된 레벨/연산자/빈 action key·역전 한도)이면 Validation Error를
    // 흉내내 400으로 매핑되는지 본다. 정상 입력이면 스냅샷 DTO를 성공으로 반환한다.
    private sealed class FakeBridge : IFdcBridge
    {
        public Task<Result<FdcParameterGroupDto>> CreateParameterGroupAsync(
            string groupId, string groupName, string equipmentId, string? description, int displayOrder, CancellationToken ct = default)
            => Task.FromResult(displayOrder >= 0 && !string.IsNullOrWhiteSpace(groupName)
                ? Result.Success(new FdcParameterGroupDto(groupId, groupName, equipmentId, description, displayOrder, true))
                : Result.Failure<FdcParameterGroupDto>(Error.Validation(nameof(displayOrder), "Display order must not be negative.")));

        public Task<Result<FdcAlarmConfigDto>> CreateAlarmConfigAsync(
            string alarmConfigId, string equipmentId, string parameterId, string alarmLevel, string @operator,
            decimal threshold, CancellationToken ct = default)
            => Task.FromResult(alarmLevel is "Warning" or "Critical" && @operator is "GT" or "LT" or "GTE" or "LTE" or "EQ"
                ? Result.Success(new FdcAlarmConfigDto(alarmConfigId, equipmentId, parameterId, alarmLevel, @operator, threshold, true))
                : Result.Failure<FdcAlarmConfigDto>(Error.Validation(nameof(alarmLevel), "Alarm level must be Warning or Critical.")));

        public Task<Result<FdcInterlockRuleDto>> CreateInterlockRuleAsync(
            string ruleId, string ruleName, string equipmentId, string parameterId, string @operator,
            decimal threshold, string action, int priority, CancellationToken ct = default)
            => Task.FromResult(!string.IsNullOrWhiteSpace(action) && action.Length <= 200
                && @operator is "GT" or "LT" or "GTE" or "LTE" or "EQ"
                ? Result.Success(new FdcInterlockRuleDto(ruleId, ruleName, equipmentId, parameterId, @operator, threshold, action, priority, true))
                : Result.Failure<FdcInterlockRuleDto>(Error.Validation(nameof(action), "Action key is required and must be 200 characters or fewer.")));

        // 가상 이벤트 평가 — id 규약: "MISSING"→404, "BADFORMULA"→400(수식 오류), 그 외 On/전이 성공.
        public Task<Result<VirtualEventEvaluationDto>> EvaluateVirtualEventAsync(
            string equipmentId, string eventId, CancellationToken ct = default)
            => Task.FromResult(eventId switch
            {
                "MISSING" => Result.Failure<VirtualEventEvaluationDto>(Error.NotFound("VirtualEvent", eventId)),
                "BADFORMULA" => Result.Failure<VirtualEventEvaluationDto>(
                    Error.Validation("VirtualEvent.FormulaInvalid", "파라미터 'TEMP'의 최신 수집 값이 없습니다.")),
                _ => Result.Success(new VirtualEventEvaluationDto(equipmentId, eventId, "과열 감지", true, true, new DateTime(2026, 7, 3))),
            });
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "fdc-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    // ── 권한 게이트 ──

    [Fact]
    public async Task CreateParameterGroup_without_fdc_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/fdc/parameter-groups",
            new { groupId = "G1", groupName = "온도그룹", equipmentId = "EQ1", description = (string?)null, displayOrder = 0 });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "fdc:manage 미보유 쓰기는 403(fdc:read·fdc:control로는 불가)");
    }

    [Fact]
    public async Task CreateAlarmConfig_without_fdc_manage_is_forbidden()
    {
        var res = await Client("fdc:control").PostAsJsonAsync("/api/v1/fdc/alarm-configs",
            new { alarmConfigId = "A1", equipmentId = "EQ1", parameterId = "P1", alarmLevel = "Warning", @operator = "GT", threshold = 100m });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "fdc:control은 설정관리 권한이 아니다 — 403");
    }

    [Fact]
    public async Task CreateInterlockRule_without_token_is_forbidden()
    {
        var res = await Client().PostAsJsonAsync("/api/v1/fdc/interlock-rules",
            new { ruleId = "R1", ruleName = "과열정지", equipmentId = "EQ1", parameterId = "P1", @operator = "GT", threshold = 200m, action = "STOP", priority = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "무권한 쓰기는 403");
    }

    // ── 파라미터그룹 ──

    [Fact]
    public async Task CreateParameterGroup_with_fdc_manage_returns_200_with_dto()
    {
        var res = await Client("fdc:manage").PostAsJsonAsync("/api/v1/fdc/parameter-groups",
            new { groupId = "G1", groupName = "온도그룹", equipmentId = "EQ1", description = "설명", displayOrder = 0 });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<FdcParameterGroupDto>();
        dto!.GroupId.Should().Be("G1");
        dto.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateParameterGroup_negative_display_order_maps_to_400()
    {
        var res = await Client("fdc:manage").PostAsJsonAsync("/api/v1/fdc/parameter-groups",
            new { groupId = "G1", groupName = "온도그룹", equipmentId = "EQ1", description = (string?)null, displayOrder = -1 });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "도메인 검증 실패(Validation)는 400");
    }

    // ── 알람설정 ──

    [Fact]
    public async Task CreateAlarmConfig_with_fdc_manage_returns_200_with_dto()
    {
        var res = await Client("fdc:manage").PostAsJsonAsync("/api/v1/fdc/alarm-configs",
            new { alarmConfigId = "A1", equipmentId = "EQ1", parameterId = "P1", alarmLevel = "Critical", @operator = "GTE", threshold = 150m });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<FdcAlarmConfigDto>();
        dto!.AlarmConfigId.Should().Be("A1");
        dto.AlarmLevel.Should().Be("Critical");
    }

    [Fact]
    public async Task CreateAlarmConfig_invalid_level_maps_to_400()
    {
        var res = await Client("fdc:manage").PostAsJsonAsync("/api/v1/fdc/alarm-configs",
            new { alarmConfigId = "A1", equipmentId = "EQ1", parameterId = "P1", alarmLevel = "Fatal", @operator = "GT", threshold = 100m });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "허용되지 않은 알람 레벨(Validation)은 400");
    }

    // ── 인터락규칙 ──

    [Fact]
    public async Task CreateInterlockRule_with_fdc_manage_returns_200_with_dto()
    {
        var res = await Client("fdc:manage").PostAsJsonAsync("/api/v1/fdc/interlock-rules",
            new { ruleId = "R1", ruleName = "과열정지", equipmentId = "EQ1", parameterId = "P1", @operator = "GT", threshold = 200m, action = "STOP", priority = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<FdcInterlockRuleDto>();
        dto!.RuleId.Should().Be("R1");
        dto.Action.Should().Be("STOP");
    }

    [Fact]
    public async Task CreateInterlockRule_missing_action_key_maps_to_400()
    {
        var res = await Client("fdc:manage").PostAsJsonAsync("/api/v1/fdc/interlock-rules",
            new { ruleId = "R1", ruleName = "과열정지", equipmentId = "EQ1", parameterId = "P1", @operator = "GT", threshold = 200m, action = " ", priority = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "비어 있는 프로젝트 action key(Validation)는 400");
    }

    [Fact]
    public async Task Admin_wildcard_permission_passes_manage_gate()
    {
        var res = await Client("*").PostAsJsonAsync("/api/v1/fdc/parameter-groups",
            new { groupId = "G2", groupName = "압력그룹", equipmentId = "EQ1", description = (string?)null, displayOrder = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.OK, "와일드카드(*) 권한은 fdc:manage 게이트를 통과한다");
    }

    // ── 가상 이벤트 수동 평가(V067→V069 평가 엔진) ──

    [Fact]
    public async Task EvaluateVirtualEvent_without_fdc_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsync("/api/v1/fdc/virtual-events/EQ1/VE-1/evaluate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EvaluateVirtualEvent_success_returns_state_and_transition()
    {
        var res = await Client("fdc:manage").PostAsync("/api/v1/fdc/virtual-events/EQ1/VE-1/evaluate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<VirtualEventEvaluationDto>();
        dto!.IsOn.Should().BeTrue();
        dto.Changed.Should().BeTrue("전이 여부(이력 기록)가 응답에 실려야 한다");
    }

    [Fact]
    public async Task EvaluateVirtualEvent_missing_definition_maps_to_404()
    {
        var res = await Client("fdc:manage").PostAsync("/api/v1/fdc/virtual-events/EQ1/MISSING/evaluate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EvaluateVirtualEvent_formula_error_maps_to_400()
    {
        var res = await Client("fdc:manage").PostAsync("/api/v1/fdc/virtual-events/EQ1/BADFORMULA/evaluate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "수식 오류/값 부재(Validation)는 400 — 조용한 false 금지");
    }
}
