using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.POM;

/// <summary>
/// POM Lot Mixing(혼합 투입) HTTP 통합 테스트 — 기존 LotTrackingIntegrationTests가 명시적으로 제외한
/// POST /api/v1/lots/mixing/track-in-out(설계 19.4.7) 경로를 end-to-end로 검증한다.
///
/// 검증 대상(미검증 경로): 투입 Lot 복수 소비 → 출력 Lot 생성/추적 → POM_LOT_MIXING_RELATION 영속.
///   1) 테이블 존재 + 방언: POM_LOT_MIXING_RELATION INSERT/SELECT가 SQLite에서 동작.
///   2) 인증/인가: 미인증 401 + 권한없는 토큰("pom:read") 403 + ADMIN 200(쓰기 정책 perm:pom:manage).
///   3) 직렬화: MixingTrackRequest(중첩 inputs[]) 바인딩 + 응답 Lot/route 역직렬화.
///   4) 영속/되읽기: GET {outputLotId}/route 로 MixingInputs(투입 2건·InputQty)와 출력 Lot 상태,
///      투입 Lot의 소비 상태가 영속·재구성되는지 확인.
///   5) 비-부인성: 혼합 관계의 ConsumedBy가 본문이 아닌 토큰(sub="test-admin")으로 기록되는지 되읽기로 확인
///      (LotController.MixingTrackInOut → CurrentUserId() 사용).
///   6) 부분커밋 없음: 투입 Lot 중 하나가 미존재면 400, 다른 투입 Lot은 소비되지 않고 출력 Lot도 생성되지 않음
///      (서비스가 전 입력을 검증 후에만 상태 변경 — UnitOfWork 부재 적응).
///
/// 교차 모듈/하니스 사실(LotTrackingIntegrationTests와 동일):
///   - ITrackingMasterGateway가 MDM_EQUIPMENT 존재 + Plant 일치 + ValidState='Valid'를 강제하므로
///     Mixing 전에 설비를 POST로 등록한다(하니스 FK OFF여도 앱 레벨 게이트웨이 검증은 우회 불가).
///   - 투입 Lot은 POST /lots로 생성(생성 즉시 Queued)하면 LotStateMachine상 Consumed로 전이 가능.
///   - 신규 출력 Lot 생성은 Lot.Create를 거치므로 OutputRouteSteps가 최소 1개 필요하다(단일 스텝 →
///     TrackIn→TrackOut 연속 수행으로 출력 Lot은 Completed에 도달).
///   - CreateAuthenticatedClient()는 기본 "*"(ADMIN, sub="test-admin")라 perm:pom:manage 정책을 통과한다.
///
/// 주의(코드 사실): Lot.Consume()은 LOT_STATE만 Consumed로 바꾸고 QTY는 변경하지 않는다(차감 없음).
///   따라서 투입 Lot의 소비 검증은 '상태=Consumed' + 'Qty 불변'으로 단언한다(코드의 실제 동작).
/// </summary>
public sealed class LotMixingIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public LotMixingIntegrationTests(TestApiFactory factory) => _factory = factory;

    private static object EquipmentBody(string equipmentId, string plantId) => new
    {
        equipmentId,
        equipmentName = "POM Mixing Equipment",
        plantId,
        areaId = "A-POM",
        equipmentType = "GENERIC",
        equipmentClassId = "CLS-POM"
    };

    private static object CreateLotBody(string plantId, string lotId, string productId, decimal qty) => new
    {
        plantId,
        lotId,
        workOrderId = (string?)null,
        productId,
        qty,
        routeSteps = new[] { "CUT" }
    };

    // ── 인가: 미인증 401 / 권한없음 403 ─────────────────────────────────────────────

    [Fact]
    public async Task MixingTrackInOut_requires_auth()
    {
        // 토큰 없는 POST mixing/track-in-out은 [Authorize]가 모델바인딩보다 먼저 적용돼 401이어야 한다.
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/lots/mixing/track-in-out", new
        {
            plantId = "P-POM-MIX-AUTH",
            outputLotId = "LOT-MIX-AUTH-OUT",
            productId = "PROD-MIX",
            equipmentId = "EQ-MIX-AUTH",
            outputRouteSteps = new[] { "MIX" },
            inputs = new[] { new { lotId = "LOT-IN-1", inQty = 1m } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 mixing/track-in-out은 [Authorize]로 401이어야 한다");
    }

    [Fact]
    public async Task MixingTrackInOut_forbidden_without_pom_manage_permission()
    {
        // ADR-003: 쓰기는 perm:pom:manage 필요. "pom:read"만 가진 토큰은 정책 미충족으로 403이어야 한다
        // (PermissionAuthorizationHandler는 정확 권한/와일드카드만 통과; ADMIN 역할은 자동 통과시키지 않음).
        var client = _factory.CreateAuthenticatedClient("pom:read");
        var resp = await client.PostAsJsonAsync("/api/v1/lots/mixing/track-in-out", new
        {
            plantId = "P-POM-MIX-FORBID",
            outputLotId = "LOT-MIX-FORBID-OUT",
            productId = "PROD-MIX",
            equipmentId = "EQ-MIX-FORBID",
            outputRouteSteps = new[] { "MIX" },
            inputs = new[] { new { lotId = "LOT-IN-1", inQty = 1m } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "perm:pom:manage 없는 토큰의 mixing 쓰기는 403이어야 한다");
    }

    // ── 해피패스: 투입 2건 → 출력 Lot 생성 → 관계/소비/되읽기 영속 ────────────────────

    [Fact]
    public async Task MixingTrackInOut_consumes_inputs_and_persists_relations_and_output()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN, sub="test-admin") — perm:pom:manage 통과
        const string plantId = "P-POM-MIX";
        const string equipmentId = "EQ-POM-MIX-1";
        const string inputLot1 = "LOT-MIX-IN-1";
        const string inputLot2 = "LOT-MIX-IN-2";
        const string outputLot = "LOT-MIX-OUT-1";
        const string productId = "PROD-POM-MIX";
        const decimal in1Qty = 60m;
        const decimal in2Qty = 40m;
        const decimal in1Seed = 100m;   // 투입 가능 수량 ≥ 투입 수량
        const decimal in2Seed = 50m;

        // 0) 설비 등록 — Mixing 설비 검증(존재 + Plant 일치 + ValidState='Valid') 선행.
        (await client.PostAsJsonAsync("/api/v1/mdm/equipment", EquipmentBody(equipmentId, plantId)))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 Mixing 설비 검증을 통과한다");

        // 1) 투입 Lot 2건 시드(POST /lots → POM_LOT INSERT, 생성 즉시 Queued).
        var seed1 = await client.PostAsJsonAsync("/api/v1/lots", CreateLotBody(plantId, inputLot1, productId, in1Seed));
        var seed1Body = await seed1.Content.ReadAsStringAsync();
        seed1.StatusCode.Should().Be(HttpStatusCode.OK, $"투입 Lot1 생성이 성공해야 한다. 응답 본문: {seed1Body}");
        (await seed1.Content.ReadFromJsonAsync<LotDto>())!.State.Should().Be("Queued");

        var seed2 = await client.PostAsJsonAsync("/api/v1/lots", CreateLotBody(plantId, inputLot2, productId, in2Seed));
        var seed2Body = await seed2.Content.ReadAsStringAsync();
        seed2.StatusCode.Should().Be(HttpStatusCode.OK, $"투입 Lot2 생성이 성공해야 한다. 응답 본문: {seed2Body}");

        // 2) Mixing TrackIn/Out(POST mixing/track-in-out). 신규 출력 Lot 생성을 위해 outputRouteSteps 1개 지정.
        //    inputs[] 중첩 직렬화 + 투입 소비 + 출력 Lot TrackIn→TrackOut 연속 수행이 모두 SQLite에서 성공해야 한다.
        var mixResp = await client.PostAsJsonAsync("/api/v1/lots/mixing/track-in-out", new
        {
            plantId,
            outputLotId = outputLot,
            productId,
            equipmentId,
            outputRouteSteps = new[] { "MIX" },
            inputs = new[]
            {
                new { lotId = inputLot1, inQty = in1Qty },
                new { lotId = inputLot2, inQty = in2Qty }
            }
        });
        var mixBody = await mixResp.Content.ReadAsStringAsync();
        mixResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "Mixing TrackIn/Out(투입 소비 + 출력 Lot 생성 + 관계/이력 기록)이 SQLite에서 성공해야 한다. " +
            $"응답 본문: {mixBody}");

        // 응답은 출력 Lot. 신규 출력 + 단일 스텝 → TrackIn→TrackOut으로 Completed에 도달.
        var output = await mixResp.Content.ReadFromJsonAsync<LotDto>();
        output.Should().NotBeNull("Mixing 응답은 생성된 출력 Lot이어야 한다");
        output!.Id.Should().Be(outputLot);
        output.PlantId.Should().Be(plantId);
        output.ProductId.Should().Be(productId);
        output.Qty.Should().Be(in1Qty + in2Qty, "출력 Lot 수량은 투입 합계(60+40=100)여야 한다");
        output.State.Should().Be("Completed", "단일 스텝 Mixing은 TrackIn→TrackOut 연속 수행으로 Completed가 된다");

        // 3) GET {outputLotId}/route — POM_LOT_MIXING_RELATION에 투입 2건 + InputQty 영속 단언.
        var route = await client.GetFromJsonAsync<LotRouteDto>($"/api/v1/lots/{outputLot}/route");
        route.Should().NotBeNull();
        route!.Lot.Id.Should().Be(outputLot);
        route.Lot.State.Should().Be("Completed");

        route.MixingInputs.Should().NotBeNull();
        route.MixingInputs.Should().HaveCount(2, "투입 Lot 2건이 POM_LOT_MIXING_RELATION에 기록돼야 한다");
        route.MixingInputs.Should().OnlyContain(m => m.OutputLotId == outputLot && m.PlantId == plantId);

        var rel1 = route.MixingInputs.Should().ContainSingle(m => m.InputLotId == inputLot1).Which;
        rel1.InputQty.Should().Be(in1Qty, "투입 Lot1의 InputQty(60)가 영속화돼야 한다");
        var rel2 = route.MixingInputs.Should().ContainSingle(m => m.InputLotId == inputLot2).Which;
        rel2.InputQty.Should().Be(in2Qty, "투입 Lot2의 InputQty(40)가 영속화돼야 한다");

        // 비-부인성: ConsumedBy는 본문이 아닌 토큰 sub("test-admin")으로 기록돼야 한다.
        route.MixingInputs.Should().OnlyContain(m => m.ConsumedBy == "test-admin",
            "혼합 관계의 ConsumedBy는 본문이 아닌 토큰 사용자(CurrentUserId)로 기록돼야 한다(비-부인성)");

        // MixingRate(투입/합계)도 4자리 반올림으로 영속·재구성돼야 한다(NUMERIC 왕복).
        rel1.MixingRate.Should().Be(0.6m, "투입 Lot1 비율 = 60/100 = 0.6");
        rel2.MixingRate.Should().Be(0.4m, "투입 Lot2 비율 = 40/100 = 0.4");

        // 4) 투입 Lot 소비 되읽기 — 상태=Consumed로 영속(GET {inputLotId}).
        //    (코드 사실: Consume은 QTY를 차감하지 않으므로 Qty는 시드값 그대로 유지된다.)
        var consumed1 = await client.GetFromJsonAsync<LotDto>($"/api/v1/lots/{inputLot1}");
        consumed1!.State.Should().Be("Consumed", "투입 Lot1은 Mixing 후 Consumed로 영속화돼야 한다");
        consumed1.Qty.Should().Be(in1Seed, "Consume은 QTY를 변경하지 않으므로 투입 Lot1 Qty는 시드값을 유지한다");

        var consumed2 = await client.GetFromJsonAsync<LotDto>($"/api/v1/lots/{inputLot2}");
        consumed2!.State.Should().Be("Consumed", "투입 Lot2은 Mixing 후 Consumed로 영속화돼야 한다");
        consumed2.Qty.Should().Be(in2Seed, "Consume은 QTY를 변경하지 않으므로 투입 Lot2 Qty는 시드값을 유지한다");

        // 5) 출력 Lot 이력: 투입별 Consume + 출력 TrackIn/TrackOut/Finish가 POM_LOT_HISTORY에 기록.
        route.Histories.Should().NotBeNull();
        route.Histories.Should().Contain(h => h.ExecutionId == "TrackIn" && h.LotId == outputLot,
            "출력 Lot의 TrackIn 이력이 기록돼야 한다");
        route.Histories.Should().Contain(h => h.ExecutionId == "TrackOut" && h.LotId == outputLot,
            "출력 Lot의 TrackOut 이력이 기록돼야 한다");
        route.Histories.Should().Contain(h => h.ExecutionId == "Finish" && h.LotId == outputLot,
            "단일 스텝 완료 시 출력 Lot의 Finish 이력이 기록돼야 한다");
    }

    // ── 에러: 미존재 투입 Lot → 400, 부분커밋 없음 ──────────────────────────────────

    [Fact]
    public async Task MixingTrackInOut_with_missing_input_lot_returns_400_and_does_not_partially_commit()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string plantId = "P-POM-MIX-ERR";
        const string equipmentId = "EQ-POM-MIX-ERR-1";
        const string validInputLot = "LOT-MIX-ERR-VALID";
        const string missingInputLot = "LOT-MIX-ERR-MISSING";   // 시드하지 않음(미존재)
        const string outputLot = "LOT-MIX-ERR-OUT";
        const string productId = "PROD-POM-MIX-ERR";

        (await client.PostAsJsonAsync("/api/v1/mdm/equipment", EquipmentBody(equipmentId, plantId)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 유효 투입 Lot만 시드. 두 번째 투입 Lot은 존재하지 않는다.
        (await client.PostAsJsonAsync("/api/v1/lots", CreateLotBody(plantId, validInputLot, productId, 30m)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "유효 투입 Lot 생성이 성공해야 한다");

        // 서비스는 모든 투입 Lot을 상태 변경 전에 전수 검증한다 → 미존재 투입은 NotFound, 컨트롤러가 400으로 매핑.
        var mixResp = await client.PostAsJsonAsync("/api/v1/lots/mixing/track-in-out", new
        {
            plantId,
            outputLotId = outputLot,
            productId,
            equipmentId,
            outputRouteSteps = new[] { "MIX" },
            inputs = new[]
            {
                new { lotId = validInputLot, inQty = 10m },
                new { lotId = missingInputLot, inQty = 5m }
            }
        });
        var mixBody = await mixResp.Content.ReadAsStringAsync();
        mixResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"미존재 투입 Lot이 포함된 Mixing은 400이어야 한다(부분 커밋 없음). 응답 본문: {mixBody}");

        // 부분커밋 없음 #1: 유효 투입 Lot은 소비되지 않고 Queued 상태를 유지해야 한다.
        var stillQueued = await client.GetFromJsonAsync<LotDto>($"/api/v1/lots/{validInputLot}");
        stillQueued!.State.Should().Be("Queued",
            "전수 검증 실패 시 유효 투입 Lot은 소비되지 않고 Queued로 남아야 한다(부분 커밋 없음)");

        // 부분커밋 없음 #2: 출력 Lot은 생성되지 않아야 한다 → route 조회 404.
        var outRoute = await client.GetAsync($"/api/v1/lots/{outputLot}/route");
        outRoute.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "검증 실패 시 출력 Lot은 생성되지 않아야 한다(부분 커밋 없음)");
    }

    // ── DTO (JSON camelCase, 대소문자 무시 역직렬화) — 기존 LotTrackingIntegrationTests와 동일 구조 ──
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
        string PlantId, string OutputLotId, string InputLotId, decimal InputQty,
        decimal? MixingRate, DateTime ConsumedAt, string ConsumedBy);

    // GET {lotId}/route → LotRouteView { lot, histories, mixingInputs }.
    private sealed record LotRouteDto(
        LotDto Lot, IReadOnlyList<LotHistoryDto> Histories, IReadOnlyList<LotMixingRelationDto> MixingInputs);
}
