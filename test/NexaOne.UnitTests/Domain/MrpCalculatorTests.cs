using NexaOne.POM.Domain.Mrp;

namespace NexaOne.UnitTests.Domain;

/// <summary>MRP v1 순소요 전개(순수 계산기) — 스펙 2026-07-09-mrp-v1-design의 규칙 ①~⑥ 검증.
/// 넷팅식 net = max(0, gross + safety − onHand − onOrder), 로트 suggested = ceil(max(net,MOQ)/lot)×lot,
/// LLC 레벨 내림차순(부모 먼저) 처리로 공유 구성품 수요를 전량 누적 후 한 번만 넷팅한다.</summary>
public sealed class MrpCalculatorTests
{
    private static readonly IReadOnlyDictionary<string, decimal> NoQty = new Dictionary<string, decimal>();
    private static readonly IReadOnlyDictionary<string, MrpItemParameters> NoPlan = new Dictionary<string, MrpItemParameters>();
    private static readonly IReadOnlyDictionary<string, MrpVendorParameters> NoVendor = new Dictionary<string, MrpVendorParameters>();

    private static MrpCalculationResult Calc(
        IReadOnlyList<MrpDemand> demands,
        IReadOnlyList<MrpBomLine>? bom = null,
        IReadOnlyDictionary<string, decimal>? onHand = null,
        IReadOnlyDictionary<string, decimal>? onOrder = null,
        IReadOnlyDictionary<string, MrpItemParameters>? planning = null,
        IReadOnlyDictionary<string, MrpVendorParameters>? vendors = null)
        => MrpCalculator.Calculate(
            demands, bom ?? Array.Empty<MrpBomLine>(),
            onHand ?? NoQty, onOrder ?? NoQty, planning ?? NoPlan, vendors ?? NoVendor);

    [Fact]
    public void Single_item_no_bom_nets_demand_against_on_hand_and_on_order()
    {
        var result = Calc(
            new[] { new MrpDemand("A", 100, new DateTime(2026, 8, 1), "SO-1") },
            onHand: new Dictionary<string, decimal> { ["A"] = 30 },
            onOrder: new Dictionary<string, decimal> { ["A"] = 20 });

        result.Success.Should().BeTrue();
        var p = result.Proposals.Should().ContainSingle().Subject;
        p.GrossQty.Should().Be(100);
        p.NetQty.Should().Be(50, "net = 100 + 0 − 30 − 20");
        p.SuggestedQty.Should().Be(50, "로트 미지정은 1 배수 그대로");
        p.OrderType.Should().Be("Purchase", "BOM 부모도 아니고 파라미터도 없으면 기본 구매");
    }

    [Fact]
    public void Sufficient_supply_produces_no_proposal_and_no_component_explosion()
    {
        var result = Calc(
            new[] { new MrpDemand("A", 100, null, "SO-1") },
            bom: new[] { new MrpBomLine("A", "B", 2, 0) },
            onHand: new Dictionary<string, decimal> { ["A"] = 150 });

        result.Success.Should().BeTrue();
        result.Proposals.Should().BeEmpty("재고 충분 — 부모 미제안이면 구성품 종속 수요도 발생하지 않아야 한다");
    }

    [Fact]
    public void Safety_stock_raises_net_requirement()
    {
        var planning = new Dictionary<string, MrpItemParameters>
        {
            ["A"] = new(SafetyStock: 50, LeadTimeDays: null, LotSize: 1, MakeOrBuy: null),
        };

        var result = Calc(
            new[] { new MrpDemand("A", 100, null, "SO-1") },
            onHand: new Dictionary<string, decimal> { ["A"] = 120 },
            planning: planning);

        var p = result.Proposals.Should().ContainSingle().Subject;
        p.NetQty.Should().Be(30, "net = 100 + 50(안전) − 120");
        p.SafetyStockQty.Should().Be(50);
    }

    [Fact]
    public void Lot_size_rounds_up_and_moq_sets_floor()
    {
        var planning = new Dictionary<string, MrpItemParameters>
        {
            ["A"] = new(0, null, LotSize: 500, MakeOrBuy: "Buy"),
            ["B"] = new(0, null, LotSize: 1, MakeOrBuy: "Buy"),
        };
        var vendors = new Dictionary<string, MrpVendorParameters>
        {
            ["B"] = new(LeadTimeDays: null, Moq: 200),
        };

        var result = Calc(
            new[] { new MrpDemand("A", 1_234, null, "SO-1"), new MrpDemand("B", 10, null, "SO-2") },
            planning: planning, vendors: vendors);

        result.Proposals.Should().HaveCount(2);
        result.Proposals.Single(p => p.ItemId == "A").SuggestedQty.Should().Be(1_500, "ceil(1234/500)×500");
        result.Proposals.Single(p => p.ItemId == "B").SuggestedQty.Should().Be(200, "max(net=10, MOQ=200)");
    }

    [Fact]
    public void Multi_level_bom_explodes_from_suggested_qty_with_scrap()
    {
        // A(로트 100) → B ×2(스크랩 5%) → C ×10. 수요 A=1000.
        var planning = new Dictionary<string, MrpItemParameters>
        {
            ["A"] = new(0, 2, LotSize: 100, MakeOrBuy: "Make"),
            ["B"] = new(0, 5, LotSize: 1, MakeOrBuy: "Make"),
            ["C"] = new(0, 7, LotSize: 500, MakeOrBuy: "Buy"),
        };
        var bom = new[]
        {
            new MrpBomLine("A", "B", 2, 0.05m),
            new MrpBomLine("B", "C", 10, 0),
        };

        var result = Calc(
            new[] { new MrpDemand("A", 1_000, new DateTime(2026, 8, 10), "SO-1") },
            bom: bom, planning: planning);

        result.Success.Should().BeTrue();
        result.Proposals.Should().HaveCount(3);

        var a = result.Proposals.Single(p => p.ItemId == "A");
        a.OrderType.Should().Be("Production");
        a.SuggestedQty.Should().Be(1_000);
        a.ReleaseDate.Should().Be(new DateTime(2026, 8, 8), "납기 −리드 2일");

        var b = result.Proposals.Single(p => p.ItemId == "B");
        b.GrossQty.Should().Be(2_100, "1000(제안) × 2 × 1.05(스크랩) — 종속 수요는 제안수량 기준");
        b.DueDate.Should().Be(a.ReleaseDate, "구성품 납기 = 부모 착수일");
        b.ReleaseDate.Should().Be(new DateTime(2026, 8, 3));

        var c = result.Proposals.Single(p => p.ItemId == "C");
        c.GrossQty.Should().Be(21_000, "2100 × 10");
        c.SuggestedQty.Should().Be(21_000, "로트 500 배수에 이미 정합");
        c.OrderType.Should().Be("Purchase");
    }

    [Fact]
    public void Shared_component_accumulates_all_parent_demand_before_single_netting()
    {
        // A와 B가 공유 구성품 C를 사용 — C는 두 부모의 종속 수요를 모두 누적한 뒤 한 번만 넷팅(LLC).
        var bom = new[]
        {
            new MrpBomLine("A", "C", 1, 0),
            new MrpBomLine("B", "C", 3, 0),
        };
        var planning = new Dictionary<string, MrpItemParameters>
        {
            ["A"] = new(0, null, 1, "Make"),
            ["B"] = new(0, null, 1, "Make"),
        };

        var result = Calc(
            new[] { new MrpDemand("A", 100, null, "SO-1"), new MrpDemand("B", 50, null, "SO-2") },
            bom: bom,
            onHand: new Dictionary<string, decimal> { ["C"] = 200 },
            planning: planning);

        var c = result.Proposals.Single(p => p.ItemId == "C");
        c.GrossQty.Should().Be(250, "100×1 + 50×3 누적 후");
        c.NetQty.Should().Be(50, "250 − 200 — 부모별 개별 넷팅이었다면 왜곡된다");
        result.Proposals.Should().ContainSingle(p => p.ItemId == "C", "제안은 품목당 1건");
    }

    [Fact]
    public void Make_or_buy_falls_back_to_bom_parent_then_default_buy()
    {
        var bom = new[] { new MrpBomLine("A", "B", 1, 0) };

        var result = Calc(
            new[] { new MrpDemand("A", 10, null, "SO-1") },
            bom: bom);

        result.Proposals.Single(p => p.ItemId == "A").OrderType
            .Should().Be("Production", "파라미터 없음 + BOM 부모 → 생산");
        result.Proposals.Single(p => p.ItemId == "B").OrderType
            .Should().Be("Purchase", "파라미터·BOM 자식 없음 → 기본 구매");
    }

    [Fact]
    public void Explicit_buy_parameter_overrides_bom_parent_and_stops_explosion()
    {
        var bom = new[] { new MrpBomLine("A", "B", 5, 0) };
        var planning = new Dictionary<string, MrpItemParameters>
        {
            ["A"] = new(0, null, 1, "Buy"),   // 외주 완제품 — BOM이 있어도 구매
        };

        var result = Calc(new[] { new MrpDemand("A", 10, null, "SO-1") }, bom: bom, planning: planning);

        var a = result.Proposals.Should().ContainSingle().Subject;
        a.OrderType.Should().Be("Purchase");
        result.Proposals.Should().NotContain(p => p.ItemId == "B", "구매 품목은 BOM 전개하지 않는다");
    }

    [Fact]
    public void Lead_time_prefers_planning_then_vendor_and_past_release_is_not_clamped()
    {
        var vendors = new Dictionary<string, MrpVendorParameters>
        {
            ["A"] = new(LeadTimeDays: 30, Moq: null),
        };
        var due = DateTime.Today.AddDays(3);

        var result = Calc(
            new[] { new MrpDemand("A", 10, due, "SO-1") },
            vendors: vendors);

        var p = result.Proposals.Should().ContainSingle().Subject;
        p.ReleaseDate.Should().Be(due.AddDays(-30), "벤더 리드타임 폴백 — 과거여도 클램프 없이 지연을 노출한다");
        p.ReleaseDate.Should().BeBefore(DateTime.Today);
    }

    [Fact]
    public void Earliest_due_date_wins_when_multiple_demands_contribute()
    {
        var result = Calc(new[]
        {
            new MrpDemand("A", 10, new DateTime(2026, 9, 1), "SO-1"),
            new MrpDemand("A", 20, new DateTime(2026, 8, 1), "SO-2"),
        });

        var p = result.Proposals.Should().ContainSingle().Subject;
        p.GrossQty.Should().Be(30);
        p.DueDate.Should().Be(new DateTime(2026, 8, 1));
        p.SourceDemand.Should().Contain("외 1건");
    }

    [Fact]
    public void Bom_cycle_fails_with_path_in_error_message()
    {
        var bom = new[]
        {
            new MrpBomLine("A", "B", 1, 0),
            new MrpBomLine("B", "C", 1, 0),
            new MrpBomLine("C", "A", 1, 0),
        };

        var result = Calc(new[] { new MrpDemand("A", 10, null, "SO-1") }, bom: bom);

        result.Success.Should().BeFalse();
        result.Proposals.Should().BeEmpty();
        result.Error.Should().Contain("순환").And.Contain("A").And.Contain("B").And.Contain("C");
    }

    [Fact]
    public void Zero_and_negative_demands_are_ignored()
    {
        var result = Calc(new[]
        {
            new MrpDemand("A", 0, null, "SO-1"),
            new MrpDemand("B", -5, null, "SO-2"),
        });

        result.Success.Should().BeTrue();
        result.Proposals.Should().BeEmpty();
    }
}
