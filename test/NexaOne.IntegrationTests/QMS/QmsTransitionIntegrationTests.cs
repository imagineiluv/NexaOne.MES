using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.QMS;

/// <summary>
/// QMS 상태전이 HTTP 통합 테스트 — 미검증 "쓰기/상태전이" 경로(부적합 확정·SPC 한계수정)가
/// SQLite에서 end-to-end로 동작하는지(테이블 존재 + 방언 UPDATE + 인증 + 직렬화 + 영속/되읽기)를 실증한다.
///
/// 대상 엔드포인트(QmsController):
///  - PUT  /api/v1/qms/defects/{defectId}/confirm  (ConfirmDefectRequest)  → NoContent
///  - PUT  /api/v1/qms/spc-params/{paramId}/limits (UpdateSpcLimitsRequest) → NoContent
///
/// 시나리오:
///  1) RecordDefect(POST) → ConfirmDefect(PUT) → GetDefectsByLot(GET)에서 IsConfirmed 확정상태 영속을 검증.
///  2) CreateSpcParam(POST) → UpdateSpcLimits(PUT) → GetSpcParams(GET)에서 UCL/LCL/Mean UPDATE 영속을 검증.
///
/// 하니스: TestApiFactory(클래스별 고유 SQLite 임시 DB, FK OFF, db/migrations 부트스트랩).
/// CreateAuthenticatedClient()는 기본 "*"(ADMIN, sub="test-admin") 토큰 → perm:qms:manage 정책 통과.
/// 전이 대상 행을 먼저 생성하므로 FK-OFF와 무관하게 성립. QMS_DEFECT.EQUIPMENT_ID는 MDM_EQUIPMENT FK라
/// 운영 무결성·EST 레퍼런스 패턴을 따라 설비 부모를 선행 시드한다(SPC는 V030 FK가 있으나 하니스 FK OFF + 부모 시드로 동등).
/// </summary>
public sealed class QmsTransitionIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public QmsTransitionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 부적합(Defect) 확정 전이 ─────────────────────────────────────────────────

    /// <summary>
    /// 미인증 가드: defect 확정(PUT)은 [Authorize]+perm:qms:manage라 토큰 없는 요청은 401이어야 한다.
    /// (전이 대상 존재 여부와 무관하게 인증이 먼저 강제됨)
    /// </summary>
    [Fact]
    public async Task ConfirmDefect_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsJsonAsync(
            "/api/v1/qms/defects/NO-SUCH-DEFECT/confirm",
            new { confirmerId = "test-admin" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "QmsController.ConfirmDefect는 [Authorize]라 토큰 없는 요청은 401이어야 한다");
    }

    /// <summary>
    /// 해피패스 전이: 설비 시드 → RecordDefect(POST) → ConfirmDefect(PUT, NoContent) → GetDefectsByLot(GET).
    /// 되읽기에서 IsConfirmed=true가 영속되는지 검증한다 — UPDATE(IS_CONFIRMED/CONFIRMED_AT)의 방언·영속 왕복.
    ///
    /// 검증 가치: 확정상태는 읽기경로(DefectRow.ToDomain)에서 복원되어야 한다. 만약 read-path가 상태를
    /// 손실하면(예: Create로 재구성하며 IsConfirmed를 강제 false) UPDATE가 DB에 영속됐어도 GET이 false를
    /// 보고해 본 단언이 실패하고, 그 차이가 곧 제품 버그(읽기경로 상태손실)다.
    /// </summary>
    [Fact]
    public async Task ConfirmDefect_persists_confirmed_state_and_is_visible_on_read()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:qms:manage 통과
        const string plantId = "P-QMS-TR";
        const string equipmentId = "EQ-QMS-TR-1";
        const string defectId = "QMS-TR-DEF-1";
        const string lotId = "LOT-QMS-TR-CONF";

        // 0) 설비 부모 선행 시드(QMS_DEFECT.EQUIPMENT_ID → MDM_EQUIPMENT). EST 레퍼런스와 동일.
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "QMS Transition Equipment",
            plantId,
            areaId = "A-QMS-TR",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-QMS-TR"
        });
        var equipBody = await equipResp.Content.ReadAsStringAsync();
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다. 응답 본문: {equipBody}");

        // 1) 결함 등록(QMS_DEFECT INSERT). 등록 직후 IsConfirmed는 false여야 한다.
        var recordResp = await client.PostAsJsonAsync("/api/v1/qms/defects", new
        {
            defectId,
            lotId,
            equipmentId,
            defectClassId = "DC-QMS-TR",
            count = 2,
            rate = 0.10m,
            inspectorId = "test-admin"
        });
        var recordBody = await recordResp.Content.ReadAsStringAsync();
        recordResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"결함 등록(QMS_DEFECT INSERT)이 성공해야 한다. 응답 본문: {recordBody}");

        var created = await recordResp.Content.ReadFromJsonAsync<DefectDto>();
        created.Should().NotBeNull();
        created!.IsConfirmed.Should().BeFalse("등록 직후 결함은 아직 미확정이어야 한다");

        // 2) 확정 전이(PUT) — 서비스는 GetById → Defect.Confirm → UPDATE(IS_CONFIRMED=1, CONFIRMED_AT) 수행.
        //    성공 시 컨트롤러는 NoContent(204)를 반환한다.
        var confirmResp = await client.PutAsJsonAsync(
            $"/api/v1/qms/defects/{defectId}/confirm",
            new { confirmerId = "test-admin" });
        var confirmBody = await confirmResp.Content.ReadAsStringAsync();
        confirmResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"결함 확정(QMS_DEFECT UPDATE)이 성공하면 204 NoContent여야 한다. 응답 본문: {confirmBody}");

        // 3) lotId로 되읽기(QMS_DEFECT SELECT) — 확정상태가 영속/복원됐는지 검증.
        var afterResp = await client.GetAsync($"/api/v1/qms/defects?lotId={lotId}");
        var afterBody = await afterResp.Content.ReadAsStringAsync();
        afterResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"확정 후 QMS_DEFECT SELECT가 성공해야 한다. 응답 본문: {afterBody}");

        var defects = await afterResp.Content.ReadFromJsonAsync<List<DefectDto>>();
        defects.Should().NotBeNull();
        var reread = defects!.SingleOrDefault(d => d.Id == defectId);
        reread.Should().NotBeNull("확정한 결함이 lotId 조회로 되읽혀야 한다");
        reread!.IsConfirmed.Should().BeTrue(
            "확정(PUT) 후 되읽기에서 IsConfirmed=true가 복원되어야 한다 — false면 읽기경로 상태손실(DefectRow.ToDomain이 " +
            "Defect.Create로 재구성하며 IsConfirmed/ConfirmedAt를 버림) 제품 버그다");
    }

    /// <summary>
    /// 비-부인성: 확정자(ConfirmerId)는 본문 값에 의존하지 말고, 확정 행위가 토큰 신원(sub="test-admin")으로
    /// UpdatedBy 감사필드에 기록되는지 검증한다. ServiceObjectProcessor.UpdateAsync가 앰비언트 사용자로
    /// UPDATED_BY를 덮어쓰므로, 본문에 다른 confirmerId를 보내도 감사 사용자는 토큰 신원이어야 한다.
    /// (확정상태 자체가 읽기경로에서 손실되면 본 단언 이전 단계에서 이미 실패해 버그가 드러난다.)
    /// </summary>
    [Fact]
    public async Task ConfirmDefect_records_token_identity_in_audit_not_body_confirmer()
    {
        var client = _factory.CreateAuthenticatedClient();   // sub = "test-admin"
        const string equipmentId = "EQ-QMS-TR-2";
        const string defectId = "QMS-TR-DEF-2";
        const string lotId = "LOT-QMS-TR-AUDIT";

        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "QMS Audit Equipment",
            plantId = "P-QMS-TR",
            areaId = "A-QMS-TR",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-QMS-TR"
        });
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK, "설비 등록이 성공해야 한다");

        var recordResp = await client.PostAsJsonAsync("/api/v1/qms/defects", new
        {
            defectId,
            lotId,
            equipmentId,
            defectClassId = "DC-QMS-TR",
            count = 1,
            rate = 0.05m,
            inspectorId = "test-admin"
        });
        recordResp.StatusCode.Should().Be(HttpStatusCode.OK, "결함 등록이 성공해야 한다");

        // 본문에는 의도적으로 토큰과 다른 confirmerId를 보낸다 — 감사 사용자는 토큰 신원이어야 한다.
        var confirmResp = await client.PutAsJsonAsync(
            $"/api/v1/qms/defects/{defectId}/confirm",
            new { confirmerId = "SOMEONE-ELSE" });
        var confirmBody = await confirmResp.Content.ReadAsStringAsync();
        confirmResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"결함 확정이 성공하면 204여야 한다. 응답 본문: {confirmBody}");

        var defects = await client.GetFromJsonAsync<List<DefectDto>>($"/api/v1/qms/defects?lotId={lotId}");
        defects.Should().NotBeNull();
        var reread = defects!.SingleOrDefault(d => d.Id == defectId);
        reread.Should().NotBeNull("확정한 결함이 되읽혀야 한다");
        reread!.IsConfirmed.Should().BeTrue(
            "확정 후 되읽기에서 IsConfirmed=true여야 한다(false면 읽기경로 상태손실 버그)");
        reread.UpdatedBy.Should().Be("test-admin",
            "확정 행위의 감사 사용자(UPDATED_BY)는 본문 confirmerId가 아니라 토큰 신원(sub)으로 기록되어야 한다");
    }

    // ── SPC 한계(Control Limits) 수정 전이 ───────────────────────────────────────

    /// <summary>
    /// 미인증 가드: SPC 한계수정(PUT)은 [Authorize]+perm:qms:manage라 토큰 없는 요청은 401이어야 한다.
    /// </summary>
    [Fact]
    public async Task UpdateSpcLimits_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsJsonAsync(
            "/api/v1/qms/spc-params/NO-SUCH-PARAM/limits",
            new { mean = 100.0m, ucl = 110.0m, lcl = 90.0m });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "QmsController.UpdateSpcLimits는 [Authorize]라 토큰 없는 요청은 401이어야 한다");
    }

    /// <summary>
    /// 해피패스 전이: CreateSpcParam(POST) → UpdateSpcLimits(PUT, NoContent) → GetSpcParams(GET).
    /// 되읽기에서 Mean/UCL/LCL이 수정값으로 영속되는지 검증한다 — QMS_SPC_PARAM UPDATE의 방언·영속 왕복.
    /// (USL/LSL/SampleSize는 UPDATE 대상이 아니므로 그대로 유지되어야 한다.)
    /// </summary>
    [Fact]
    public async Task UpdateSpcLimits_persists_new_limits_and_is_visible_on_read()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string paramId = "QMS-TR-SPC-1";
        const string equipmentId = "EQ-QMS-TR-SPC";

        // 1) SPC 파라미터 등록(QMS_SPC_PARAM INSERT) — 초기 한계.
        var createResp = await client.PostAsJsonAsync("/api/v1/qms/spc-params", new
        {
            paramId,
            paramName = "Chamber Pressure",
            equipmentId,
            processId = "PROC-QMS-TR",
            mean = 100.0m,
            ucl = 110.0m,
            lcl = 90.0m,
            sampleSize = 5,
            usl = 115.0m,
            lsl = 85.0m
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SPC 파라미터 등록(QMS_SPC_PARAM INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // 등록 직후 초기 한계 확인.
        var before = await client.GetFromJsonAsync<List<SpcParamDto>>(
            $"/api/v1/qms/spc-params?equipmentId={equipmentId}");
        before.Should().NotBeNull();
        before!.Should().ContainSingle(p => p.Id == paramId)
            .Which.Ucl.Should().Be(110.0m, "등록 직후 UCL은 초기값이어야 한다");

        // 2) 한계 수정 전이(PUT) — 서비스: GetById → UpdateControlLimits → UPDATE(MEAN/UCL/LCL). 성공 시 204.
        var newMean = 102.5m;
        var newUcl = 120.0m;
        var newLcl = 80.0m;
        var updateResp = await client.PutAsJsonAsync(
            $"/api/v1/qms/spc-params/{paramId}/limits",
            new { mean = newMean, ucl = newUcl, lcl = newLcl });
        var updateBody = await updateResp.Content.ReadAsStringAsync();
        updateResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"SPC 한계 수정(QMS_SPC_PARAM UPDATE)이 성공하면 204 NoContent여야 한다. 응답 본문: {updateBody}");

        // 3) 되읽기(QMS_SPC_PARAM SELECT) — 수정한 Mean/UCL/LCL이 영속됐는지 검증.
        var afterResp = await client.GetAsync($"/api/v1/qms/spc-params?equipmentId={equipmentId}");
        var afterBody = await afterResp.Content.ReadAsStringAsync();
        afterResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"수정 후 QMS_SPC_PARAM SELECT가 성공해야 한다. 응답 본문: {afterBody}");

        var after = await afterResp.Content.ReadFromJsonAsync<List<SpcParamDto>>();
        after.Should().NotBeNull();
        var reread = after!.SingleOrDefault(p => p.Id == paramId);
        reread.Should().NotBeNull("수정한 SPC 파라미터가 equipmentId 조회로 되읽혀야 한다");
        reread!.Mean.Should().Be(newMean, "수정한 Mean이 영속되어야 한다(QMS_SPC_PARAM.MEAN UPDATE)");
        reread.Ucl.Should().Be(newUcl, "수정한 UCL이 영속되어야 한다(QMS_SPC_PARAM.UCL UPDATE)");
        reread.Lcl.Should().Be(newLcl, "수정한 LCL이 영속되어야 한다(QMS_SPC_PARAM.LCL UPDATE)");
        // UPDATE 대상이 아닌 필드는 보존되어야 한다.
        reread.Usl.Should().Be(115.0m, "한계 수정은 USL을 변경하지 않으므로 보존되어야 한다");
        reread.Lsl.Should().Be(85.0m, "한계 수정은 LSL을 변경하지 않으므로 보존되어야 한다");
        reread.SampleSize.Should().Be(5, "한계 수정은 SampleSize를 변경하지 않으므로 보존되어야 한다");
    }

    // ── DTO(컨트롤러가 직렬화하는 도메인 객체와 1:1, ASP.NET Core 기본 camelCase) ─────────────
    private sealed record DefectDto(
        string Id, string LotId, string EquipmentId, string DefectClassId,
        int DefectCount, decimal DefectRate, string InspectorId,
        bool IsConfirmed, DateTime? ConfirmedAt, string? UpdatedBy);

    private sealed record SpcParamDto(
        string Id, string ParamName, string EquipmentId, string ProcessId,
        decimal Mean, decimal Ucl, decimal Lcl, decimal? Usl, decimal? Lsl,
        int SampleSize, bool IsActive);
}
