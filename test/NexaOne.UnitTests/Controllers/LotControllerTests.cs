using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Controllers;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;

namespace NexaOne.UnitTests.Controllers;

/// <summary>
/// §19.4 — LotController: 인증 사용자(NameIdentifier)가 작업자로 흘러가는지,
/// Request record -> Command 매핑, 성공/실패 시 HTTP 응답 코드를 검증한다.
/// </summary>
public sealed class LotControllerTests
{
    private readonly Mock<ILotRepository> _lots = new();
    private readonly Mock<ILotHistoryRepository> _histories = new();
    private readonly Mock<ILotMixingRelationRepository> _mixings = new();
    private readonly Mock<IProductionOrderRepository> _orders = new();
    private readonly Mock<ITrackingMasterGateway> _master = new();

    public LotControllerTests()
    {
        _master.Setup(m => m.GetEquipmentAsync("EQ001", default))
            .ReturnsAsync(new TrackingEquipmentInfo("EQ001", "P1", "CLS01", true));
        _master.Setup(m => m.IsUsableRecipeAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), default))
            .ReturnsAsync(true);
        _master.Setup(m => m.IsValidDefectCodeAsync(It.IsAny<string>(), default))
            .ReturnsAsync(true);
        _lots.Setup(r => r.AddAsync(It.IsAny<Lot>(), default)).Returns(Task.CompletedTask);
        _lots.Setup(r => r.UpdateAsync(It.IsAny<Lot>(), default)).Returns(Task.CompletedTask);
        _histories.Setup(r => r.AddAsync(It.IsAny<LotHistory>(), default)).Returns(Task.CompletedTask);
        _mixings.Setup(r => r.AddAsync(It.IsAny<LotMixingRelation>(), default)).Returns(Task.CompletedTask);
    }

    private LotController BuildController() =>
        new(new LotTrackingService(
            _lots.Object, _histories.Object, _mixings.Object, _orders.Object, _master.Object))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "worker1") }, "test"))
                }
            }
        };

    private static Lot QueuedLot(string id = "LOT001", decimal qty = 10m, params string[] steps) =>
        Lot.Create(id, "P1", null, "PROD01", qty,
            steps.Length == 0 ? ["CUT", "ASSY"] : steps, "tester").Value;

    // ── GetLots / GetLot / GetRoute ───────────────────────────────────────────

    [Fact]
    public async Task GetLots_returns_200_with_list()
    {
        _lots.Setup(r => r.GetByPlantAsync("P1", "Queued", default))
            .ReturnsAsync(new List<Lot> { QueuedLot() });

        var result = await BuildController().GetLots("P1", "Queued", default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IEnumerable<Lot>>().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task GetLots_missing_plant_returns_400()
    {
        var result = await BuildController().GetLots("", null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetLot_unknown_returns_404()
    {
        _lots.Setup(r => r.GetByIdAsync("LXXX", default)).ReturnsAsync((Lot?)null);

        var result = await BuildController().GetLot("LXXX", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetRoute_returns_200_with_route_view()
    {
        var lot = QueuedLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _histories.Setup(r => r.GetByLotAsync("P1", "LOT001", default))
            .ReturnsAsync(new List<LotHistory>());
        _mixings.Setup(r => r.GetByOutputLotAsync("P1", "LOT001", default))
            .ReturnsAsync(new List<LotMixingRelation>());

        var result = await BuildController().GetRoute("LOT001", default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<LotRouteView>().Which.Lot.Should().BeSameAs(lot);
    }

    // ── CreateLot ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateLot_returns_200_and_stamps_current_user()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync((Lot?)null);

        var result = await BuildController().CreateLot(
            new CreateLotRequest("P1", "LOT001", null, "PROD01", 10m, ["CUT"]), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var lot = ok.Value.Should().BeOfType<Lot>().Subject;
        lot.CreatedBy.Should().Be("worker1", "생성자는 인증 사용자(NameIdentifier)여야 한다");
    }

    [Fact]
    public async Task CreateLot_duplicate_returns_409()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());

        var result = await BuildController().CreateLot(
            new CreateLotRequest("P1", "LOT001", null, "PROD01", 10m, ["CUT"]), default);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    // ── TrackIn / TrackOut ────────────────────────────────────────────────────

    [Fact]
    public async Task TrackIn_returns_200_and_records_current_user_as_worker()
    {
        var lot = QueuedLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await BuildController().TrackIn(
            "LOT001", new TrackInRequest("P1", "EQ001", null, null), default);

        result.Should().BeOfType<OkObjectResult>();
        lot.TrackInUser.Should().Be("worker1", "TrackIn 작업자는 인증 사용자여야 한다");
        lot.State.Should().Be(LotState.Processing);
    }

    [Fact]
    public async Task TrackIn_hold_lot_returns_409()
    {
        var lot = QueuedLot();
        lot.Hold("admin");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await BuildController().TrackIn(
            "LOT001", new TrackInRequest("P1", "EQ001", null, null), default);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task TrackOut_maps_defect_requests_to_entries()
    {
        var lot = QueuedLot(steps: "CUT");
        lot.TrackIn("EQ001", null, null, "worker1", DateTime.UtcNow);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _lots.Setup(r => r.GetByWorkOrderAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<Lot>());

        var result = await BuildController().TrackOut("LOT001", new TrackOutRequest(
            "P1", "EQ001", 10m,
            [new DefectEntryRequest("D1", 1m), new DefectEntryRequest("D2", 2m)], null), default);

        result.Should().BeOfType<OkObjectResult>();
        lot.DefectQty.Should().Be(3m, "요청의 불량 목록이 DefectEntry로 매핑돼 합산돼야 한다");
        _master.Verify(m => m.IsValidDefectCodeAsync("D1", default), Times.Once);
        _master.Verify(m => m.IsValidDefectCodeAsync("D2", default), Times.Once);
    }

    [Fact]
    public async Task TrackOut_invalid_defect_code_returns_400()
    {
        var lot = QueuedLot(steps: "CUT");
        lot.TrackIn("EQ001", null, null, "worker1", DateTime.UtcNow);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _master.Setup(m => m.IsValidDefectCodeAsync("BAD", default)).ReturnsAsync(false);

        var result = await BuildController().TrackOut("LOT001", new TrackOutRequest(
            "P1", "EQ001", 10m, [new DefectEntryRequest("BAD", 1m)], null), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Mixing ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MixingTrackInOut_returns_200_with_output_lot()
    {
        var input = QueuedLot("LOT001", qty: 5m);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(input);
        _lots.Setup(r => r.GetByIdAsync("MIX001", default)).ReturnsAsync((Lot?)null);

        var result = await BuildController().MixingTrackInOut(new MixingTrackRequest(
            "P1", "MIX001", "PROD-MIX", "EQ001", ["MIX"],
            [new MixingInputRequest("LOT001", 5m)]), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var output = ok.Value.Should().BeOfType<Lot>().Subject;
        output.Id.Should().Be("MIX001");
        input.State.Should().Be(LotState.Consumed);
    }

    [Fact]
    public async Task MixingTrackInOut_null_inputs_returns_400()
    {
        var result = await BuildController().MixingTrackInOut(new MixingTrackRequest(
            "P1", "MIX001", "PROD-MIX", "EQ001", ["MIX"], null), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Hold / ReleaseHold ────────────────────────────────────────────────────

    [Fact]
    public async Task Hold_returns_204()
    {
        var lot = QueuedLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await BuildController().Hold("LOT001", default);

        result.Should().BeOfType<NoContentResult>();
        lot.IsHold.Should().BeTrue();
        lot.UpdatedBy.Should().Be("worker1");
    }

    [Fact]
    public async Task Hold_unknown_lot_returns_404()
    {
        _lots.Setup(r => r.GetByIdAsync("LXXX", default)).ReturnsAsync((Lot?)null);

        var result = await BuildController().Hold("LXXX", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ReleaseHold_returns_204()
    {
        var lot = QueuedLot();
        lot.Hold("admin");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await BuildController().ReleaseHold("LOT001", default);

        result.Should().BeOfType<NoContentResult>();
        lot.IsHold.Should().BeFalse();
    }

    // ── 보고서 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTrackingReport_returns_200_with_histories()
    {
        _histories.Setup(r => r.SearchAsync(
                "P1", null, null, null, null, null, LotTrackingService.DefaultReportRows, default))
            .ReturnsAsync(new List<LotHistory>
            {
                LotHistory.Of(QueuedLot(), LotExecutionId.TrackIn, "worker1", 10m, 0)
            });

        var result = await BuildController().GetTrackingReport(
            "P1", null, null, null, null, null, ct: default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IEnumerable<LotHistory>>().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task GetTrackingReport_missing_plant_returns_400()
    {
        var result = await BuildController().GetTrackingReport(
            "", null, null, null, null, null, ct: default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
