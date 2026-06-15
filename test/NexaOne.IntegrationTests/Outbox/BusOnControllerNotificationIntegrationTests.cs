using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 §2.5 — 이벤트 버스 기본 활성(OutboxEnabledTestApiFactory) 환경에서 HTTP 컨트롤러 경로를 검증한다.
/// 컨트롤러가 RealtimeNotificationCoordinator를 정상 주입받고(버스 활성 시 직접 SignalR 호출 생략 분기), 엔드포인트가
/// 성공하며, 이벤트가 같은 트랜잭션에 EES_OUTBOX로 기록돼 구독자가 알림을 전달할 수 있음을 종단으로 확인한다.
/// </summary>
public sealed class BusOnControllerNotificationIntegrationTests : IClassFixture<OutboxEnabledTestApiFactory>
{
    private readonly OutboxEnabledTestApiFactory _on;

    public BusOnControllerNotificationIntegrationTests(OutboxEnabledTestApiFactory on) => _on = on;

    private static int OutboxCount(string connectionString, string aggregateId, string eventType)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID = $id AND EVENT_TYPE = $type";
        cmd.Parameters.AddWithValue("$id", aggregateId);
        cmd.Parameters.AddWithValue("$type", eventType);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Record_alarm_endpoint_succeeds_and_writes_outbox_event_when_bus_on()
    {
        var client = _on.CreateAuthenticatedClient();   // ADMIN("*") — perm:est:manage 충족
        var resp = await client.PostAsJsonAsync("/api/v1/est/alarms", new
        {
            alarmId = "AL-BUSON-1",
            equipmentId = "EQ-BUSON-1",
            alarmCode = "OVERTEMP",
            alarmName = "[bus-on] 컨트롤러 경로 검증",
            level = "CRITICAL"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "버스 활성 시에도 컨트롤러가 RealtimeNotificationCoordinator를 주입받아 정상 200을 반환해야 한다");

        OutboxCount(_on.ConnectionString, "EQ-BUSON-1", "EquipmentAlarmRaised").Should().Be(1,
            "알람 기록이 같은 트랜잭션에 EquipmentAlarmRaised 이벤트로 적재돼야 한다(구독자가 알림 전달)");
    }
}
