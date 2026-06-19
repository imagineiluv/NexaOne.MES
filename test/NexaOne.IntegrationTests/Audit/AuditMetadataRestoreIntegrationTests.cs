using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.FDC.Application.Fdc;

namespace NexaOne.IntegrationTests.Audit;

/// <summary>
/// 읽기경로 감사 메타데이터 복원 회귀 안전망(AuditableEntity.RestoreAudit) — 영속된 CREATED_BY/CREATED_AT/
/// UPDATED_BY/UPDATED_AT가 되읽기에서 재생성·리셋되지 않고 행값 그대로 복원되는지 SQLite로 실증한다.
/// 과거: Row.ToDomain이 감사필드를 복원하지 않아 CreatedAt이 매 읽기마다 UtcNow로 재생성되고 CreatedBy=""·
/// UpdatedBy/At=null로 리셋됐다(도메인 객체를 직접 직렬화하는 응답에서 노출). 24개 엔티티 일괄 수정의 대표 검증:
/// Restore-확장 케이스(MaintenancePlan)와 신규-Restore 케이스(FdcParameter, 기존 Create+뮤테이터 대체)를 함께 고정한다.
/// 하니스: TestApiFactory(클래스별 고유 SQLite 임시 DB, FK OFF). 고유 ID로 클래스 내 병렬 격리.
/// </summary>
public sealed class AuditMetadataRestoreIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public AuditMetadataRestoreIntegrationTests(TestApiFactory factory) => _factory = factory;

    // 읽은 시각(오늘)과 명확히 구분되는 과거 감사 타임스탬프 — 재생성되면 .Date 단언이 깨진다.
    private static readonly DateTime CreatedAtSeed = new(2020, 3, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAtSeed = new(2021, 7, 22, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MaintenancePlan_read_restores_persisted_audit_metadata()
    {
        const string id = "AUDIT-MP-1";
        using (var conn = new SqliteConnection(_factory.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO CMMS_MAINTENANCE_PLAN
                (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE, SCHEDULED_DATE,
                 ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ($id, '감사검증계획', 'EQ-AUDIT-MP', 'PM', 'Monthly', $sched,
                 1.0, 'USER-A', 'Planned', $cby, $cat, $uby, $uat)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$sched", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("$cby", "ORIG-CREATOR");
            cmd.Parameters.AddWithValue("$cat", CreatedAtSeed);
            cmd.Parameters.AddWithValue("$uby", "LAST-EDITOR");
            cmd.Parameters.AddWithValue("$uat", UpdatedAtSeed);
            cmd.ExecuteNonQuery();
        }

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>();
        var reread = await repo.GetByIdAsync(id);

        reread.Should().NotBeNull();
        reread!.CreatedBy.Should().Be("ORIG-CREATOR", "영속된 CREATED_BY가 복원돼야 한다(빈문자열로 리셋 금지)");
        reread.CreatedAt.Date.Should().Be(new DateTime(2020, 3, 10),
            "영속된 CREATED_AT이 복원돼야 한다 — 읽은 시각(오늘)으로 재생성되면 감사 메타데이터 손실 버그다");
        reread.UpdatedBy.Should().Be("LAST-EDITOR", "영속된 UPDATED_BY가 복원돼야 한다(null로 리셋 금지)");
        reread.UpdatedAt.Should().NotBeNull();
        reread.UpdatedAt!.Value.Date.Should().Be(new DateTime(2021, 7, 22), "영속된 UPDATED_AT이 복원돼야 한다");
    }

    [Fact]
    public async Task FdcParameter_read_restores_audit_and_business_state()
    {
        const string id = "AUDIT-FP-1";
        using (var conn = new SqliteConnection(_factory.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO FDC_PARAMETER
                (PARAMETER_ID, PARAMETER_NAME, EQUIPMENT_ID, GROUP_ID, UNIT, LOWER_LIMIT, UPPER_LIMIT,
                 LOWER_CONTROL_LIMIT, UPPER_CONTROL_LIMIT, SAMPLING_INTERVAL_MS, IS_ACTIVE,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ($id, '감사검증파라미터', 'EQ-AUDIT-FP', 'GRP-1', '℃', 0, 100,
                 10, 90, 500, 0, $cby, $cat, $uby, $uat)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$cby", "ORIG-CREATOR");
            cmd.Parameters.AddWithValue("$cat", CreatedAtSeed);
            cmd.Parameters.AddWithValue("$uby", "LAST-EDITOR");
            cmd.Parameters.AddWithValue("$uat", UpdatedAtSeed);
            cmd.ExecuteNonQuery();
        }

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFdcParameterRepository>();
        var reread = await repo.GetByIdAsync(id);

        reread.Should().NotBeNull();
        // 감사 메타데이터 복원(신규 Restore 경로 — 기존 Create+뮤테이터 대체)
        reread!.CreatedBy.Should().Be("ORIG-CREATOR");
        reread.CreatedAt.Date.Should().Be(new DateTime(2020, 3, 10),
            "신규 Restore 경로도 CREATED_AT을 보존해야 한다(Create 경로는 UtcNow로 재생성했음)");
        reread.UpdatedBy.Should().Be("LAST-EDITOR");
        // 비즈니스 상태도 기존 Create+뮤테이터 경로와 동일하게 보존(그룹·한도·제어한도·비활성)
        reread.GroupId.Should().Be("GRP-1", "GROUP_ID가 복원돼야 한다");
        reread.LowerLimit.Should().Be(0m);
        reread.UpperLimit.Should().Be(100m);
        reread.LowerControlLimit.Should().Be(10m, "제어한도(LCL)가 복원돼야 한다");
        reread.UpperControlLimit.Should().Be(90m, "제어한도(UCL)가 복원돼야 한다");
        reread.IsActive.Should().BeFalse("IS_ACTIVE=0이 복원돼야 한다");
    }
}
