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
using NexaOne.ServiceContracts.Est;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>EST 얇은 브리지 컨트롤러 HTTP 매핑 검증 — modules OFF + 가짜 IEquipmentStateBridge 주입으로
/// Result→HTTP(200/409/400)·쓰기 권한 403·읽기 200을 Spring/ALC 없이 결정적으로 검증한다.</summary>
public sealed class EstBridgeControllerTests : IClassFixture<EstBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase3c-bridge-e2e-jwt-secret-key-at-least-32b!!";
    private const string Issuer = "nexaone-bridge-test";
    private readonly BridgeFactory _factory;
    public EstBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-bridge-{Guid.NewGuid():N}.db");
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
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IEquipmentStateBridge>(new FakeBridge());
                s.AddSingleton<IEquipmentAlarmBridge>(new FakeAlarmBridge());
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private sealed class FakeBridge : IEquipmentStateBridge
    {
        public Task<IReadOnlyList<EquipmentStateMatrixDto>> GetMatrixAsync(string plantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentStateMatrixDto>>(
                new[] { new EquipmentStateMatrixDto($"{plantId}:IDLE:RUN", plantId, "IDLE", "RUN", true, "RUN", false, "Valid") });
        public Task<IReadOnlyList<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default)
            => GetMatrixAsync(plantId, ct);
        public Task<IReadOnlyList<EquipmentStateDto>> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentStateDto>>(
                new[] { new EquipmentStateDto("EQ1", plantId, "IDLE", DateTime.UtcNow, 1) });
        public Task<Result<EquipmentStateDto>> ChangeStateAsync(string equipmentId, string plantId, string toState,
            string requestedBy, string? reason, string sourceType, int? expectedVersion, CancellationToken ct = default)
            => Task.FromResult(toState switch
            {
                "__conflict__" => Result.Failure<EquipmentStateDto>(Error.Conflict("concurrent")),
                "__invalid__"  => Result.Failure<EquipmentStateDto>(Error.Failure("EPT.InvalidTransition", "not allowed")),
                "__reason__"   => Result.Failure<EquipmentStateDto>(Error.Validation("reason", "reason required")),
                _ => Result.Success(new EquipmentStateDto(equipmentId, plantId, toState, DateTime.UtcNow, (expectedVersion ?? 1) + 1)),
            });
        public Task<IReadOnlyList<EquipmentStateHistoryDto>> GetHistoryAsync(string equipmentId, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentStateHistoryDto>>(
                new[] { new EquipmentStateHistoryDto("H1", equipmentId, "IDLE", "RUN", "RUN", DateTime.UtcNow, "tester", "", "UI", null) });
        public Task<Result<EquipmentStateMatrixDto>> UpsertMatrixAsync(string plantId, string fromStateId, string toStateId,
            bool allowFlag, string? setStateId, bool requireReason, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new EquipmentStateMatrixDto(
                $"{plantId}:{fromStateId}:{toStateId}", plantId, fromStateId, toStateId, allowFlag, setStateId ?? toStateId, requireReason, "Valid")));
    }

    private sealed class FakeAlarmBridge : IEquipmentAlarmBridge
    {
        public Task<Result<EquipmentAlarmDto>> RecordAlarmAsync(
            string alarmId, string equipmentId, string alarmCode, string alarmName, string level, CancellationToken ct = default)
            => Task.FromResult(alarmId switch
            {
                "__invalid__" => Result.Failure<EquipmentAlarmDto>(Error.Validation("alarmId", "Alarm ID is required.")),
                _ => Result.Success(new EquipmentAlarmDto(
                    alarmId, equipmentId, alarmCode, alarmName, level, DateTime.UtcNow, null, null, true)),
            });
        public Task<Result> ClearAlarmAsync(string alarmId, DateTime clearedAt, CancellationToken ct = default)
            => Task.FromResult(alarmId == "__missing__"
                ? Result.Failure(Error.NotFound("EquipmentAlarm", alarmId))
                : Result.Success());
        public Task<IReadOnlyList<EquipmentAlarmDto>> GetActiveAlarmsAsync(string plantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentAlarmDto>>(
                new[] { new EquipmentAlarmDto("A1", "EQ1", "E001", "Overheat", "HIGH", DateTime.UtcNow, null, null, true) });
        public Task<int> GetActiveAlarmCountAsync(CancellationToken ct = default) => Task.FromResult(1);
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task ChangeState_success_returns_200_with_dto()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "RUN", reason = (string?)null, expectedVersion = (int?)1 });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<EquipmentStateDto>();
        dto!.CurrentStateId.Should().Be("RUN");
        dto.StateVersion.Should().Be(2);
    }

    [Fact]
    public async Task ChangeState_conflict_maps_to_409()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "__conflict__", reason = (string?)null, expectedVersion = (int?)1 });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "낙관적 동시성 Conflict는 409로 매핑");
    }

    [Fact]
    public async Task ChangeState_invalid_transition_and_missing_reason_map_to_400()
    {
        var invalid = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "__invalid__", reason = (string?)null, expectedVersion = (int?)null });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "InvalidTransition(Failure)은 400");
        var reason = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "__reason__", reason = (string?)null, expectedVersion = (int?)null });
        reason.StatusCode.Should().Be(HttpStatusCode.BadRequest, "RequireReason(Validation)은 400");
    }

    [Fact]
    public async Task ChangeState_without_est_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "RUN", reason = (string?)null, expectedVersion = (int?)1 });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "est:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task GetStateMatrix_returns_200_for_authenticated_reader()
    {
        var res = await Client().GetAsync("/api/v1/est/state-matrix?plantId=P1");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<EquipmentStateMatrixDto>>();
        rows!.Should().ContainSingle(m => m.FromStateId == "IDLE" && m.ToStateId == "RUN");
    }

    [Fact]
    public async Task GetActiveAlarms_returns_200_for_authenticated_reader()
    {
        var res = await Client().GetAsync("/api/v1/est/alarms?plantId=P1");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<EquipmentAlarmDto>>();
        rows!.Should().ContainSingle(a => a.AlarmId == "A1" && a.IsActive);
    }

    [Fact]
    public async Task GetActiveAlarmCount_returns_200_for_authenticated_reader()
    {
        var res = await Client().GetAsync("/api/v1/est/alarms/count");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<int>()).Should().Be(1);
    }

    [Fact]
    public async Task RecordAlarm_success_returns_200_with_dto()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/alarms",
            new { alarmId = "A2", equipmentId = "EQ1", alarmCode = "E002", alarmName = "Vibration", level = "MED" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<EquipmentAlarmDto>();
        dto!.AlarmId.Should().Be("A2");
        dto.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task RecordAlarm_validation_failure_maps_to_400()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/alarms",
            new { alarmId = "__invalid__", equipmentId = "EQ1", alarmCode = "E002", alarmName = "Vibration", level = "MED" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Validation 실패는 400");
    }

    [Fact]
    public async Task RecordAlarm_without_est_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/est/alarms",
            new { alarmId = "A3", equipmentId = "EQ1", alarmCode = "E003", alarmName = "Door", level = "LOW" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "est:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task ClearAlarm_success_returns_204()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/alarms/A1/clear", new { });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "해제 성공은 204(NoContent)");
    }

    [Fact]
    public async Task ClearAlarm_not_found_maps_to_404()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/alarms/__missing__/clear", new { });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "존재하지 않는 알람 해제는 404");
    }

    [Fact]
    public async Task ClearAlarm_without_est_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/est/alarms/A1/clear", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "est:manage 미보유 쓰기는 403");
    }
}
