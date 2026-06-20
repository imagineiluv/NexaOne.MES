using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Infrastructure;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.IntegrationTests.Rms;

/// <summary>
/// ADR-008 얇은 브리지 어댑터 로직 통합 검증 — 실 SQLite RMS 리포 위에서 <see cref="RecipeBridge"/>를
/// 직접 구동(Spring/ALC 불필요)해 레시피 승인 상태기계(Draft→WaitApproval→Approved1→Approved→Released)와
/// 4-eyes(2차≠1차) 규칙, Released 잠금, 신버전 생성, NotFound, 도메인→DTO 매핑(읽기 경로)을 검증한다.
/// 마이그레이션 스키마는 운영과 동일한 <see cref="SqliteSchemaBootstrapper"/>(= SqliteSchemaInitializer)로
/// 부트스트랩하며, 브리지/서비스/리포는 호스트 DI(rms.xml)와 동일한 인자로 손수 조립한다:
/// new RecipeBridge(new RecipeService(new RecipeRepository(ds, config), new RecipeParamRepository(ds))).
/// 테스트 간 격리는 Guid 기반 고유 recipe/param id로 보장한다.
/// </summary>
public sealed class RecipeBridgeIntegrationTests : IClassFixture<RecipeBridgeIntegrationTests.BridgeHarness>
{
    private readonly BridgeHarness _h;
    public RecipeBridgeIntegrationTests(BridgeHarness h) => _h = h;

    /// <summary>실 SQLite DB(임시 파일) + 마이그레이션 스키마를 1회 부트스트랩하고, 호스트와 동일 인자로 브리지를 조립한다.</summary>
    public sealed class BridgeHarness : IDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nexaone-rms-bridge-it-{Guid.NewGuid():N}.db");
        public IRecipeApprovalBridge Bridge { get; }

        public BridgeHarness()
        {
            // Foreign Keys=False — TestApiFactory와 동일(연결별 FK 강제의 플래키 방지). 운영(MSSQL)은 FK 강제.
            var connString = $"Data Source={_dbPath};Foreign Keys=False";
            SqliteSchemaBootstrapper.Apply(connString);

            var ds = new EesDataSource
            {
                Provider = new NexusCom.Data.Sqlite.SqliteProvider(),
                ConnectionString = connString,
            };
            // 리포가 읽는 유일 키는 Events:Outbox:Enabled(기본 off). 빈 구성이면 outbox 미기록(기존 동작)이라 어댑터 로직 검증에 충분.
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            Bridge = new RecipeBridge(new RecipeService(
                new RecipeRepository(ds, config),
                new RecipeParamRepository(ds)));
        }

        public void Dispose()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 임시 파일 정리 실패는 무시 */ }
        }
    }

    [Fact]
    public async Task Recipe_approval_state_machine_runs_end_to_end_on_real_sqlite()
    {
        var bridge = _h.Bridge;
        // 테스트 간 격리 — 고유 recipe/param id(Guid). 동일 DB를 공유하나 키가 겹치지 않는다.
        var rid = $"R-{Guid.NewGuid():N}";
        var newRid = $"R-{Guid.NewGuid():N}";
        var pid = $"P-{Guid.NewGuid():N}";

        // 1) Create — 신규 레시피는 Draft/Version=1로 영속·복원돼야 한다.
        var created = await bridge.CreateRecipeAsync(rid, "n", "d", "EC1");
        created.IsSuccess.Should().BeTrue("신규 레시피 생성(RMS_RECIPE INSERT)은 성공해야 한다");
        created.Value.ApprovalState.Should().Be("Draft", "신규 레시피 초기 상태는 Draft여야 한다");
        created.Value.Version.Should().Be(1, "신규 레시피 버전은 1이어야 한다");

        // 2) RequestApproval — Draft→WaitApproval. 재요청은 더 이상 Draft가 아니므로 Conflict여야 한다(읽기경로 상태복원 검증).
        (await bridge.RequestApprovalAsync(rid)).IsSuccess.Should().BeTrue("Draft→WaitApproval 승인요청은 성공해야 한다");
        var reRequest = await bridge.RequestApprovalAsync(rid);
        reRequest.IsFailure.Should().BeTrue("WaitApproval 상태에서 재승인요청은 실패여야 한다(Draft 아님)");
        reRequest.Error.Type.Should().Be(ErrorType.Conflict, "상태위반 재요청은 Conflict 타입이어야 한다(→409)");

        // 3) Approve1 → Approved1. 동일 승인자로 Approve2는 4-eyes 위반(2차≠1차)이라 Conflict, 다른 승인자는 성공.
        (await bridge.Approve1Async(rid, "u1")).IsSuccess.Should().BeTrue("WaitApproval→Approved1 1차 승인은 성공해야 한다");
        var sameApprover = await bridge.Approve2Async(rid, "u1");
        sameApprover.IsFailure.Should().BeTrue("2차 승인자가 1차와 같으면 실패여야 한다(4-eyes)");
        sameApprover.Error.Type.Should().Be(ErrorType.Conflict, "동일 승인자 2차 승인은 Conflict 타입이어야 한다");
        (await bridge.Approve2Async(rid, "u2")).IsSuccess.Should().BeTrue("다른 승인자(u2)의 2차 승인은 성공해야 한다(Approved1→Approved)");

        // 4) Release → Released. 배포된 레시피는 파라미터 변경이 잠겨야 한다(Conflict).
        (await bridge.ReleaseAsync(rid, "u3")).IsSuccess.Should().BeTrue("Approved→Released 배포는 성공해야 한다");
        var lockedAdd = await bridge.AddParamAsync(pid, rid, "p", "v", "u", 1);
        lockedAdd.IsFailure.Should().BeTrue("Released 레시피에 파라미터 추가는 실패여야 한다(배포 잠금)");
        lockedAdd.Error.Type.Should().Be(ErrorType.Conflict, "Released 잠금 위반은 Conflict 타입이어야 한다(→409)");

        // 5) CreateNewVersion — Released 레시피만 신버전 생성 가능. 신버전은 Draft/Version=2.
        var newVersion = await bridge.CreateNewVersionAsync(rid, newRid);
        newVersion.IsSuccess.Should().BeTrue("Released 레시피의 신버전 생성은 성공해야 한다");
        newVersion.Value.Version.Should().Be(2, "신버전의 버전은 원본+1=2여야 한다");
        newVersion.Value.ApprovalState.Should().Be("Draft", "신버전 초기 상태는 Draft여야 한다");

        // 6) NotFound — 미존재 레시피 조회는 NotFound 타입이어야 한다(→404).
        var notFound = await bridge.GetRecipeAsync("nope");
        notFound.IsFailure.Should().BeTrue("미존재 레시피 조회는 실패여야 한다");
        notFound.Error.Type.Should().Be(ErrorType.NotFound, "미존재 레시피는 NotFound 타입이어야 한다(→404)");

        // 7) DTO 읽기 매핑 — 상태/설비클래스 필터 조회가 계약 DTO로 노출되는지 확인.
        (await bridge.GetByStateAsync("Released")).Should().Contain(d => d.RecipeId == rid,
            "배포된 레시피가 상태 필터 조회(DTO)에 나타나야 한다");
        (await bridge.GetByEquipmentClassAsync("EC1")).Should().NotBeEmpty(
            "설비클래스 필터 조회(DTO)는 비어있지 않아야 한다");
    }
}
