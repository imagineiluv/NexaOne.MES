using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Est;

/// <summary>
/// EST 설비상태 추적 HTTP 통합 테스트 — V025 마이그레이션으로 생성되는 3개 테이블
/// (EST_EQUIPMENT_STATE / _HISTORY / EST_STATE_MATRIX)이 SQLite에서 실제로 동작하는지,
/// 그리고 상태 변경 시 현재 상태와 이력이 함께 기록되는지(Wave4 원자성)를 end-to-end로 검증한다.
/// EST_EQUIPMENT_STATE는 MDM_EQUIPMENT를 FK 참조하므로(운영 무결성) 먼저 설비를 등록한다.
/// </summary>
public sealed class EquipmentStateIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public EquipmentStateIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ChangeState_persists_current_state_and_history_atomically()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — mdm/est:manage 정책 통과
        const string plantId = "P-EST";
        const string equipmentId = "EQ-EST-1";

        // 0) 설비 등록 — EST_EQUIPMENT_STATE.EQUIPMENT_ID는 MDM_EQUIPMENT FK라 선행되어야 한다.
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "EST Test Equipment",
            plantId,
            areaId = "A-EST",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-EST"
        });
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK, "설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다");

        // 1) 전이 매트릭스 등록: IDLE → RUN 허용(설정 상태 RUN), 사유 불필요.
        var matrixResp = await client.PostAsJsonAsync("/api/v1/est/state-matrix", new
        {
            plantId,
            fromStateId = "IDLE",
            toStateId = "RUN",
            allowFlag = true,
            setStateId = "RUN",
            requireReason = false
        });
        matrixResp.StatusCode.Should().Be(HttpStatusCode.OK, "매트릭스 등록(EST_STATE_MATRIX INSERT)이 성공해야 한다");

        // 2) 상태 변경: 미존재 상태행은 IDLE로 부트스트랩된 뒤 IDLE→RUN 전이된다.
        var changeResp = await client.PostAsJsonAsync("/api/v1/est/equipment-state/change", new
        {
            equipmentId,
            plantId,
            toState = "RUN",
            reason = (string?)null,
            expectedVersion = (int?)null
        });
        var changeBody = await changeResp.Content.ReadAsStringAsync();
        changeResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"EST_EQUIPMENT_STATE 업서트 + 이력 기록이 성공해야 한다. 응답 본문: {changeBody}");

        var changed = await changeResp.Content.ReadFromJsonAsync<StateDto>();
        changed.Should().NotBeNull();
        changed!.CurrentStateId.Should().Be("RUN", "전이 후 현재 상태는 매트릭스의 SET_STATE_ID(RUN)여야 한다");

        // 3) 현재 상태 조회(EST_EQUIPMENT_STATE SELECT) — RUN으로 영속화됐는지 확인.
        var states = await client.GetFromJsonAsync<List<StateDto>>($"/api/v1/est/equipment-state?plantId={plantId}");
        states.Should().NotBeNull();
        states!.Should().ContainSingle(s => s.Id == equipmentId)
            .Which.CurrentStateId.Should().Be("RUN");

        // 4) 이력 조회(EST_EQUIPMENT_STATE_HISTORY SELECT) — 상태 변경과 원자적으로 1행 기록됐는지 확인.
        var history = await client.GetFromJsonAsync<List<HistoryDto>>(
            $"/api/v1/est/equipment-state/{equipmentId}/history");
        history.Should().NotBeNull();
        history!.Should().ContainSingle(h => h.FromState == "IDLE" && h.ToState == "RUN",
            "상태 변경과 이력 기록이 한 트랜잭션으로 함께 남아야 한다(부분 커밋 없음)");
    }

    [Fact]
    public async Task ChangeState_for_unregistered_equipment_returns_400_not_500()
    {
        // A-1 회귀 — 미등록 설비 상태변경은 FK 위반 500이 아니라 명확한 400(검증 오류)이어야 한다.
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/v1/est/state-matrix", new
        {
            plantId = "P-NOEQ", fromStateId = "IDLE", toStateId = "RUN",
            allowFlag = true, setStateId = "RUN", requireReason = false
        });

        var resp = await client.PostAsJsonAsync("/api/v1/est/equipment-state/change", new
        {
            equipmentId = "EQ-DOES-NOT-EXIST", plantId = "P-NOEQ", toState = "RUN",
            reason = (string?)null, expectedVersion = (int?)null
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "MDM에 없는 설비는 사전 검증으로 400을 반환해야 한다(FK 위반 500이 아니라)");
    }

    private sealed record StateDto(string Id, string PlantId, string CurrentStateId, int StateVersion);
    private sealed record HistoryDto(string Id, string FromState, string ToState, string SetState, string ChangedBy);
}
