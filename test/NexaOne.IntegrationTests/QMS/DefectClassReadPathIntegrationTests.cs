using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.QMS.Application.Qms;

namespace NexaOne.IntegrationTests.QMS;

/// <summary>
/// DefectClassRepository 읽기경로 상태손실 회귀 안전망 — 영속된 소프트삭제 상태(IS_DELETED/DELETED_AT)가
/// 되읽기에서 충실히 복원되는지 SQLite로 실증한다. 과거 버그: ToDomain()이 Create로 재구성한 뒤 비활성 행에
/// Deactivate()를 호출했는데, Deactivate가 IsActive=false뿐 아니라 IsDeleted=true·DeletedAt=UtcNow까지 결합
/// 설정해 (1) 비활성·미삭제 행이 삭제로 둔갑하고 (2) 영속 삭제시각이 읽은 시각으로 덮어써졌다. GetByIdAsync는
/// IS_DELETED 필터가 없어 이 손상이 호출자에게 그대로 노출됐다. 수정은 DefectClass.Restore(...)로 세 플래그를
/// 행값 그대로 독립 복원한다(읽기경로 Restore 패턴). 시드한 영속 상태→GET 단언으로 그 차이를 고정한다.
/// 하니스: TestApiFactory(클래스별 고유 SQLite 임시 DB). 시드 행은 고유 ID라 클래스 내 병렬과 무관.
/// </summary>
public sealed class DefectClassReadPathIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public DefectClassReadPathIntegrationTests(TestApiFactory factory) => _factory = factory;

    private void Seed(string id, bool isActive, bool isDeleted, DateTime? deletedAt)
    {
        using var conn = new SqliteConnection(_factory.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO QMS_DEFECT_CLASS
            (DEFECT_CLASS_ID, DEFECT_CLASS_NAME, DESCRIPTION, SEVERITY, IS_ACTIVE, IS_DELETED, DELETED_AT,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            ($id, $name, $desc, $sev, $active, $deleted, $deletedAt,
             $cby, $cat, $uby, $uat)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", "부적합유형-" + id);
        cmd.Parameters.AddWithValue("$desc", "read-path 회귀 시드");
        cmd.Parameters.AddWithValue("$sev", "Major");
        cmd.Parameters.AddWithValue("$active", isActive);
        cmd.Parameters.AddWithValue("$deleted", isDeleted);
        cmd.Parameters.AddWithValue("$deletedAt", (object?)deletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cby", "TEST");
        cmd.Parameters.AddWithValue("$cat", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("$uby", "TEST");
        cmd.Parameters.AddWithValue("$uat", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task GetById_deactivated_but_not_deleted_row_is_not_silently_marked_deleted()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDefectClassRepository>();

        // 비활성이지만 삭제되지 않은 행(IS_ACTIVE=0, IS_DELETED=0, DELETED_AT=NULL) — 정당한 상태.
        const string id = "DC-RP-DEACTIVE";
        Seed(id, isActive: false, isDeleted: false, deletedAt: null);

        var reread = await repo.GetByIdAsync(id);

        reread.Should().NotBeNull("GetByIdAsync는 IS_DELETED 필터가 없어 비삭제 행을 반환해야 한다");
        reread!.IsActive.Should().BeFalse("영속된 IS_ACTIVE=0이 복원돼야 한다");
        reread.IsDeleted.Should().BeFalse(
            "비활성·미삭제 행은 읽기에서 IsDeleted=true로 둔갑하면 안 된다(과거 Deactivate() 결합부수효과 버그)");
        reread.DeletedAt.Should().BeNull("미삭제 행의 DeletedAt은 읽은 시각으로 채워지면 안 된다");
    }

    [Fact]
    public async Task GetById_soft_deleted_row_preserves_original_deleted_at()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDefectClassRepository>();

        // 소프트삭제 행 — 원래 삭제시각이 보존돼야 한다(읽은 시각 UtcNow로 덮어쓰면 안 됨).
        const string id = "DC-RP-DELETED";
        var deletedAt = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        Seed(id, isActive: false, isDeleted: true, deletedAt: deletedAt);

        var reread = await repo.GetByIdAsync(id);

        reread.Should().NotBeNull();
        reread!.IsDeleted.Should().BeTrue("영속된 IS_DELETED=1이 복원돼야 한다");
        reread.DeletedAt.Should().NotBeNull("영속된 DELETED_AT이 복원돼야 한다");
        reread.DeletedAt!.Value.Date.Should().Be(new DateTime(2026, 1, 15),
            "영속된 삭제시각이 보존돼야 한다 — 읽은 시각(오늘)으로 덮어써지면 읽기경로 상태손실 버그다");
    }

    [Fact]
    public async Task GetById_active_row_round_trips_unchanged()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDefectClassRepository>();

        const string id = "DC-RP-ACTIVE";
        Seed(id, isActive: true, isDeleted: false, deletedAt: null);

        var reread = await repo.GetByIdAsync(id);

        reread.Should().NotBeNull();
        reread!.IsActive.Should().BeTrue();
        reread.IsDeleted.Should().BeFalse();
        reread.DeletedAt.Should().BeNull();
        reread.Severity.Should().Be("Major");
    }
}
