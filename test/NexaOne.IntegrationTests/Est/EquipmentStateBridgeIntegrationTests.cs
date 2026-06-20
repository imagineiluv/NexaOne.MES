using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.IntegrationTests.Est;

/// <summary>
/// ADR-008 얇은 브리지 어댑터 로직 통합 검증 — 실 SQLite EST 리포 위에서 <see cref="EquipmentStateBridge"/>를
/// 직접 구동(Spring/ALC 불필요)해 ChangeState의 5분기(Success/Conflict/InvalidTransition/RequireReason)와
/// 도메인→DTO 매핑(읽기 경로)을 검증한다. 마이그레이션 스키마는 운영과 동일한 <see cref="SqliteSchemaBootstrapper"/>
/// (= SqliteSchemaInitializer)로 부트스트랩하며, 브리지/서비스/리포는 호스트 DI(server.xml)와 동일한 인자로
/// 손수 조립한다: new EquipmentStateBridge(new EquipmentStateService(new EquipmentStateMatrixRepository(ds),
/// new EquipmentStateRepository(ds, dialect, config))). 테스트 간 격리는 Guid 기반 고유 plant/equipment id로 보장한다.
/// </summary>
public sealed class EquipmentStateBridgeIntegrationTests : IClassFixture<EquipmentStateBridgeIntegrationTests.BridgeHarness>
{
    private readonly BridgeHarness _h;
    public EquipmentStateBridgeIntegrationTests(BridgeHarness h) => _h = h;

    /// <summary>실 SQLite DB(임시 파일) + 마이그레이션 스키마를 1회 부트스트랩하고, 호스트와 동일 인자로 브리지를 조립한다.</summary>
    public sealed class BridgeHarness : IDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nexaone-bridge-it-{Guid.NewGuid():N}.db");
        public IEquipmentStateBridge Bridge { get; }

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
            var dialect = new SqliteEesDbCapability();
            // 리포가 읽는 유일 키는 Events:Outbox:Enabled(기본 off). 빈 구성이면 outbox 미기록(기존 동작)이라 어댑터 로직 검증에 충분.
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            Bridge = new EquipmentStateBridge(new EquipmentStateService(
                new EquipmentStateMatrixRepository(ds),
                new EquipmentStateRepository(ds, dialect, config)));
        }

        public void Dispose()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 임시 파일 정리 실패는 무시 */ }
        }
    }

    [Fact]
    public async Task ChangeState_covers_success_conflict_invalid_and_reason_branches_with_dto_mapping()
    {
        var bridge = _h.Bridge;
        // 테스트 간 격리 — 고유 plant/equipment id(Guid). 동일 DB를 공유하나 키가 겹치지 않는다.
        var plant = $"P-{Guid.NewGuid():N}";
        var eq = $"EQ-{Guid.NewGuid():N}";
        var eq2 = $"EQ-{Guid.NewGuid():N}";
        var eq3 = $"EQ-{Guid.NewGuid():N}";

        // 1) 매트릭스 시드: IDLE→RUN 허용(사유 불필요), IDLE→NEEDR 허용(사유 필수). IDLE→BAD는 의도적으로 미시드.
        (await bridge.UpsertMatrixAsync(plant, "IDLE", "RUN", allowFlag: true, setStateId: null, requireReason: false))
            .IsSuccess.Should().BeTrue("매트릭스 업서트(IDLE→RUN)는 성공해야 한다");
        (await bridge.UpsertMatrixAsync(plant, "IDLE", "NEEDR", allowFlag: true, setStateId: null, requireReason: true))
            .IsSuccess.Should().BeTrue("매트릭스 업서트(IDLE→NEEDR, 사유필수)는 성공해야 한다");

        // 2) Success — 미존재 상태행은 IDLE로 부트스트랩(version=1) 후 IDLE→RUN 전이 → version 증가(>1).
        var success = await bridge.ChangeStateAsync(eq, plant, "RUN", "tester", null, "UI", null);
        success.IsSuccess.Should().BeTrue("IDLE→RUN 허용 전이는 성공해야 한다");
        success.Value.Should().BeOfType<EquipmentStateDto>("브리지는 도메인이 아니라 계약 DTO를 반환해야 한다");
        success.Value.CurrentStateId.Should().Be("RUN", "전이 후 현재 상태는 SET_STATE_ID(RUN)여야 한다");
        success.Value.StateVersion.Should().BeGreaterThan(1, "부트스트랩(1) + 전이로 버전이 증가해야 한다");

        // 3) Conflict — 위 성공으로 버전이 2+가 됐으므로 expectedVersion:1은 낙관적 동시성 충돌이어야 한다.
        var conflict = await bridge.ChangeStateAsync(eq, plant, "RUN", "tester", null, "UI", expectedVersion: 1);
        conflict.IsFailure.Should().BeTrue("기대 버전 불일치는 실패여야 한다");
        conflict.Error.Type.Should().Be(ErrorType.Conflict, "낙관적 동시성 충돌은 Conflict 타입이어야 한다(→409)");

        // 4) InvalidTransition — eq2는 신규(IDLE 부트스트랩), IDLE→BAD는 매트릭스에 없다.
        var invalid = await bridge.ChangeStateAsync(eq2, plant, "BAD", "tester", null, "UI", null);
        invalid.IsFailure.Should().BeTrue("미허용 전이는 실패여야 한다");
        invalid.Error.Code.Should().Be("EPT.InvalidTransition", "매트릭스에 없는 전이는 EPT.InvalidTransition이어야 한다(→400)");

        // 5) Validation(require reason) — IDLE→NEEDR은 사유 필수인데 reason:null이다.
        var needReason = await bridge.ChangeStateAsync(eq3, plant, "NEEDR", "tester", reason: null, "UI", null);
        needReason.IsFailure.Should().BeTrue("사유 누락은 실패여야 한다");
        needReason.Error.Type.Should().Be(ErrorType.Validation, "사유 필수 미충족은 Validation 타입이어야 한다(→400)");

        // 6) DTO 매핑 읽기 — 상태/매트릭스/이력이 계약 DTO로 노출되는지 확인.
        (await bridge.GetEquipmentStatesAsync(plant)).Should().Contain(d => d.EquipmentId == eq,
            "성공 전이된 설비가 상태 조회(DTO)에 나타나야 한다");
        (await bridge.GetMatrixAsync(plant)).Should().Contain(m => m.FromStateId == "IDLE" && m.ToStateId == "RUN",
            "시드한 매트릭스 행이 매트릭스 조회(DTO)에 나타나야 한다");
        (await bridge.GetHistoryAsync(eq)).Should().NotBeNull("이력 조회는 DTO 리스트를 반환해야 한다");
    }
}
