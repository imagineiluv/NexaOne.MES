using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Dashboard;

/// <summary>
/// Dashboard 집계 HTTP 통합 테스트 — GET /api/v1/dashboard(GetSummary)가 5개 모듈
/// (EST 알람 / CMMS 작업지시 / POM 생산계획 / RMS 레시피승인 / SHP 출하지시)을 가로질러
/// 집계할 때, 각 집계가 참조하는 테이블이 SQLite에 존재하고 방언(COUNT(*)/SELECT … WHERE col=@p,
/// CLEARED_AT IS NULL)이 동작하는지를 end-to-end로 검증한다.
///
/// 컨트롤러는 5개 서비스를 동시 호출(Task.WhenAll)해 단일 DashboardSummaryResponse로 합친다:
///   ActiveAlarms          ← EquipmentAlarmService.GetActiveAlarmCountAsync()
///                            (EST_EQUIPMENT_ALARM, V003 / COUNT WHERE CLEARED_AT IS NULL)
///   Issued/InProgressWO   ← CmmsService.GetByStatusAsync(WorkOrderStatus.*)
///                            (CMMS_WORK_ORDER, V008 / SELECT WHERE STATUS=@status)
///   ReleasedPlans         ← PomService.GetCountByStatusAsync(ProductionPlanStatus.Released)
///                            (POM_PRODUCTION_PLAN, V007 / COUNT WHERE STATUS=@status)
///   PendingRecipeApprovals← RecipeService.GetByStateAsync(WaitApproval/Approved1)
///                            (RMS_RECIPE, V005 / SELECT WHERE APPROVAL_STATE=@state)
///   OpenDeliveryOrders    ← ShpService.GetCountByStatusAsync(Draft/Confirmed)
///                            (SHP_DELIVERY_ORDER, V009 / COUNT WHERE STATUS=@status)
///
/// 어느 한 모듈 테이블이라도 마이그레이션에서 누락되거나 MSSQL 전용 집계 함수(DATEPART/CONVERT/TOP)를
/// 썼다면 Task.WhenAll에서 예외가 전파되어 500이 떴을 것이다 — 모든 집계가 단순 COUNT/equality라
/// SQLite에서도 동작하며, 시드 없는 빈 DB에서도 전 필드 0으로 200이어야 한다.
/// </summary>
public sealed class DashboardIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public DashboardIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetSummary_without_token_returns_401()
    {
        // [Authorize]가 라우팅보다 먼저 적용되는지 — 토큰 없는 요청은 401이어야 한다.
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/v1/dashboard");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]가 적용된 GET dashboard는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetSummary_for_admin_returns_200_with_aggregated_zero_counts_on_empty_db()
    {
        // ADMIN("*") 토큰 → 200. 5개 모듈 테이블(EST_EQUIPMENT_ALARM / CMMS_WORK_ORDER /
        // POM_PRODUCTION_PLAN / RMS_RECIPE / SHP_DELIVERY_ORDER) 모두 존재하고 집계 SQL이
        // SQLite에서 성공해야 한다. 시드가 없으므로 전 필드 0이어야 한다.
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/dashboard");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"5개 모듈 집계(COUNT/SELECT)가 SQLite에서 모두 성공해야 한다. 응답 본문: {body}");

        var summary = await resp.Content.ReadFromJsonAsync<DashboardSummaryDto>();
        summary.Should().NotBeNull("dashboard 응답은 DashboardSummaryResponse JSON 객체여야 한다");

        // 빈 DB 집계 — 어느 필드든 음수면 집계 경로 손상(예: 실패를 0이 아닌 값으로 매핑) 신호.
        summary!.ActiveAlarms.Should().Be(0, "시드 없는 EST_EQUIPMENT_ALARM 활성 알람 수는 0");
        summary.IssuedWorkOrders.Should().Be(0, "시드 없는 Issued 작업지시 수는 0");
        summary.InProgressWorkOrders.Should().Be(0, "시드 없는 InProgress 작업지시 수는 0");
        summary.ReleasedPlans.Should().Be(0, "시드 없는 Released 생산계획 수는 0");
        summary.PendingRecipeApprovals.Should().Be(0, "시드 없는 대기 레시피 승인 수(WaitApproval+Approved1)는 0");
        summary.OpenDeliveryOrders.Should().Be(0, "시드 없는 미결 출하지시 수(Draft+Confirmed)는 0");
    }

    // DashboardSummaryResponse(camelCase 직렬화) 역직렬화용 미러.
    private sealed record DashboardSummaryDto(
        int ActiveAlarms,
        int IssuedWorkOrders,
        int InProgressWorkOrders,
        int ReleasedPlans,
        int PendingRecipeApprovals,
        int OpenDeliveryOrders);
}
