using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.CMMS.Application.Cmms;

namespace NexaOne.IntegrationTests.CMMS;

/// <summary>
/// MaintenancePlanRepository.GetDueAsync(asOf) 도래 판정을 SQLite에서 실증한다.
/// 도래 술어 = SCHEDULED_DATE &lt;= asOf AND STATUS NOT IN (Completed, Cancelled)
/// (MaintenancePlanRepository.cs:47-49 근거). STATUS는 enum명 문자열로 저장된다(동 파일 :109, status.ToString()).
/// CMMS_MAINTENANCE_PLAN의 NOT NULL 컬럼은 V027__CMMS_PLAN_SPAREPART.sql:12-24를 빠짐없이 채운다.
/// SCHEDULED_DATE는 DATETIME2→TEXT 매핑(SqliteSchemaInitializer.cs:90)이라, 리포가 비교하는 @asOf(DateTime 파라미터)와
/// 동일 바인딩 형식이 되도록 시드도 DateTime 파라미터로 INSERT한다(수동 포맷 불일치 회피).
/// </summary>
public sealed class CmmsMaintenanceDueIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public CmmsMaintenanceDueIntegrationTests(TestApiFactory factory) => _factory = factory;

    // 모든 NOT NULL 컬럼(V027:12-24)을 채워 한 계획 행을 직접 시드한다. STATUS는 enum명 문자열로,
    // SCHEDULED_DATE는 DateTime 파라미터로 바인딩(리포의 @asOf와 동일 TEXT 형식·정렬 호환).
    private void SeedPlan(string planId, DateTime scheduledDate, string status)
    {
        using var conn = new SqliteConnection(_factory.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO CMMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE,
             SCHEDULED_DATE, ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            ($id, $name, $equip, $ptype, $ctype,
             $sched, $dur, $assignee, $status,
             $cby, $cat, $uby, $uat)";
        cmd.Parameters.AddWithValue("$id", planId);
        cmd.Parameters.AddWithValue("$name", "정비계획-" + planId);
        cmd.Parameters.AddWithValue("$equip", "EQ-DUE-" + planId);
        cmd.Parameters.AddWithValue("$ptype", "PM");
        cmd.Parameters.AddWithValue("$ctype", "Monthly");
        cmd.Parameters.AddWithValue("$sched", scheduledDate);
        cmd.Parameters.AddWithValue("$dur", 1.0m);
        cmd.Parameters.AddWithValue("$assignee", "USER-DUE");
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$cby", "TEST");
        cmd.Parameters.AddWithValue("$cat", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("$uby", "TEST");
        cmd.Parameters.AddWithValue("$uat", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task GetDueAsync_returns_only_past_and_active_plans()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>();

        // 클래스 병렬 실행 간 간섭 방지 — 시드한 고유 PLAN_ID로만 단언한다(공유 테이블).
        var asOf = DateTime.UtcNow;
        const string due = "DUE-A-PAST-ACTIVE";       // (a) 과거 + Planned → 도래로 포함
        const string completed = "DUE-B-PAST-DONE";    // (b) 과거 + Completed → 제외
        const string future = "DUE-C-FUTURE-ACTIVE";   // (c) 미래 + Planned → 미도래로 제외

        SeedPlan(due, asOf.AddDays(-1), "Planned");
        SeedPlan(completed, asOf.AddDays(-1), "Completed");
        SeedPlan(future, asOf.AddDays(+1), "Planned");

        var result = await repo.GetDueAsync(asOf);
        var ids = result.Select(p => p.Id).ToList();

        ids.Should().Contain(due, "과거 예정 + 진행가능(Planned) 계획은 도래로 포함돼야 한다");
        ids.Should().NotContain(completed, "완료(Completed) 계획은 STATUS NOT IN 술어로 제외돼야 한다");
        ids.Should().NotContain(future, "미래 예정 계획은 SCHEDULED_DATE <= asOf 위반으로 제외돼야 한다");
    }
}
