using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.QMS;

/// <summary>
/// QMS 마스터/결과 "생성→되읽기" HTTP 통합 테스트 — 통합 커버리지가 없던 두 보안 생성 엔드포인트
/// (POST defect-classes, POST inspection-results)가 SQLite에서 end-to-end로 동작하는지
/// (테이블 존재 + 방언 INSERT/SELECT + 인증 + 직렬화 + 영속/되읽기)를 create→GET 왕복으로 실증한다.
///
/// 대상 엔드포인트(QmsController, 둘 다 [Authorize] + perm:qms:manage):
///  - POST /api/v1/qms/defect-classes     (CreateDefectClassRequest)       → 200 + DefectClass
///  - POST /api/v1/qms/inspection-results (RecordInspectionResultRequest)  → 200 + InspectionResult
/// 되읽기:
///  - GET  /api/v1/qms/defect-classes                  (전체 목록)
///  - GET  /api/v1/qms/inspection-results?lotId=...     (Lot 기준 목록)
///
/// 기존 커버리지와 비중복:
///  - QMSControllerIntegrationTests는 defect-classes/inspection-results의 "읽기 스모크(빈 목록)"만,
///    쓰기 해피패스는 inspection-specs/spc-params만 다룬다(생성 검증 공백 = 본 두 엔드포인트).
///  - DefectClassReadPathIntegrationTests는 IDefectClassRepository.GetByIdAsync 단위(소프트삭제 복원).
///  본 테스트는 그 둘이 비운 "API 레벨 생성→GET" 경로를 채운다.
///
/// 하니스: TestApiFactory(클래스별 고유 SQLite 임시 DB, FK OFF, db/migrations 부트스트랩).
/// CreateAuthenticatedClient()는 기본 "*"(ADMIN, sub="test-admin") 토큰 → perm:qms:manage 정책 통과.
/// inspection-results는 서비스가 SpecRepository.GetByIdAsync로 규격 존재를 먼저 검사하므로(없으면 NotFound),
/// FK-OFF와 무관하게 검사규격(InspectionSpec)을 선행 POST로 시드해야 한다.
/// 모든 ID는 클래스 고유라 클래스 내 병렬과 무관하다.
/// </summary>
public sealed class QmsMasterMutationIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public QmsMasterMutationIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── (a) 결함 분류 마스터 생성 → GET 목록 포함 ───────────────────────────────────

    /// <summary>
    /// 해피패스: defect-class 생성(POST) → GET 목록에서 되읽어 IsActive=true·Severity 보존을 검증한다.
    /// QMS_DEFECT_CLASS INSERT + 목록 SELECT 왕복.
    ///
    /// 검증 가치(읽기경로 Restore 회귀 안전망): 목록은 DefectClassRow.ToDomain → DefectClass.Restore로
    /// 복원되므로, 생성 직후(IsActive=true, IsDeleted=false) 행이 목록에서 그대로 보여야 한다. 만약 읽기경로가
    /// Create로 재구성 후 비활성 복원에 Deactivate()를 쓰면 IsActive가 뒤집히거나 미삭제 행이 삭제로 둔갑해
    /// 본 단언이 깨진다 — 그 차이가 곧 제품 버그(읽기경로 상태손실)다.
    /// </summary>
    [Fact]
    public async Task CreateDefectClass_persists_and_is_returned_active_in_list()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:qms:manage 통과
        const string defectClassId = "DC-MM-1";

        var createResp = await client.PostAsJsonAsync("/api/v1/qms/defect-classes", new
        {
            defectClassId,
            defectClassName = "표면 스크래치",
            description = "마스터 생성 통합테스트 시드",
            severity = "Major"   // 도메인 제약: Critical/Major/Minor 중 하나여야 한다
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"결함 분류 생성(QMS_DEFECT_CLASS INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // 생성 응답 자체에서 IsActive=true 확인 — Create가 활성으로 고정한다.
        var created = await createResp.Content.ReadFromJsonAsync<MasterDefectClassDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(defectClassId, "생성 응답의 Id는 요청한 분류 ID여야 한다");
        created.IsActive.Should().BeTrue("DefectClass.Create는 IsActive=true로 생성한다");

        // GET 목록에서 되읽기 — 읽기경로(Restore) 복원이 활성 상태를 손실하지 않는지 검증.
        var list = await client.GetFromJsonAsync<List<MasterDefectClassDto>>("/api/v1/qms/defect-classes");
        list.Should().NotBeNull();
        var reread = list!.SingleOrDefault(d => d.Id == defectClassId);
        reread.Should().NotBeNull("생성한 결함 분류가 목록 GET으로 되읽혀야 한다");
        reread!.IsActive.Should().BeTrue(
            "생성 직후 분류는 목록에서 IsActive=true로 복원돼야 한다 — false면 읽기경로 상태손실(Restore가 활성을 버림) 버그다");
        reread.Severity.Should().Be("Major", "영속된 Severity가 보존돼야 한다(QMS_DEFECT_CLASS.SEVERITY 왕복)");
        reread.DefectClassName.Should().Be("표면 스크래치", "영속된 분류명이 보존돼야 한다");
    }

    // ── (b) 검사 결과 생성(IsPass 지정) → GET ?lotId= 되읽기 ─────────────────────────

    /// <summary>
    /// 해피패스: 검사규격 선행 시드(POST inspection-specs) → 검사결과 생성(POST inspection-results, IsPass 지정) →
    /// GET ?lotId=로 되읽어 IsPass·측정값 보존을 검증한다. QMS_INSPECTION_RESULT INSERT + Lot SELECT 왕복.
    ///
    /// 선행조건: RecordInspectionResultAsync는 SpecRepository.GetByIdAsync로 규격 존재를 먼저 확인하므로
    /// (없으면 NotFound) 검사규격을 반드시 선행 생성한다. 측정타입은 "Attribute"(비-Numeric)로 둔다 —
    /// InspectionResult.Create는 measureType=="Numeric"이고 measuredValue·nominalValue가 모두 있을 때만
    /// 공차로 IsPass를 재계산하고, 그 외에는 요청 isPass를 그대로 채택한다. 따라서 비-Numeric 규격이라야
    /// "지정한 IsPass가 보존되는가"를 결정적으로 검증할 수 있다.
    ///
    /// 검증 가치: IsPass는 검사 시점에 확정된 권위값이라 되읽기에서 보존돼야 한다. 읽기경로(Restore)가 저장된
    /// IS_PASS를 그대로 복원하므로, 지정한 합부(여기선 불합격 false)가 GET에서 뒤집히면 안 된다.
    /// </summary>
    [Fact]
    public async Task RecordInspectionResult_persists_specified_ispass_and_is_visible_by_lot()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string specId = "SPEC-MM-1";
        const string resultFailId = "RES-MM-FAIL";
        const string resultPassId = "RES-MM-PASS";
        const string lotId = "LOT-MM-INSP";
        const string equipmentId = "EQ-MM-INSP";

        // 0) 검사규격 선행 시드(서비스 GetByIdAsync 가드 충족). 비-Numeric이라 요청 IsPass가 그대로 채택된다.
        var specResp = await client.PostAsJsonAsync("/api/v1/qms/inspection-specs", new
        {
            specId,
            specName = "외관 합부 판정",
            processId = "PROC-MM",
            itemName = "외관",
            measureType = "Attribute",
            nominalValue = (decimal?)null,
            tolerancePlus = (decimal?)null,
            toleranceMinus = (decimal?)null
        });
        var specBody = await specResp.Content.ReadAsStringAsync();
        specResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"검사규격 선행 생성(QMS_INSPECTION_SPEC INSERT)이 성공해야 한다. 응답 본문: {specBody}");

        // 1) 불합격(IsPass=false) 결과 생성 — 측정값/속성도 함께 보존되는지 본다.
        var failResp = await client.PostAsJsonAsync("/api/v1/qms/inspection-results", new
        {
            resultId = resultFailId,
            specId,
            lotId,
            equipmentId,
            inspectorId = "test-admin",
            measuredValue = 12.34m,
            attributeResult = "NG",
            isPass = false,
            remark = "지정 불합격"
        });
        var failBody = await failResp.Content.ReadAsStringAsync();
        failResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"검사결과 생성(QMS_INSPECTION_RESULT INSERT, IsPass=false)이 성공해야 한다. 응답 본문: {failBody}");

        var createdFail = await failResp.Content.ReadFromJsonAsync<MasterInspectionResultDto>();
        createdFail.Should().NotBeNull();
        createdFail!.IsPass.Should().BeFalse("비-Numeric 규격이라 지정한 IsPass=false가 그대로 채택돼야 한다");

        // 2) 합격(IsPass=true) 결과를 같은 Lot에 추가 — 합부 양방향이 독립 보존되는지 결정적으로 본다.
        var passResp = await client.PostAsJsonAsync("/api/v1/qms/inspection-results", new
        {
            resultId = resultPassId,
            specId,
            lotId,
            equipmentId,
            inspectorId = "test-admin",
            measuredValue = (decimal?)null,
            attributeResult = "OK",
            isPass = true,
            remark = "지정 합격"
        });
        var passBody = await passResp.Content.ReadAsStringAsync();
        passResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"검사결과 생성(IsPass=true)이 성공해야 한다. 응답 본문: {passBody}");

        // 3) lotId로 되읽기(QMS_INSPECTION_RESULT SELECT) — IsPass·측정값이 지정값 그대로 복원되는지 검증.
        var afterResp = await client.GetAsync($"/api/v1/qms/inspection-results?lotId={lotId}");
        var afterBody = await afterResp.Content.ReadAsStringAsync();
        afterResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"검사결과 Lot 조회(QMS_INSPECTION_RESULT SELECT)가 성공해야 한다. 응답 본문: {afterBody}");

        var results = await afterResp.Content.ReadFromJsonAsync<List<MasterInspectionResultDto>>();
        results.Should().NotBeNull();
        results!.Should().HaveCount(2, "같은 Lot에 두 건을 기록했으므로 둘 다 되읽혀야 한다");

        var rereadFail = results!.SingleOrDefault(r => r.Id == resultFailId);
        rereadFail.Should().NotBeNull("불합격 결과가 Lot 조회로 되읽혀야 한다");
        rereadFail!.IsPass.Should().BeFalse(
            "지정한 불합격(IsPass=false)이 되읽기에서 보존돼야 한다 — true로 뒤집히면 읽기경로 합부 손실 버그다");
        rereadFail.MeasuredValue.Should().Be(12.34m, "영속된 측정값이 보존돼야 한다(QMS_INSPECTION_RESULT.MEASURED_VALUE 왕복)");
        rereadFail.SpecId.Should().Be(specId, "결과가 선행 시드한 검사규격에 연결돼야 한다");

        var rereadPass = results!.SingleOrDefault(r => r.Id == resultPassId);
        rereadPass.Should().NotBeNull("합격 결과가 Lot 조회로 되읽혀야 한다");
        rereadPass!.IsPass.Should().BeTrue("지정한 합격(IsPass=true)이 되읽기에서 보존돼야 한다");
    }

    // ── (c) 미인증 POST 가드 ───────────────────────────────────────────────────────

    /// <summary>
    /// 미인증 가드: 두 생성 엔드포인트는 [Authorize]+perm:qms:manage라 토큰 없는 POST는 401이어야 한다.
    /// (인증이 본문 검증·영속보다 먼저 강제되므로 시드 없이도 성립.)
    /// </summary>
    [Fact]
    public async Task Create_endpoints_require_auth()
    {
        var anon = _factory.CreateClient();

        var dcResp = await anon.PostAsJsonAsync("/api/v1/qms/defect-classes", new
        {
            defectClassId = "DC-MM-ANON",
            defectClassName = "anon",
            description = "anon",
            severity = "Minor"
        });
        dcResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "QmsController.CreateDefectClass는 [Authorize]라 토큰 없는 요청은 401이어야 한다");

        var irResp = await anon.PostAsJsonAsync("/api/v1/qms/inspection-results", new
        {
            resultId = "RES-MM-ANON",
            specId = "SPEC-MM-ANON",
            lotId = "LOT-MM-ANON",
            equipmentId = "EQ-MM-ANON",
            inspectorId = "test-admin",
            measuredValue = (decimal?)null,
            attributeResult = "OK",
            isPass = true,
            remark = (string?)null
        });
        irResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "QmsController.RecordInspectionResult는 [Authorize]라 토큰 없는 요청은 401이어야 한다");
    }

    // ── DTO(컨트롤러가 직렬화하는 도메인 객체와 1:1, ASP.NET Core 기본 camelCase) ─────────────
    //    같은 namespace의 형제 테스트 private record(DefectDto/InspectionResultDto 등)와 충돌하지 않게
    //    본 파일 전용 이름(Master*)으로 새로 정의한다.
    private sealed record MasterDefectClassDto(
        string Id, string DefectClassName, string Description, string Severity, bool IsActive);

    private sealed record MasterInspectionResultDto(
        string Id, string SpecId, string LotId, string EquipmentId,
        decimal? MeasuredValue, string? AttributeResult, string InspectorId,
        bool IsPass, string? Remark);
}
