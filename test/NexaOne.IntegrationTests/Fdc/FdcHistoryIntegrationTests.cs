using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FdcController 이력/잔여 읽기 경로 HTTP 통합 테스트 — 기존 FdcControllerIntegrationTests(읽기 스모크)·
/// FdcWriteIntegrationTests(쓰기 해피패스)에서 아직 검증되지 않은 GET 엔드포인트를 end-to-end로 덮는다.
/// 마이그레이션으로 생성되는 이력/시계열/마스터 테이블이 SQLite에서 실제로 SELECT 되는지
/// (테이블 존재 + 방언 SELECT + 페이징 + 인증 + 라우팅 + 직렬화)를 검증한다.
///
/// 대상 GET:
///   interlock-history (FDC_INTERLOCK_HISTORY V018, WHERE EQUIPMENT_ID AND TRIGGERED_AT BETWEEN)
///   alarm-history     (FDC_ALARM_HISTORY     V021, WHERE EQUIPMENT_ID AND OCCURRED_AT  BETWEEN)
///   collect-data      (FDC_COLLECT_DATA      V017, WHERE PARAMETER_ID AND COLLECTED_AT BETWEEN)
///   collect-data/latest (FDC_COLLECT_DATA, WrapPaged 페이징 — SQLite LIMIT/OFFSET 경로 증명)
///   parameters        (FDC_PARAMETER         V016, WHERE EQUIPMENT_ID — SELECT * 라운드트립)
///   alarm-configs     (FDC_ALARM_CONFIG      V020, WHERE EQUIPMENT_ID)
///   equipment/state   (PlantController 상태 — DB 무접근, 인증/라우팅/직렬화 스모크)
///
/// 인가: 위 GET들은 컨트롤러 [Authorize]만 적용(추가 권한 정책 없음) — 미인증 401 + 임의 인증 200.
/// 토큰은 ADMIN("*")으로 발급한다. 이력 리포지토리(IFdcInterlockHistoryRepository/IFdcAlarmHistoryRepository)는
/// DI에 등록되어 있어(ServiceCollectionExtensions) GetHistoryAsync가 빈 배열 단락(null repo)이 아닌
/// 실제 SQLite SELECT를 친다 — 즉 본 테스트가 이력 테이블의 방언/컬럼 정합을 실증한다.
///
/// 시드 없이 빈 결과(200 + 빈 JSON 배열)를 기대한다(읽기 전용 스모크). FK는 테스트 하니스가
/// PRAGMA foreign_keys=OFF로 부트스트랩하므로 부모 선행 시드가 불필요하다.
///
/// 시간 범위(from/to)는 ISO-8601("o")로 직렬화해 쿼리스트링에 싣는다 — 컨트롤러가 [FromQuery] DateTime으로
/// 바인딩하고 리포지토리가 @from/@to 파라미터로 SQLite에 전달한다(DATETIME2→TEXT 방언 비교 경로).
/// </summary>
public sealed class FdcHistoryIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FdcHistoryIntegrationTests(TestApiFactory factory) => _factory = factory;

    // 결정적 기간 — UTC now 기준 ±1일. ISO-8601 round-trip("o")로 쿼리스트링 인코딩.
    private static string Range(out string toEncoded)
    {
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        toEncoded = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        return from;
    }

    // ── interlock-history (FDC_INTERLOCK_HISTORY, V018) ──────────────────────────

    [Fact]
    public async Task GetInterlockHistory_requires_auth()
    {
        var from = Range(out var to);
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync(
            $"/api/v1/fdc/interlock-history?equipmentId=EQ-X&from={from}&to={to}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET interlock-history는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetInterlockHistory_returns_ok_empty_for_admin()
    {
        var from = Range(out var to);
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync(
            $"/api/v1/fdc/interlock-history?equipmentId=FDC-IT-HIST-NONE&from={from}&to={to}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC_INTERLOCK_HISTORY SELECT(WHERE EQUIPMENT_ID AND TRIGGERED_AT BETWEEN)가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<InterlockHistoryDto>>();
        list.Should().NotBeNull("interlock-history 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 이력이어야 한다");
    }

    // ── alarm-history (FDC_ALARM_HISTORY, V021) ──────────────────────────────────

    [Fact]
    public async Task GetAlarmHistory_requires_auth()
    {
        var from = Range(out var to);
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync(
            $"/api/v1/fdc/alarm-history?equipmentId=EQ-X&from={from}&to={to}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET alarm-history는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetAlarmHistory_returns_ok_empty_for_admin()
    {
        var from = Range(out var to);
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync(
            $"/api/v1/fdc/alarm-history?equipmentId=FDC-IT-ALARMHIST-NONE&from={from}&to={to}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC_ALARM_HISTORY SELECT(WHERE EQUIPMENT_ID AND OCCURRED_AT BETWEEN)가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<AlarmHistoryDto>>();
        list.Should().NotBeNull("alarm-history 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 이력이어야 한다");
    }

    // ── collect-data (FDC_COLLECT_DATA, V017 — 기간 조회) ─────────────────────────

    [Fact]
    public async Task GetCollectData_requires_auth()
    {
        var from = Range(out var to);
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync(
            $"/api/v1/fdc/collect-data?parameterId=P-X&from={from}&to={to}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET collect-data는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetCollectData_returns_ok_empty_for_admin()
    {
        var from = Range(out var to);
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync(
            $"/api/v1/fdc/collect-data?parameterId=FDC-IT-CDATA-NONE&from={from}&to={to}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC_COLLECT_DATA SELECT(WHERE PARAMETER_ID AND COLLECTED_AT BETWEEN)가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<CollectDataDto>>();
        list.Should().NotBeNull("collect-data 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 수집 데이터여야 한다");
    }

    // 회귀 가드 — GetCollectData가 무제한 시계열을 끌어오지 않도록 limit가 SQL LIMIT/TOP로 스며들어
    // 결과를 클램프하는지 증명한다. 리포지토리로 결정론적 N건을 직접 시드(HTTP보다 빠르고 안정적)한 뒤,
    // (1) 상한 미만의 명시 limit이 정확히 그만큼만 반환(LIMIT 적용 증명)하고,
    // (2) 과대 limit(999999)이 상한(5000)으로 클램프되어 오류 없이 시드 전량을 반환함을 확인한다.
    private static async Task SeedCollectDataAsync(IServiceProvider sp, string parameterId, int count, DateTime baseTime)
    {
        var repo = sp.GetRequiredService<IFdcCollectDataRepository>();
        var rows = Enumerable.Range(0, count).Select(i =>
            FdcCollectData.Create(
                $"{parameterId}-CD-{i}", "FDC-IT-EQ-CLAMP", parameterId, 10m + i,
                baseTime.AddSeconds(i), "Good", 0m, 100m).Value);
        await repo.AddBatchAsync(rows);
    }

    [Fact]
    public async Task GetCollectData_clamps_result_to_limit_and_caps_over_large_limit()
    {
        const string parameterId = "FDC-IT-CDATA-CLAMP";
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        // 결정론적으로 5건 시드(상한 5000 미만).
        using (var scope = _factory.Services.CreateScope())
            await SeedCollectDataAsync(scope.ServiceProvider, parameterId, 5, baseTime);

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        var client = _factory.CreateAuthenticatedClient();

        // (1) limit=3 < 시드(5) — LIMIT가 SQL에 스며들어 정확히 3건만 반환되어야 한다("at most the cap").
        var bounded = await client.GetFromJsonAsync<List<CollectDataDto>>(
            $"/api/v1/fdc/collect-data?parameterId={parameterId}&from={from}&to={to}&limit=3");
        bounded.Should().NotBeNull();
        bounded!.Should().HaveCount(3, "명시 limit(3)이 SQL LIMIT/TOP로 적용돼 결과를 상한으로 잘라야 한다");

        // (2) 과대 limit(999999) — Math.Clamp(…,1,5000)로 상한 클램프. 무검증 통과면 페이징 SQL이 깨지거나
        //     무제한 조회가 되지만, 클램프가 가드하므로 오류 없이 시드 전량(5건, 5000 미만)을 반환해야 한다.
        var capped = await client.GetFromJsonAsync<List<CollectDataDto>>(
            $"/api/v1/fdc/collect-data?parameterId={parameterId}&from={from}&to={to}&limit=999999");
        capped.Should().NotBeNull();
        capped!.Should().HaveCount(5, "과대 limit은 상한(5000)으로 클램프되며, 시드(5)가 상한 미만이므로 전량 반환되어야 한다");
    }

    // 일괄 적재 가드 — AddBatchAsync가 단일 트랜잭션 일괄 INSERT(ExecuteManyAsync)로 전환된 뒤에도
    // 정확히 N행이 적재되는지 증명한다. 결정론적 N건을 배치 시드한 뒤, 상한보다 큰 limit로 전량을
    // 읽어 시드 건수와 일치함을 확인한다(부분 적재·중복·누락 회귀 탐지).
    [Fact]
    public async Task AddBatch_inserts_exactly_n_rows_in_single_batch()
    {
        const string parameterId = "FDC-IT-CDATA-BATCH";
        const int seedCount = 12;
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        using (var scope = _factory.Services.CreateScope())
            await SeedCollectDataAsync(scope.ServiceProvider, parameterId, seedCount, baseTime);

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        var client = _factory.CreateAuthenticatedClient();

        var rows = await client.GetFromJsonAsync<List<CollectDataDto>>(
            $"/api/v1/fdc/collect-data?parameterId={parameterId}&from={from}&to={to}&limit=1000");
        rows.Should().NotBeNull();
        rows!.Should().HaveCount(seedCount,
            "AddBatchAsync 단일 트랜잭션 일괄 INSERT는 시드한 정확히 N행만 적재해야 한다");
    }

    // ── collect-data/latest (FDC_COLLECT_DATA, WrapPaged — SQLite LIMIT/OFFSET) ───

    [Fact]
    public async Task GetLatestCollectData_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync(
            "/api/v1/fdc/collect-data/latest?parameterId=P-X&limit=10");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET collect-data/latest는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetLatestCollectData_paged_query_runs_on_sqlite_and_returns_ok()
    {
        var client = _factory.CreateAuthenticatedClient();
        // limit를 명시해 _dialect.WrapPaged(...LIMIT @limit OFFSET 0) 페이징 SQL이 SQLite에서 파싱/실행됨을 증명.
        var resp = await client.GetAsync(
            "/api/v1/fdc/collect-data/latest?parameterId=FDC-IT-LATEST-NONE&limit=25");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC_COLLECT_DATA WrapPaged(LIMIT/OFFSET) 페이징 SELECT가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<CollectDataDto>>();
        list.Should().NotBeNull("collect-data/latest 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 최근 수집 데이터여야 한다");
    }

    [Fact]
    public async Task GetLatestCollectData_uses_default_limit_when_omitted()
    {
        // limit 미지정 → 컨트롤러 기본값 50 + Clamp(1..500). 기본값 경로도 SQLite 페이징에서 동작해야 한다.
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync(
            "/api/v1/fdc/collect-data/latest?parameterId=FDC-IT-LATEST-DEFAULT");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"limit 생략 시 기본값(50)으로도 WrapPaged 페이징이 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<CollectDataDto>>();
        list.Should().NotBeNull("collect-data/latest 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 최근 수집 데이터여야 한다");
    }

    // ── parameters (FDC_PARAMETER, V016 — SELECT * 라운드트립) ────────────────────

    [Fact]
    public async Task GetParameters_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/v1/fdc/parameters?equipmentId=EQ-X");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET parameters는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetParameters_returns_ok_empty_for_admin()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/fdc/parameters?equipmentId=FDC-IT-PARAM-NONE");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC_PARAMETER SELECT(WHERE EQUIPMENT_ID, SELECT * 라운드트립)가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<ParameterDto>>();
        list.Should().NotBeNull("parameters 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 파라미터 목록이어야 한다");
    }

    // ── alarm-configs (FDC_ALARM_CONFIG, V020) ───────────────────────────────────

    [Fact]
    public async Task GetAlarmConfigs_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/v1/fdc/alarm-configs?equipmentId=EQ-X");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET alarm-configs는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetAlarmConfigs_returns_ok_empty_for_admin()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/fdc/alarm-configs?equipmentId=FDC-IT-ACFG-NONE");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"FDC_ALARM_CONFIG SELECT(WHERE EQUIPMENT_ID)가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<AlarmConfigDto>>();
        list.Should().NotBeNull("alarm-configs 응답은 JSON 배열이어야 한다");
        list!.Should().BeEmpty("시드가 없으므로 빈 알람 설정 목록이어야 한다");
    }

    // ── equipment/state (PlantController — DB 무접근 상태 스냅샷) ──────────────────

    [Fact]
    public async Task GetPlantState_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/v1/fdc/equipment/state");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] GET equipment/state는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetPlantState_returns_ok_snapshot_for_admin()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/fdc/equipment/state");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"equipment/state는 인증된 호출에 PlantController 상태 스냅샷(200)을 반환해야 한다. 응답 본문: {body}");

        // 미초기화 StateMachine이면 State='Uninitialized', OperationMode='None', MachineCount=0 (DB 무접근).
        var state = await resp.Content.ReadFromJsonAsync<PlantStateDto>();
        state.Should().NotBeNull("equipment/state 응답은 상태 스냅샷 객체여야 한다");
        state!.State.Should().NotBeNullOrEmpty("StateMachine 미초기화 시 'Uninitialized'를 반환해야 한다");
        state.OperationMode.Should().NotBeNullOrEmpty();
        state.Machines.Should().NotBeNull("Machines는 (빈) JSON 배열이어야 한다");
    }

    // ── DTOs — 컨트롤러가 도메인 엔티티(Result.Value)/익명형을 그대로 Ok()로 직렬화(JSON camelCase).
    //    도메인 프로퍼티명과 일치시킨다(Id는 Entity<TId> 식별자).

    private sealed record InterlockHistoryDto(
        string Id, string RuleId, string EquipmentId, string ParameterId,
        decimal TriggerValue, string Action, string Message, bool IsResolved);

    private sealed record AlarmHistoryDto(
        string Id, string AlarmConfigId, string EquipmentId, string ParameterId,
        string AlarmLevel, decimal TriggerValue, string Message, bool IsCleared);

    private sealed record CollectDataDto(
        string Id, string EquipmentId, string ParameterId, decimal Value,
        string Quality, decimal LowerLimit, decimal UpperLimit, bool IsOutOfSpec);

    private sealed record ParameterDto(
        string Id, string ParameterName, string EquipmentId, string? GroupId, string Unit,
        decimal LowerLimit, decimal UpperLimit, bool IsActive);

    private sealed record AlarmConfigDto(
        string Id, string EquipmentId, string ParameterId,
        string AlarmLevel, string Operator, decimal ThresholdValue, bool IsActive);

    private sealed record MachineDto(string Name, string State);

    private sealed record PlantStateDto(
        string State, string OperationMode, int MachineCount, List<MachineDto> Machines);
}
