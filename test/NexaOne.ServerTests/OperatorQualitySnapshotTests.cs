using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaOne.Server.Components;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class OperatorQualitySnapshotTests : BunitContext
{
    [Fact]
    public void Renders_latest_metrics_and_lot_quality_gate()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.ExecuteQueryAsync("EST.OeeSummaryList", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["PLANT_ID"] = "P1", ["EQUIPMENT_ID"] = "EQ-01",
                    ["OEE_PERCENT"] = 82.4m, ["AVAILABILITY_PERCENT"] = 91.2m,
                    ["QUALITY_PERCENT"] = 98.7m, ["GOOD_COUNT"] = 987m, ["TOTAL_COUNT"] = 1000m,
                },
            });
        api.Setup(x => x.ExecuteQueryAsync("EST.TaktSummaryList", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["EQUIPMENT_ID"] = "EQ-01", ["ACTUAL_CYCLE_SECONDS_PER_UNIT"] = 58.5m,
                    ["TARGET_TAKT_SECONDS_PER_UNIT"] = 60m, ["DEVIATION_PERCENT"] = -2.5m,
                },
            });
        api.Setup(x => x.GetSpcViolationsAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SpcRuleViolationDto>
            {
                new("V1", "P1", "R1", "O1", "WE1", DateTime.UtcNow, "point outside UCL"),
            });
        api.Setup(x => x.GetLotInspectionStatusAsync("LOT-100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LotInspectionStatusDto("LOT-100", true, false, 4, 1, DateTime.UtcNow));
        Services.AddSingleton(api.Object);

        var cut = Render<OperatorQualitySnapshot>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("82.4%");
            cut.Markup.Should().Contain("91.2%");
            cut.Markup.Should().Contain("98.7%");
            cut.Markup.Should().Contain("58.5 / 60 s/unit");
            cut.Markup.Should().Contain("SPC 이상 이력");
        });

        cut.Find("#operator-lot-id").Input("LOT-100");
        cut.Find("#operator-lot-id").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("등록 결과에 실패 있음");
            cut.Markup.Should().Contain("결과 4건 · 실패 1건");
        });
        api.Verify(x => x.GetLotInspectionStatusAsync("LOT-100", It.IsAny<CancellationToken>()), Times.Once,
            "바코드 스캐너의 Enter 완료 신호만으로 검사 조회가 시작돼야 한다");
    }

    [Fact]
    public void Keeps_channel_home_usable_when_one_metric_source_fails()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.ExecuteQueryAsync("EST.OeeSummaryList", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("OEE unavailable"));
        api.Setup(x => x.ExecuteQueryAsync("EST.TaktSummaryList", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["EQUIPMENT_ID"] = "EQ-02", ["ACTUAL_CYCLE_SECONDS_PER_UNIT"] = 42m,
                    ["TARGET_TAKT_SECONDS_PER_UNIT"] = 40m, ["DEVIATION_PERCENT"] = 5m,
                },
            });
        api.Setup(x => x.GetSpcViolationsAsync(null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("QMS unavailable"));
        Services.AddSingleton(api.Object);

        var cut = Render<OperatorQualitySnapshot>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("42 / 40 s/unit");
            cut.Markup.Should().Contain("일부 지표를 불러오지 못했습니다");
            cut.Markup.Should().Contain("Lot 검사 결과");
        });
    }
}
