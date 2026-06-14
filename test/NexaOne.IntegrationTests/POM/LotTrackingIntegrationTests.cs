using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.POM;

/// <summary>
/// POM 로트 추적(genealogy) HTTP 통합 테스트 — V014 마이그레이션으로 생성되는 3개 테이블
/// (POM_LOT / POM_LOT_HISTORY / POM_LOT_MIXING_RELATION)이 SQLite에서 실제로 동작하는지,
/// 그리고 Lot 생성 → TrackIn → TrackOut 의 end-to-end 생산 추적 경로가 현재 상태와 이력에
/// 함께 영속화되는지(설계 §19.4)를 검증한다.
///
/// 교차 모듈 검증: TrackIn은 ITrackingMasterGateway(=API의 TrackingMasterGateway 어댑터)가
/// MDM_EQUIPMENT를 조회해 설비 존재 + Plant 일치 + ValidState=='Valid'를 강제한다. 하니스가
/// FK를 OFF로 두더라도 이 앱 레벨 검증은 우회할 수 없으므로, TrackIn 전에 MDM 설비를 POST로
/// 먼저 등록한다(신규 설비의 기본 ValidState='Valid'라 IsValid 통과). Recipe는 미지정(=null)으로
/// 두어 RMS 시드를 생략한다(서비스가 RecipeDefId 빈 값이면 검증을 건너뜀).
///
/// 상태 전이(설계 §19.4.3 LotStateMachine): Create → Queued, TrackIn(Queued→Processing),
/// TrackOut(Processing→ 마지막 스텝이면 Completed). 단일 스텝 경로를 써서 1회 TrackOut으로 완료시킨다.
///
/// Mixing(POST mixing/track-in-out)은 생략한다 — 투입 Lot을 Created/Queued 상태로 복수 시드하고
/// 출력 Lot 생성/소비/관계 기록까지 거쳐야 해 입력 준비가 복잡하고, 본 테스트의 핵심(생성/추적/이력
/// 영속 + 교차모듈 설비검증)을 넘어서기 때문이다. 단건 Lot 경로로 genealogy 영속을 충분히 검증한다.
/// </summary>
public sealed class LotTrackingIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public LotTrackingIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 인증/읽기 스모크 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLots_requires_auth_and_returns_ok_for_admin()
    {
        const string plantId = "P-POM-SMOKE";

        // 1) 미인증 → 401([Authorize]가 라우팅보다 먼저 적용).
        var anon = _factory.CreateClient();
        (await anon.GetAsync($"/api/v1/lots?plantId={plantId}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET lots는 토큰 없이 401이어야 한다");

        // 2) ADMIN("*") → 200 — POM_LOT 테이블 존재 + SQLite SELECT(WHERE PLANT_ID) + 직렬화 성공(빈 목록 무방).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync($"/api/v1/lots?plantId={plantId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"POM_LOT SELECT가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<LotDto>>())
            .Should().NotBeNull("lots 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task GetTrackingReport_requires_auth_and_returns_ok_for_admin()
    {
        const string plantId = "P-POM-RPT-SMOKE";

        // 1) 미인증 → 401.
        var anon = _factory.CreateClient();
        (await anon.GetAsync($"/api/v1/reports/lot-tracking?plantId={plantId}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET lot-tracking 보고서는 토큰 없이 401이어야 한다");

        // 2) ADMIN → 200 — POM_LOT_HISTORY SELECT + 방언 페이징(SQLite LIMIT/OFFSET, WrapPaged) 성공.
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync($"/api/v1/reports/lot-tracking?plantId={plantId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"POM_LOT_HISTORY 보고서 SELECT(WrapPaged)가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<LotHistoryDto>>())
            .Should().NotBeNull("lot-tracking 보고서 응답은 JSON 배열이어야 한다");
    }

    // ── 해피패스: 생성 → TrackIn → TrackOut → 경로/이력/보고서 검증 ───────────────────

    [Fact]
    public async Task Lot_lifecycle_create_trackin_trackout_persists_state_and_history()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — perm:pom:manage 통과
        const string plantId = "P-POM-LIFE";
        const string equipmentId = "EQ-POM-LIFE-1";
        const string lotId = "LOT-POM-LIFE-1";
        const string productId = "PROD-POM-1";
        const string step = "CUT";   // 단일 스텝 경로 — 1회 TrackOut으로 Completed에 도달.

        // 0) 설비 등록 — TrackIn의 ITrackingMasterGateway 검증(설비 존재 + Plant 일치 + ValidState='Valid')을
        //    통과하려면 MDM_EQUIPMENT가 선행되어야 한다(하니스 FK OFF여도 앱 레벨 검증은 우회 불가).
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "POM Lifecycle Equipment",
            plantId,
            areaId = "A-POM",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-POM"
        });
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 TrackIn 설비 검증을 통과한다");

        // 1) Lot 생성(POST /api/v1/lots → POM_LOT INSERT). WorkOrderId는 null이라 생산지시 선행이 불필요.
        //    설계 §19.4.3 적응: 생성 즉시 Queued(공정 대기).
        var createResp = await client.PostAsJsonAsync("/api/v1/lots", new
        {
            plantId,
            lotId,
            workOrderId = (string?)null,
            productId,
            qty = 100m,
            routeSteps = new[] { step }
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Lot 생성(POM_LOT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<LotDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(lotId, "POST 응답은 생성된 Lot(Result.Value)이어야 한다");
        created.State.Should().Be("Queued", "생성 직후 Lot 상태는 Queued여야 한다(설계 §19.4.3 적응)");
        created.PlantId.Should().Be(plantId);
        created.ProductId.Should().Be(productId);

        // 2) TrackIn(POST {lotId}/track-in → POM_LOT UPDATE + POM_LOT_HISTORY INSERT).
        //    RecipeDefId 미지정(null) — RMS 시드 없이 Recipe 검증을 건너뛴다.
        var trackInResp = await client.PostAsJsonAsync($"/api/v1/lots/{lotId}/track-in", new
        {
            plantId,
            equipmentId,
            recipeDefId = (string?)null,
            recipeDefVersion = (int?)null
        });
        var trackInBody = await trackInResp.Content.ReadAsStringAsync();
        trackInResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"TrackIn(설비 점유 + Processing 전이 + 이력 1행)이 성공해야 한다. 응답 본문: {trackInBody}");

        var trackedIn = await trackInResp.Content.ReadFromJsonAsync<LotDto>();
        trackedIn.Should().NotBeNull();
        trackedIn!.State.Should().Be("Processing", "TrackIn 후 상태는 Processing이어야 한다");
        trackedIn.EquipmentId.Should().Be(equipmentId, "TrackIn 중에는 설비를 점유한다");

        // 3) TrackOut(POST {lotId}/track-out → POM_LOT UPDATE + 이력 추가).
        //    단일 스텝이라 마지막 스텝 → Completed. 설비를 반납(null)한다. 불량 없음.
        var trackOutResp = await client.PostAsJsonAsync($"/api/v1/lots/{lotId}/track-out", new
        {
            plantId,
            equipmentId,
            qty = 100m,
            defects = (object?)null,
            carrierId = (string?)null
        });
        var trackOutBody = await trackOutResp.Content.ReadAsStringAsync();
        trackOutResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"TrackOut(설비 반납 + Completed 전이 + 이력)가 성공해야 한다. 응답 본문: {trackOutBody}");

        var trackedOut = await trackOutResp.Content.ReadFromJsonAsync<LotDto>();
        trackedOut.Should().NotBeNull();
        trackedOut!.State.Should().Be("Completed", "마지막 스텝 TrackOut 후 상태는 Completed여야 한다");
        trackedOut.EquipmentId.Should().BeNull("TrackOut 시 설비 점유를 반납해야 한다");

        // 4) 단건 조회(GET {lotId} → POM_LOT SELECT) — Completed로 영속화됐는지 확인.
        var single = await client.GetFromJsonAsync<LotDto>($"/api/v1/lots/{lotId}");
        single.Should().NotBeNull();
        single!.Id.Should().Be(lotId);
        single.State.Should().Be("Completed", "TrackOut 결과가 POM_LOT에 영속화돼야 한다(읽기 경로 상태 손실 없음)");

        // 5) 목록 조회(GET lots?plantId&state) — Completed 필터로 방금 Lot이 잡혀야 한다.
        var completedList = await client.GetFromJsonAsync<List<LotDto>>(
            $"/api/v1/lots?plantId={plantId}&state=Completed");
        completedList.Should().NotBeNull();
        completedList!.Should().ContainSingle(l => l.Id == lotId)
            .Which.State.Should().Be("Completed");

        // 6) 경로/이력 조회(GET {lotId}/route → POM_LOT + POM_LOT_HISTORY + POM_LOT_MIXING_RELATION).
        //    genealogy: TrackIn → TrackOut → Finish 이력이 추가 기록(append-only)돼야 한다.
        var route = await client.GetFromJsonAsync<LotRouteDto>($"/api/v1/lots/{lotId}/route");
        route.Should().NotBeNull();
        route!.Lot.Id.Should().Be(lotId);
        route.Lot.State.Should().Be("Completed");
        route.Histories.Should().NotBeNull();
        route.Histories.Should().Contain(h => h.ExecutionId == "TrackIn",
            "TrackIn 이력이 POM_LOT_HISTORY에 기록돼야 한다");
        route.Histories.Should().Contain(h => h.ExecutionId == "TrackOut",
            "TrackOut 이력이 POM_LOT_HISTORY에 기록돼야 한다");
        route.Histories.Should().Contain(h => h.ExecutionId == "Finish",
            "마지막 스텝 완료 시 Finish 이력이 추가돼야 한다");
        route.Histories.Should().OnlyContain(h => h.LotId == lotId);
        route.MixingInputs.Should().NotBeNull().And.BeEmpty(
            "Mixing 투입이 없는 단건 Lot은 mixing 관계가 비어야 한다");

        // 7) 생산 추적 보고서(GET /api/v1/reports/lot-tracking?plantId&lotId) — POM_LOT_HISTORY 필터 조회.
        var report = await client.GetFromJsonAsync<List<LotHistoryDto>>(
            $"/api/v1/reports/lot-tracking?plantId={plantId}&lotId={lotId}");
        report.Should().NotBeNull();
        report!.Should().NotBeEmpty("보고서는 해당 Lot의 이력을 반환해야 한다");
        report.Should().OnlyContain(h => h.LotId == lotId);
        report.Should().Contain(h => h.ExecutionId == "TrackIn")
            .And.Contain(h => h.ExecutionId == "TrackOut");
    }

    // ── Hold / Release-Hold 스모크 ─────────────────────────────────────────────────

    [Fact]
    public async Task Hold_then_release_hold_round_trips_and_blocks_trackin_while_held()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string plantId = "P-POM-HOLD";
        const string equipmentId = "EQ-POM-HOLD-1";
        const string lotId = "LOT-POM-HOLD-1";

        // 설비 + Lot 시드.
        (await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId, equipmentName = "POM Hold Equipment", plantId,
            areaId = "A-POM", equipmentType = "GENERIC", equipmentClassId = "CLS-POM"
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/v1/lots", new
        {
            plantId, lotId, workOrderId = (string?)null,
            productId = "PROD-POM-1", qty = 50m, routeSteps = new[] { "CUT" }
        })).StatusCode.Should().Be(HttpStatusCode.OK, "Hold 테스트용 Lot 생성이 성공해야 한다");

        // 1) Hold(PUT {lotId}/hold) → 204 NoContent — POM_LOT.IS_HOLD='Y' 영속.
        var holdResp = await client.PutAsync($"/api/v1/lots/{lotId}/hold", null);
        holdResp.StatusCode.Should().Be(HttpStatusCode.NoContent, "Hold 성공은 204 NoContent여야 한다");

        // 읽기 경로로 IS_HOLD가 'Y'로 영속됐는지 확인(컬럼 매핑/CHAR(1) 왕복).
        var held = await client.GetFromJsonAsync<LotDto>($"/api/v1/lots/{lotId}");
        held!.IsHold.Should().BeTrue("Hold 후 IS_HOLD가 'Y'로 영속화돼야 한다");

        // 2) Hold 상태에서 TrackIn은 400(도메인 검증으로 차단) — 상태 손실/우회 없음.
        var blockedTrackIn = await client.PostAsJsonAsync($"/api/v1/lots/{lotId}/track-in", new
        {
            plantId, equipmentId, recipeDefId = (string?)null, recipeDefVersion = (int?)null
        });
        blockedTrackIn.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Hold 상태 Lot의 TrackIn은 도메인 검증으로 400이어야 한다");

        // 3) Release-Hold(PUT {lotId}/release-hold) → 204 — 다시 TrackIn 가능 상태로.
        var releaseResp = await client.PutAsync($"/api/v1/lots/{lotId}/release-hold", null);
        releaseResp.StatusCode.Should().Be(HttpStatusCode.NoContent, "Release-Hold 성공은 204여야 한다");

        var released = await client.GetFromJsonAsync<LotDto>($"/api/v1/lots/{lotId}");
        released!.IsHold.Should().BeFalse("Release-Hold 후 IS_HOLD가 'N'으로 영속화돼야 한다");
        released.State.Should().Be("Queued", "Hold/Release는 생산 상태(Queued)를 바꾸지 않아야 한다");
    }

    // ── 검증 경로(에러) 스모크 ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetRoute_for_missing_lot_returns_404()
    {
        // GetRoute는 Lot 미존재 시 NotFound(404)를 반환해야 한다(읽기 경로 NotFound 매핑).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/lots/LOT-DOES-NOT-EXIST/route");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "존재하지 않는 Lot의 route 조회는 404여야 한다");
    }

    [Fact]
    public async Task TrackIn_with_unregistered_equipment_returns_400_not_500()
    {
        // TrackIn은 MDM에 없는 설비에 대해 교차모듈 검증으로 400(NotFound→BadRequest)을 반환해야 한다.
        // (FK 위반 500이 아니라 — 하니스 FK OFF이지만 앱 레벨 게이트웨이 검증이 먼저 차단)
        var client = _factory.CreateAuthenticatedClient();
        const string plantId = "P-POM-NOEQ";
        const string lotId = "LOT-POM-NOEQ-1";

        (await client.PostAsJsonAsync("/api/v1/lots", new
        {
            plantId, lotId, workOrderId = (string?)null,
            productId = "PROD-POM-1", qty = 10m, routeSteps = new[] { "CUT" }
        })).StatusCode.Should().Be(HttpStatusCode.OK, "검증 테스트용 Lot 생성이 성공해야 한다");

        var resp = await client.PostAsJsonAsync($"/api/v1/lots/{lotId}/track-in", new
        {
            plantId, equipmentId = "EQ-NOPE", recipeDefId = (string?)null, recipeDefVersion = (int?)null
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "MDM에 없는 설비로의 TrackIn은 게이트웨이 검증으로 400을 반환해야 한다");
    }

    // ── DTO (JSON camelCase, 대소문자 무시 역직렬화) ────────────────────────────────
    // Lot.Id는 Entity<TId> 기반 식별자 → JSON "id". State/ProcessState는 JsonStringEnumConverter로 문자열.
    private sealed record LotDto(
        string Id, string PlantId, string? WorkOrderId, string ProductId,
        decimal Qty, decimal DefectQty, string State, string ProcessState,
        IReadOnlyList<string> RouteSteps, int CurrentStepIndex, string CurrentProcessId,
        string? EquipmentId, string? RecipeDefId, int? RecipeDefVersion, string? CarrierId, bool IsHold);

    private sealed record LotHistoryDto(
        long LotHistoryId, string PlantId, string LotId, string? EquipmentId, string ProcessId,
        string? RecipeDefId, int? RecipeDefVersion, string ExecutionId, string ExecutionUser,
        decimal Qty, decimal DefectQty, string LotState, string ProcessState);

    private sealed record LotMixingRelationDto(
        string PlantId, string OutputLotId, string InputLotId, decimal InputQty, decimal? MixingRate);

    // GET {lotId}/route → LotRouteView { lot, histories, mixingInputs }.
    private sealed record LotRouteDto(
        LotDto Lot, IReadOnlyList<LotHistoryDto> Histories, IReadOnlyList<LotMixingRelationDto> MixingInputs);
}
