using NexaOne.POM.Domain.Mrp;

namespace NexaOne.UnitTests.Domain;

/// <summary>MRP v2 3단 — 기간 버킷(시간위상) 넷팅. 스펙 'v2 3단' 절의 전진 루프 규칙 검증:
/// projected(b)=projected(b−1)+receipts(b)−gross(b), 부족 시 net=safety−projected → 로트 → 이월.
/// 총량 모드(buckets=null)의 불변은 기존 MrpCalculatorTests 14종이 게이트한다.</summary>
public sealed class MrpBucketedCalculatorTests
{
    private static readonly DateTime Today = new(2026, 7, 10);
    private static readonly IReadOnlyDictionary<string, decimal> NoQty = new Dictionary<string, decimal>();
    private static readonly IReadOnlyDictionary<string, MrpItemParameters> NoPlan = new Dictionary<string, MrpItemParameters>();
    private static readonly IReadOnlyDictionary<string, MrpVendorParameters> NoVendor = new Dictionary<string, MrpVendorParameters>();

    private static MrpCalculationResult Calc(
        IReadOnlyList<MrpDemand> demands,
        IReadOnlyList<MrpBomLine>? bom = null,
        IReadOnlyDictionary<string, decimal>? onHand = null,
        IReadOnlyList<MrpScheduledReceipt>? receipts = null,
        IReadOnlyDictionary<string, MrpItemParameters>? planning = null,
        int bucketDays = 7, int horizon = 8)
        => MrpCalculator.Calculate(
            demands, bom ?? Array.Empty<MrpBomLine>(),
            onHand ?? NoQty, NoQty, planning ?? NoPlan, NoVendor,
            new MrpBucketOptions(Today, bucketDays, horizon), receipts);

    private static DateTime Bucket(int b) => Today.AddDays(b * 7);

    [Fact]
    public void Demands_in_different_buckets_produce_separate_proposals_with_bucket_due_dates()
    {
        var result = Calc(new[]
        {
            new MrpDemand("A", 100, Today.AddDays(1), "SO-1"),    // 버킷 0
            new MrpDemand("A", 200, Today.AddDays(15), "SO-2"),   // 버킷 2
        });

        result.Success.Should().BeTrue();
        result.Proposals.Should().HaveCount(2, "버킷별 별도 제안(총량 모드였다면 1건 300)");
        var b0 = result.Proposals.Single(p => p.DueDate == Bucket(0));
        var b2 = result.Proposals.Single(p => p.DueDate == Bucket(2));
        b0.SuggestedQty.Should().Be(100);
        b2.SuggestedQty.Should().Be(200);
        b0.SourceDemand.Should().Be("SO-1");
        b2.SourceDemand.Should().Be("SO-2");
    }

    [Fact]
    public void On_hand_carries_forward_across_buckets()
    {
        // 재고 150 — 버킷0 수요 100을 덮고 잔여 50이 버킷2로 이월돼 200 중 150만 부족.
        var result = Calc(
            new[]
            {
                new MrpDemand("A", 100, Today, "SO-1"),
                new MrpDemand("A", 200, Today.AddDays(15), "SO-2"),
            },
            onHand: new Dictionary<string, decimal> { ["A"] = 150 });

        var p = result.Proposals.Should().ContainSingle("버킷0은 재고로 충족").Subject;
        p.DueDate.Should().Be(Bucket(2));
        p.NetQty.Should().Be(150, "200 − 이월 재고 50");
        p.OnHandQty.Should().Be(50, "버킷 진입 시점 예상재고(이월분)");
    }

    [Fact]
    public void Scheduled_receipt_covers_only_its_bucket_onward()
    {
        // 예정입고 100이 버킷2 — 버킷0 수요는 못 덮고(시점 위반), 버킷2 수요는 덮는다.
        var result = Calc(
            new[]
            {
                new MrpDemand("A", 100, Today, "SO-1"),
                new MrpDemand("A", 100, Today.AddDays(15), "SO-2"),
            },
            receipts: new[] { new MrpScheduledReceipt("A", 100, Today.AddDays(15)) });

        var p = result.Proposals.Should().ContainSingle("버킷2는 예정입고로 충족 — 총량 모드였다면 시점 왜곡으로 100만 제안").Subject;
        p.DueDate.Should().Be(Bucket(0), "버킷0 부족은 미래 입고로 못 덮는다(시간위상의 핵심)");
        p.SuggestedQty.Should().Be(100);
    }

    [Fact]
    public void Lot_excess_carries_into_next_bucket()
    {
        // 로트 500 — 버킷0 수요 300에 500 제안(초과 200 이월) → 버킷1 수요 150은 이월분으로 충족.
        var planning = new Dictionary<string, MrpItemParameters> { ["A"] = new(0, null, LotSize: 500, "Buy") };
        var result = Calc(
            new[]
            {
                new MrpDemand("A", 300, Today, "SO-1"),
                new MrpDemand("A", 150, Today.AddDays(8), "SO-2"),
            },
            planning: planning);

        var p = result.Proposals.Should().ContainSingle("버킷1은 로트 초과 이월분으로 충족").Subject;
        p.DueDate.Should().Be(Bucket(0));
        p.SuggestedQty.Should().Be(500);
    }

    [Fact]
    public void Safety_stock_is_maintained_as_floor_in_every_bucket()
    {
        // 안전재고 50 — 재고 60에서 버킷0 수요 30이면 잔여 30 < 50 → 20 보충.
        var planning = new Dictionary<string, MrpItemParameters> { ["A"] = new(SafetyStock: 50, null, 1, "Buy") };
        var result = Calc(
            new[] { new MrpDemand("A", 30, Today, "SO-1") },
            onHand: new Dictionary<string, decimal> { ["A"] = 60 },
            planning: planning);

        var p = result.Proposals.Should().ContainSingle().Subject;
        p.NetQty.Should().Be(20, "safety 50 − projected 30");
        p.SuggestedQty.Should().Be(20);
    }

    [Fact]
    public void Dependent_demand_lands_in_component_bucket_offset_by_parent_lead()
    {
        // 부모 A: 버킷2 납기, 리드 7일 → release=버킷1 시작 → 구성품 B 수요는 버킷1.
        var planning = new Dictionary<string, MrpItemParameters>
        {
            ["A"] = new(0, LeadTimeDays: 7, 1, "Make"),
            ["B"] = new(0, null, 1, "Buy"),
        };
        var bom = new[] { new MrpBomLine("A", "B", 2, 0) };

        var result = Calc(
            new[] { new MrpDemand("A", 100, Today.AddDays(15), "SO-1") },
            bom: bom, planning: planning);

        var a = result.Proposals.Single(p => p.ItemId == "A");
        a.DueDate.Should().Be(Bucket(2));
        a.ReleaseDate.Should().Be(Bucket(1), "버킷2 시작 − 리드 7일 = 버킷1 시작");

        var b = result.Proposals.Single(p => p.ItemId == "B");
        b.DueDate.Should().Be(Bucket(1), "구성품 수요는 부모 착수 버킷에 귀속");
        b.GrossQty.Should().Be(200);
        b.Contributions!.Single().Should().Be(new MrpContribution("A 생산 전개", 200));
    }

    [Fact]
    public void Past_due_and_beyond_horizon_demands_are_clamped_not_dropped()
    {
        var result = Calc(new[]
        {
            new MrpDemand("A", 10, Today.AddDays(-30), "SO-LATE"),   // 과거 → 버킷 0
            new MrpDemand("A", 20, Today.AddDays(365), "SO-FAR"),    // 호라이즌 밖 → 마지막 버킷
        }, horizon: 4);

        result.Proposals.Should().HaveCount(2);
        result.Proposals.Single(p => p.SourceDemand == "SO-LATE").DueDate.Should().Be(Bucket(0));
        result.Proposals.Single(p => p.SourceDemand == "SO-FAR").DueDate.Should().Be(Bucket(3), "누락 금지 — 마지막 버킷 클램프");
    }
}
