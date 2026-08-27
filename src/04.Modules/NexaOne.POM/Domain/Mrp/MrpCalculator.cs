namespace NexaOne.POM.Domain.Mrp;

/// <summary>독립 수요 1건 — 확정 수주(SLS Confirmed/Producing)의 미납 수량. DueDate=납기(PLAN_END_DATE).
/// PlantId — 실오더 전환(v2) 대상 공장(수주 PLANT_ID). 종속 수요는 부모 공장을 상속한다.</summary>
public sealed record MrpDemand(string ItemId, decimal Qty, DateTime? DueDate, string SourceRef, string? PlantId = null);

/// <summary>BOM 1행 — 부모 1단위당 구성품 소요(QuantityPer) × (1+ScrapRate) 전개.</summary>
public sealed record MrpBomLine(string ParentItemId, string ComponentItemId, decimal QuantityPer, decimal ScrapRate);

/// <summary>품목 계획 파라미터 — 미등록 품목은 기본값(안전재고 0·로트 1·자동 판정).</summary>
public sealed record MrpItemParameters(decimal SafetyStock, int? LeadTimeDays, decimal LotSize, string? MakeOrBuy);

/// <summary>조달 파라미터 — 품목별 최소 리드타임과 최소 주문수량.</summary>
public sealed record MrpVendorParameters(int? LeadTimeDays, decimal? Moq);

/// <summary>계획오더 제안 1건 — 넷팅 근거(Gross/OnHand/OnOrder/Safety/Net)를 함께 담아 화면에서 산식 추적 가능.</summary>
public sealed record MrpPlannedOrderProposal(
    string ItemId,
    string OrderType,           // Purchase | Production
    decimal GrossQty,
    decimal OnHandQty,
    decimal OnOrderQty,
    decimal SafetyStockQty,
    decimal NetQty,
    decimal SuggestedQty,
    DateTime? DueDate,
    DateTime? ReleaseDate,
    string? SourceDemand,
    string? PlantId = null,     // 첫 기여 수요의 공장(전환 시 실오더 PLANT_ID — 미상은 전환기가 DEFAULT 폴백)
    IReadOnlyList<MrpContribution>? Contributions = null);   // 페깅(v2) — 총소요의 수요별 기여 분해

/// <summary>페깅(수요 기여) 1건 — 제안 품목의 총소요가 어느 수요에서 얼마나 왔는지(v2 정밀 추적).</summary>
public sealed record MrpContribution(string DemandRef, decimal Qty);

/// <summary>기간 버킷 옵션(v2 3단 — 시간위상 MRP). Today는 순수성 유지를 위해 호출자가 주입한다.</summary>
public sealed record MrpBucketOptions(DateTime Today, int BucketDays, int HorizonBuckets);

/// <summary>일자 예정입고 1건(버킷 모드 전용) — PO=INCOMING_DATE, WO=PLAN_END_DATE. 날짜 없음=버킷 0
/// (v1 총량 집계와 동일하게 '가용'으로 간주 — 보수적).</summary>
public sealed record MrpScheduledReceipt(string ItemId, decimal Qty, DateTime? Date);

public sealed record MrpCalculationResult(bool Success, IReadOnlyList<MrpPlannedOrderProposal> Proposals, string? Error);

/// <summary>MRP v1 순소요 전개(순수, 무DB) — 표준 gross-to-net 사이클.
/// <para>규칙(스펙 2026-07-09-mrp-v1-design):
/// ① Low-Level Code — BOM 그래프 최장경로 레벨(순환 검출 시 실패). 공유 구성품은 전 부모의 종속 수요를
///    '모두 누적한 뒤' 한 번만 넷팅한다(레벨 오름차순 처리).
/// ② net = max(0, gross + safetyStock − onHand − onOrder).
/// ③ 로트 사이징 — suggested = ceil(max(net, MOQ) / lotSize) × lotSize.
/// ④ Make/Buy — 파라미터 우선 → BOM 부모=Production → 벤더품목 보유=Purchase → 기본 Purchase.
/// ⑤ Production 제안은 구성품에 suggested × QuantityPer × (1+ScrapRate)를 종속 수요로 가산(납기=부모 착수일).
/// ⑥ 날짜 — DUE=기여 수요 최조기 납기, RELEASE=DUE−리드타임(파라미터→벤더 MIN→0). 과거 역산도 그대로
///    노출(지연 가시화 — 클램프하지 않는다).
/// v1 명시 한계: 무버킷 총량 넷팅(기간 버킷 없음), 수요가 없는 안전재고 단독 보충은 제안하지 않음(수요 주도).</para></summary>
public static class MrpCalculator
{
    public static MrpCalculationResult Calculate(
        IReadOnlyList<MrpDemand> demands,
        IReadOnlyList<MrpBomLine> bomLines,
        IReadOnlyDictionary<string, decimal> onHand,
        IReadOnlyDictionary<string, decimal> onOrder,
        IReadOnlyDictionary<string, MrpItemParameters> planning,
        IReadOnlyDictionary<string, MrpVendorParameters> vendors,
        MrpBucketOptions? buckets = null,
        IReadOnlyList<MrpScheduledReceipt>? receipts = null)
    {
        // 버킷 모드(v2 3단) — 시간위상 넷팅 별도 경로. 총량 모드(buckets=null)는 v1 코드 그대로
        // (기존 테스트 불변 통과가 호환성 게이트 — 두 경로는 LLC/로트/판정 규칙을 공유한다).
        if (buckets is not null)
            return CalculateTimePhased(demands, bomLines, onHand, planning, vendors,
                receipts ?? Array.Empty<MrpScheduledReceipt>(), buckets);

        // ── ① Low-Level Code(최장경로) + 순환 검출 ─────────────────────────────
        var children = bomLines.GroupBy(b => b.ParentItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        int Level(string item, List<string> path)
        {
            if (levels.TryGetValue(item, out var known)) return known;
            if (!visiting.Add(item))
                throw new MrpCycleException(string.Join(" → ", path.SkipWhile(p => p != item).Append(item)));
            path.Add(item);
            var level = 0;
            if (children.TryGetValue(item, out var lines))
                foreach (var line in lines)
                    level = Math.Max(level, 1 + Level(line.ComponentItemId, path));
            path.RemoveAt(path.Count - 1);
            visiting.Remove(item);
            // 자신의 레벨 = 하위 최심 깊이(부모가 더 얕음) — 처리 순서는 '부모 먼저'라 내림차순으로 돈다.
            levels[item] = level;
            return level;
        }

        try
        {
            foreach (var item in children.Keys) Level(item, new List<string>());
            foreach (var d in demands) Level(d.ItemId, new List<string>());
        }
        catch (MrpCycleException ex)
        {
            return new MrpCalculationResult(false, Array.Empty<MrpPlannedOrderProposal>(), $"BOM 순환 감지: {ex.Message}");
        }

        // ── 독립 수요 누적 ──────────────────────────────────────────────────────
        var gross = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var due = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var plants = new Dictionary<string, string?>(StringComparer.Ordinal);   // 첫 기여 수요의 공장(전환용)
        var contribs = new Dictionary<string, List<MrpContribution>>(StringComparer.Ordinal);   // 페깅(v2)
        foreach (var d in demands.Where(d => d.Qty > 0))
        {
            gross[d.ItemId] = gross.GetValueOrDefault(d.ItemId) + d.Qty;
            MergeDue(due, d.ItemId, d.DueDate);
            (sources.TryGetValue(d.ItemId, out var list) ? list : sources[d.ItemId] = new()).Add(d.SourceRef);
            (contribs.TryGetValue(d.ItemId, out var cb) ? cb : contribs[d.ItemId] = new()).Add(new(d.SourceRef, d.Qty));
            if (!plants.ContainsKey(d.ItemId) && d.PlantId is not null) plants[d.ItemId] = d.PlantId;
        }

        // ── ②~⑥ 부모 먼저 처리 — Level 정의(부모 = 1 + max(자식))상 완제품이 최대 레벨이므로 '레벨 내림차순'.
        // 공유 구성품은 레벨이 모든 부모보다 낮아, 자기 차례 전에 전 부모의 종속 수요가 누적 완료된다(LLC 성질).
        var proposals = new List<MrpPlannedOrderProposal>();
        foreach (var item in AllItems(gross, children).OrderByDescending(i => levels.GetValueOrDefault(i)).ToList())
        {
            var g = gross.GetValueOrDefault(item);
            if (g <= 0) continue;

            var p = planning.GetValueOrDefault(item) ?? new MrpItemParameters(0, null, 1, null);
            var v = vendors.GetValueOrDefault(item);
            var oh = onHand.GetValueOrDefault(item);
            var oo = onOrder.GetValueOrDefault(item);

            var net = g + p.SafetyStock - oh - oo;
            if (net <= 0) continue;   // 충분 — 제안 없음(구성품 전개도 없음: 종속 수요는 '계획오더'에서만 발생)

            var moq = v?.Moq ?? 0m;
            var lot = p.LotSize > 0 ? p.LotSize : 1m;
            var suggested = Math.Ceiling(Math.Max(net, moq) / lot) * lot;

            var isMake = string.Equals(p.MakeOrBuy, "Make", StringComparison.OrdinalIgnoreCase)
                || (p.MakeOrBuy is null && children.ContainsKey(item));
            var orderType = isMake ? "Production" : "Purchase";

            var lead = p.LeadTimeDays ?? v?.LeadTimeDays ?? 0;
            var dueDate = due.GetValueOrDefault(item);
            var release = dueDate?.AddDays(-lead);
            var plant = plants.GetValueOrDefault(item);

            proposals.Add(new MrpPlannedOrderProposal(
                item, orderType, g, oh, oo, p.SafetyStock, net, suggested, dueDate, release, Summarize(sources, item), plant,
                contribs.TryGetValue(item, out var myContribs) ? myContribs.ToList() : null));

            // ⑤ 종속 수요 전개 — 생산 제안 수량 기준(스크랩률 가산), 구성품 납기 = 부모 착수일. 공장은 부모 상속.
            if (isMake && children.TryGetValue(item, out var lines))
                foreach (var line in lines)
                {
                    var compQty = suggested * line.QuantityPer * (1 + line.ScrapRate);
                    if (compQty <= 0) continue;
                    gross[line.ComponentItemId] = gross.GetValueOrDefault(line.ComponentItemId) + compQty;
                    MergeDue(due, line.ComponentItemId, release);
                    (sources.TryGetValue(line.ComponentItemId, out var cl) ? cl : sources[line.ComponentItemId] = new())
                        .Add($"{item} 생산 전개");
                    (contribs.TryGetValue(line.ComponentItemId, out var ccb) ? ccb : contribs[line.ComponentItemId] = new())
                        .Add(new($"{item} 생산 전개", compQty));
                    if (!plants.ContainsKey(line.ComponentItemId) && plant is not null) plants[line.ComponentItemId] = plant;
                }
        }

        return new MrpCalculationResult(true, proposals, null);
    }

    // ── v2 3단 — 시간위상(기간 버킷) 넷팅. 스펙: 볼트 2026-07-09-mrp-v1-design.md 'v2 3단' 절. ──
    // 품목별 예상재고 전진 루프: projected(b)=projected(b-1)+receipts(b)-gross(b);
    // projected<safety면 net=safety-projected → 로트 → 해당 버킷 계획오더 + projected 가산(이월).
    private static MrpCalculationResult CalculateTimePhased(
        IReadOnlyList<MrpDemand> demands,
        IReadOnlyList<MrpBomLine> bomLines,
        IReadOnlyDictionary<string, decimal> onHand,
        IReadOnlyDictionary<string, MrpItemParameters> planning,
        IReadOnlyDictionary<string, MrpVendorParameters> vendors,
        IReadOnlyList<MrpScheduledReceipt> receipts,
        MrpBucketOptions opt)
    {
        var children = bomLines.GroupBy(b => b.ParentItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        Dictionary<string, int> levels;
        try
        {
            levels = ComputeLevels(children, demands);
        }
        catch (MrpCycleException ex)
        {
            return new MrpCalculationResult(false, Array.Empty<MrpPlannedOrderProposal>(), $"BOM 순환 감지: {ex.Message}");
        }

        var h = Math.Max(1, opt.HorizonBuckets);
        // 버킷 귀속 — 과거=0(지연 수요 즉시 필요), 호라이즌 밖=마지막 클램프(누락 금지), 무납기=0(보수적).
        int BucketOf(DateTime? date) => date is null ? 0
            : Math.Clamp((date.Value.Date - opt.Today.Date).Days / opt.BucketDays, 0, h - 1);
        DateTime BucketStart(int b) => opt.Today.Date.AddDays((double)b * opt.BucketDays);

        // (품목, 버킷) 단위 누적 — 총량 모드의 품목 단위 사전들과 동형.
        var gross = new Dictionary<(string Item, int B), decimal>();
        var sources = new Dictionary<(string, int), List<string>>();
        var contribs = new Dictionary<(string, int), List<MrpContribution>>();
        var recv = new Dictionary<(string, int), decimal>();
        var plants = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var d in demands.Where(d => d.Qty > 0))
        {
            var key = (d.ItemId, BucketOf(d.DueDate));
            gross[key] = gross.GetValueOrDefault(key) + d.Qty;
            (sources.TryGetValue(key, out var sl) ? sl : sources[key] = new()).Add(d.SourceRef);
            (contribs.TryGetValue(key, out var cl) ? cl : contribs[key] = new()).Add(new(d.SourceRef, d.Qty));
            if (!plants.ContainsKey(d.ItemId) && d.PlantId is not null) plants[d.ItemId] = d.PlantId;
        }
        foreach (var r in receipts.Where(r => r.Qty > 0))
        {
            var key = (r.ItemId, BucketOf(r.Date));
            recv[key] = recv.GetValueOrDefault(key) + r.Qty;
        }

        var items = demands.Select(d => d.ItemId)
            .Concat(children.Keys)
            .Concat(children.Values.SelectMany(l => l.Select(x => x.ComponentItemId)))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(i => levels.GetValueOrDefault(i))
            .ToList();

        var proposals = new List<MrpPlannedOrderProposal>();
        foreach (var item in items)
        {
            var p = planning.GetValueOrDefault(item) ?? new MrpItemParameters(0, null, 1, null);
            var v = vendors.GetValueOrDefault(item);
            var isMake = string.Equals(p.MakeOrBuy, "Make", StringComparison.OrdinalIgnoreCase)
                || (p.MakeOrBuy is null && children.ContainsKey(item));
            var lead = p.LeadTimeDays ?? v?.LeadTimeDays ?? 0;
            var moq = v?.Moq ?? 0m;
            var lot = p.LotSize > 0 ? p.LotSize : 1m;
            var plant = plants.GetValueOrDefault(item);

            var projected = onHand.GetValueOrDefault(item);
            for (var b = 0; b < h; b++)
            {
                var g = gross.GetValueOrDefault((item, b));
                var rec = recv.GetValueOrDefault((item, b));
                if (g == 0 && rec == 0 && projected >= p.SafetyStock) continue;   // 무이벤트 버킷 스킵
                var entering = projected;
                projected += rec - g;
                if (projected >= p.SafetyStock) continue;

                var net = p.SafetyStock - projected;
                var suggested = Math.Ceiling(Math.Max(net, moq) / lot) * lot;
                projected += suggested;   // 로트 초과분은 다음 버킷으로 이월(표준)

                var due = BucketStart(b);
                var release = due.AddDays(-lead);
                proposals.Add(new MrpPlannedOrderProposal(
                    item, isMake ? "Production" : "Purchase",
                    g, entering, rec, p.SafetyStock, net, suggested, due, release,
                    SummarizeList(sources.GetValueOrDefault((item, b))), plant,
                    contribs.TryGetValue((item, b), out var myC) ? myC.ToList() : null));

                if (isMake && children.TryGetValue(item, out var lines))
                    foreach (var line in lines)
                    {
                        var compQty = suggested * line.QuantityPer * (1 + line.ScrapRate);
                        if (compQty <= 0) continue;
                        var ck = (line.ComponentItemId, BucketOf(release));
                        gross[ck] = gross.GetValueOrDefault(ck) + compQty;
                        (sources.TryGetValue(ck, out var csl) ? csl : sources[ck] = new()).Add($"{item} 생산 전개");
                        (contribs.TryGetValue(ck, out var ccl) ? ccl : contribs[ck] = new())
                            .Add(new($"{item} 생산 전개", compQty));
                        if (!plants.ContainsKey(line.ComponentItemId) && plant is not null)
                            plants[line.ComponentItemId] = plant;
                    }
            }
        }

        return new MrpCalculationResult(true, proposals, null);
    }

    // LLC 산출(총량/버킷 공용) — 최장경로 + 순환 시 MrpCycleException.
    private static Dictionary<string, int> ComputeLevels(
        Dictionary<string, List<MrpBomLine>> children, IReadOnlyList<MrpDemand> demands)
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        int Level(string item, List<string> path)
        {
            if (levels.TryGetValue(item, out var known)) return known;
            if (!visiting.Add(item))
                throw new MrpCycleException(string.Join(" → ", path.SkipWhile(p => p != item).Append(item)));
            path.Add(item);
            var level = 0;
            if (children.TryGetValue(item, out var lines))
                foreach (var line in lines)
                    level = Math.Max(level, 1 + Level(line.ComponentItemId, path));
            path.RemoveAt(path.Count - 1);
            visiting.Remove(item);
            levels[item] = level;
            return level;
        }

        foreach (var item in children.Keys) Level(item, new List<string>());
        foreach (var d in demands) Level(d.ItemId, new List<string>());
        return levels;
    }

    private static string? SummarizeList(List<string>? list)
    {
        if (list is null || list.Count == 0) return null;
        var distinct = list.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 ? distinct[0] : $"{distinct[0]} 외 {distinct.Count - 1}건";
    }

    private static IEnumerable<string> AllItems(
        Dictionary<string, decimal> gross, Dictionary<string, List<MrpBomLine>> children)
        => gross.Keys
            .Concat(children.Keys)
            .Concat(children.Values.SelectMany(l => l.Select(x => x.ComponentItemId)))
            .Distinct(StringComparer.Ordinal);

    private static void MergeDue(Dictionary<string, DateTime?> due, string item, DateTime? candidate)
    {
        var current = due.GetValueOrDefault(item);
        if (current is null || (candidate is not null && candidate < current)) due[item] = candidate;
    }

    private static string? Summarize(Dictionary<string, List<string>> sources, string item)
    {
        if (!sources.TryGetValue(item, out var list) || list.Count == 0) return null;
        var distinct = list.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 ? distinct[0] : $"{distinct[0]} 외 {distinct.Count - 1}건";
    }

    private sealed class MrpCycleException(string path) : Exception(path);
}
