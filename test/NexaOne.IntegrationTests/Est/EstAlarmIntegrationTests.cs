using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Est;

/// <summary>
/// EST 설비알람 기록→해제 라운드트립 HTTP 통합 테스트 — V003 마이그레이션으로 생성되는
/// EST_EQUIPMENT_ALARM 테이블이 SQLite에서 실제로 동작하는지(테이블 존재 + 방언 INSERT/UPDATE/SELECT-JOIN
/// + 인증 + 직렬화 + 영속)를 end-to-end로 검증한다.
///
/// 활성 알람 조회(GetActiveAlarmsAsync)는 EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT(PLANT_ID)로 조인하므로
/// 알람이 활성 목록에 노출되려면 동일 PLANT_ID의 설비가 선행 등록되어야 한다(운영 FK 무결성과도 정합).
/// 시나리오: 설비 등록 → RecordAlarm(POST) → GetActiveAlarms(노출 확인) → ClearAlarm(PUT, ClearedAt)
///           → GetActiveAlarms(활성목록서 제외). UPDATE(CLEARED_AT) 영속화와 방언을 실증한다.
/// </summary>
public sealed class EstAlarmIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public EstAlarmIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RecordAlarm_requires_authentication_returns_401()
    {
        // 미인증(토큰 없음) 클라이언트 — [Authorize] + perm:est:manage 정책으로 401이어야 한다.
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/est/alarms", new
        {
            alarmId = "ALM-UNAUTH-1",
            equipmentId = "EQ-ANY",
            alarmCode = "E-001",
            alarmName = "Unauthorized probe",
            level = "CRITICAL"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 알람 기록 요청은 401이어야 한다");
    }

    [Fact]
    public async Task RecordAlarm_then_clear_roundtrips_through_active_alarm_list()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — perm:est:manage 정책 통과
        const string plantId = "P-ALM";
        const string equipmentId = "EQ-ALM-1";
        const string alarmId = "ALM-ALM-1";

        // 0) 설비 등록 — GetActiveAlarms는 EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT(PLANT_ID)로 조인하므로
        //    동일 PLANT_ID의 설비가 없으면 기록된 알람이 활성 목록에 노출되지 않는다(운영 FK와도 정합).
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "Alarm Test Equipment",
            plantId,
            areaId = "A-ALM",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-ALM"
        });
        var equipBody = await equipResp.Content.ReadAsStringAsync();
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다. 응답 본문: {equipBody}");

        // 1) 알람 기록(EST_EQUIPMENT_ALARM INSERT) — V003 테이블 존재 + 방언 INSERT + DateTime 직렬화 실증.
        var recordResp = await client.PostAsJsonAsync("/api/v1/est/alarms", new
        {
            alarmId,
            equipmentId,
            alarmCode = "E-OVERHEAT",
            alarmName = "Chamber overheat",
            level = "CRITICAL"
        });
        var recordBody = await recordResp.Content.ReadAsStringAsync();
        recordResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"알람 기록(EST_EQUIPMENT_ALARM INSERT)이 성공해야 한다. 응답 본문: {recordBody}");

        // 2) 활성 알람 조회(EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT SELECT) — 방금 기록한 알람이 노출돼야 한다.
        var activeAfterRecord = await GetActiveAlarmsAsync(client, plantId);
        activeAfterRecord.Should().ContainSingle(a => a.Id == alarmId,
            "기록 직후 알람은 활성 목록(CLEARED_AT IS NULL)에 노출돼야 한다")
            .Which.IsActive.Should().BeTrue("해제 전 알람은 활성 상태여야 한다");

        // 3) 알람 해제(EST_EQUIPMENT_ALARM UPDATE — CLEARED_AT/ELAPSED_SECONDS) — 방언 UPDATE + 영속화 실증.
        var clearedAt = DateTime.UtcNow;
        var clearResp = await client.PutAsJsonAsync($"/api/v1/est/alarms/{alarmId}/clear", new
        {
            clearedAt
        });
        var clearBody = await clearResp.Content.ReadAsStringAsync();
        clearResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"알람 해제(UPDATE CLEARED_AT)는 204 No Content여야 한다. 응답 본문: {clearBody}");

        // 4) 활성 알람 재조회 — 해제된 알람은 활성 목록에서 제외돼야 한다(CLEARED_AT 영속화 확인).
        var activeAfterClear = await GetActiveAlarmsAsync(client, plantId);
        activeAfterClear.Should().NotContain(a => a.Id == alarmId,
            "해제 후 알람은 활성 목록(CLEARED_AT IS NULL)에서 제외돼야 한다 — CLEARED_AT UPDATE가 영속화됐다는 증거");
    }

    private static async Task<List<AlarmDto>> GetActiveAlarmsAsync(HttpClient client, string plantId)
    {
        var resp = await client.GetAsync($"/api/v1/est/alarms?plantId={plantId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"활성 알람 조회(SELECT JOIN)가 성공해야 한다. 응답 본문: {body}");
        var alarms = await resp.Content.ReadFromJsonAsync<List<AlarmDto>>();
        alarms.Should().NotBeNull();
        return alarms!;
    }

    private sealed record AlarmDto(
        string Id, string EquipmentId, string AlarmCode, string AlarmName,
        string AlarmLevel, DateTime OccurredAt, DateTime? ClearedAt, long? ElapsedSeconds, bool IsActive);
}
