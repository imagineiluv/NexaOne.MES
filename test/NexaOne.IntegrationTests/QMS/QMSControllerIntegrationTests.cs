using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.QMS;

/// <summary>
/// QMS 품질관리 HTTP 통합 테스트 — V006 마이그레이션으로 생성되는 QMS_DEFECT 테이블이
/// SQLite에서 실제로 동작하는지(테이블 존재 + 방언 SELECT/INSERT + 인증 + 라우팅 + 직렬화)를
/// end-to-end로 검증한다.
///
/// 주의: QmsController는 defects/defect-classes/inspection-specs/inspection-results/spc-params
/// 5종 엔드포인트를 노출하지만, db/migrations에는 QMS_DEFECT(V006) 한 테이블만 존재한다
/// (QMS_DEFECT_CLASS / QMS_INSPECTION_SPEC / QMS_INSPECTION_RESULT / QMS_SPC_PARAM 마이그레이션 누락 —
///  리포지토리는 해당 테이블을 SELECT/INSERT하므로 SQLite "no such table"로 실패한다).
/// 따라서 스모크/해피패스는 유일하게 스키마가 존재하는 defects(QMS_DEFECT) 경로만 사용한다.
///
/// FK: QMS_DEFECT.EQUIPMENT_ID → MDM_EQUIPMENT, INSPECTOR_ID → SYS_USER.
/// 테스트 하니스(SqliteSchemaBootstrapper)는 PRAGMA foreign_keys = OFF라 FK를 강제하지 않으나,
/// 운영 무결성과 EST 레퍼런스 패턴을 따라 설비(MDM_EQUIPMENT) 부모는 선행 시드한다.
/// </summary>
public sealed class QMSControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public QMSControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    /// <summary>
    /// 읽기 스모크: 메인 리스트 GET(defects)이 인증을 강제하고(미인증 401),
    /// 인증 시 QMS_DEFECT를 SELECT해 200(빈 목록)을 반환하는지 검증한다. 시드 불필요.
    /// </summary>
    [Fact]
    public async Task GetDefectsByLot_requires_auth_and_returns_ok_for_admin()
    {
        // 충돌 회피를 위한 고유 lotId — 쓰기 테스트와 격리한다.
        const string lotId = "LOT-QMS-RS";

        // 1) 미인증 클라이언트 → 컨트롤러 [Authorize]로 401.
        var anon = _factory.CreateClient();
        var anonResp = await anon.GetAsync($"/api/v1/qms/defects?lotId={lotId}");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "QmsController는 [Authorize]라 토큰 없는 요청은 401이어야 한다");

        // 2) ADMIN("*") 토큰 → 200 + 빈 목록(QMS_DEFECT 테이블 존재 + 방언 SELECT + 직렬화 성공).
        var admin = _factory.CreateAuthenticatedClient();
        var okResp = await admin.GetAsync($"/api/v1/qms/defects?lotId={lotId}");
        var body = await okResp.Content.ReadAsStringAsync();
        okResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"QMS_DEFECT SELECT가 성공해야 한다(테이블 존재 + dialect). 응답 본문: {body}");

        var defects = await okResp.Content.ReadFromJsonAsync<List<DefectDto>>();
        defects.Should().NotBeNull();
        defects!.Should().BeEmpty("시드하지 않았으므로 해당 lotId의 결함은 없어야 한다");
    }

    /// <summary>
    /// 쓰기 해피패스: FK 부모(설비) 선행 시드 → defect 등록(POST) → 200 → lotId로 재조회해 존재 확인.
    /// QMS_DEFECT INSERT(감사필드 자동 주입) + 재조회 SELECT의 왕복을 검증한다.
    /// </summary>
    [Fact]
    public async Task RecordDefect_persists_and_is_returned_by_lot()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — mdm/qms:manage 정책 통과
        const string plantId = "P-QMS";
        const string equipmentId = "EQ-QMS-1";
        const string defectId = "QMS-IT-1";
        const string lotId = "LOT-QMS-WP";

        // 0) 설비 등록 — QMS_DEFECT.EQUIPMENT_ID는 MDM_EQUIPMENT FK라 운영상 선행되어야 한다(EST 레퍼런스 동일).
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "QMS Test Equipment",
            plantId,
            areaId = "A-QMS",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-QMS"
        });
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK, "설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다");

        // 1) 결함 등록(QMS_DEFECT INSERT). 감사필드(CREATED_BY/AT, UPDATED_BY/AT)는 ServiceObjectProcessor가 주입한다.
        var recordResp = await client.PostAsJsonAsync("/api/v1/qms/defects", new
        {
            defectId,
            lotId,
            equipmentId,
            defectClassId = "DC-QMS",
            count = 3,
            rate = 0.15m,
            inspectorId = "test-admin"
        });
        var recordBody = await recordResp.Content.ReadAsStringAsync();
        recordResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"결함 등록(QMS_DEFECT INSERT)이 성공해야 한다. 응답 본문: {recordBody}");

        var created = await recordResp.Content.ReadFromJsonAsync<DefectDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(defectId);
        created.LotId.Should().Be(lotId);
        created.DefectCount.Should().Be(3);

        // 2) lotId로 재조회(QMS_DEFECT SELECT) — 등록한 결함이 영속화됐는지 확인.
        var defects = await client.GetFromJsonAsync<List<DefectDto>>($"/api/v1/qms/defects?lotId={lotId}");
        defects.Should().NotBeNull();
        defects!.Should().ContainSingle(d => d.Id == defectId)
            .Which.EquipmentId.Should().Be(equipmentId);
    }

    private sealed record DefectDto(
        string Id, string LotId, string EquipmentId, string DefectClassId,
        int DefectCount, decimal DefectRate, string InspectorId, bool IsConfirmed);
}
