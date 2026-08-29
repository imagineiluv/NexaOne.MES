namespace NexaOne.EST.Domain.Oee;

/// <summary>상태 코드 분류(EST_STATE_CATEGORY). IsProductive=가동(가용성 분자), IsDowntime=비가동 손실,
/// IsScheduled=계획 생산시간 포함(비계획 IDLE은 제외). Category는 유실 집계 라벨(Breakdown/Setup/...).</summary>
public sealed record OeeStateCategory(string Category, bool IsProductive, bool IsDowntime, bool IsScheduled);

/// <summary>상태 전이 1건(EST_EQUIPMENT_STATE_HISTORY). ChangedAt 시점에 FromState→ToState로 바뀌었다.</summary>
public sealed record OeeStateTransition(DateTime ChangedAt, string FromState, string ToState);

/// <summary>윈도 내 표준 설비 출력 수량 집계. Good = Total - Defect.</summary>
public sealed record OeeLotCounts(decimal TotalQty, decimal DefectQty);

/// <summary>설비 OEE 목표(EST_OEE_TARGET). IdealCycleTimeSec=성능 계산 기준, PlannedMinutes=계획시간 폴백.</summary>
public sealed record OeeTarget(decimal IdealCycleTimeSec, decimal PlannedMinutes);

/// <summary>
/// 한 번의 유실 발생 구간입니다. 같은 카테고리가 여러 번 발생해도 합치지 않아
/// EST_OEE_LOSS의 행 수와 실제 상태 전이 시각이 발생 건수/시각을 보존합니다.
/// </summary>
public sealed record OeeLossLine(
    string Category,
    decimal Minutes,
    DateTime OccurredAt,
    DateTime EndedAt);

/// <summary>계산된 OEE 지표 1행(설비×윈도). 비율은 분율(0~1)로 반올림 4자리.</summary>
public sealed record OeeResult(
    decimal PlannedMinutes, decimal OperatingMinutes, decimal DowntimeMinutes,
    decimal TotalCount, decimal GoodCount, decimal DefectCount,
    decimal Availability, decimal Performance, decimal Quality, decimal Oee,
    IReadOnlyList<OeeLossLine> Losses);

/// <summary>순수 OEE 계산기(무-부작용·무-DB) — 원자료(상태 전이 구간 + 생산 수량 + 목표 + 상태분류)를 결합해
/// OEE = 가용성 × 성능 × 품질과 6대 손실 유실 시간을 산출한다. 가용성은 상태이력을 윈도에 타일링해 계산한다:
/// 첫 전이 이전 [윈도시작, 첫전이)=첫전이.FromState, 각 전이 [전이, 다음전이/윈도끝)=전이.ToState.
/// 계획시간 = (작업조/근무달력 override) 또는 스케줄 상태(가동+비가동) 합, 가용성 = 가동/계획, 성능 =
/// (이상사이클×총생산)/(가동초), 품질 = 양품/총생산. 비율은 [0,1]로 클램프(데이터 이상 시 OEE≤1 보장).</summary>
public static class OeeCalculator
{
    /// <param name="plannedOverride">작업조/근무달력 기반 계획 생산시간(분). &gt;0이면 상태이력 파생 계획시간보다
    /// 우선한다(스케줄이 권위 있는 계획시간 근거). 0이면 상태이력 파생 → 목표 순으로 폴백.</param>
    public static OeeResult Compute(
        DateTime windowStart, DateTime windowEnd,
        IReadOnlyList<OeeStateTransition> transitions,
        OeeLotCounts lots,
        OeeTarget target,
        IReadOnlyDictionary<string, OeeStateCategory> categories,
        OeeStateCategory unknownCategory,
        decimal plannedOverride = 0m)
    {
        // 상태별 구간 시간(분) 누적 — 카테고리 분류로 가동/비가동/계획/유실을 나눈다.
        decimal operating = 0m, downtime = 0m, planned = 0m, unscheduled = 0m, attributed = 0m;
        var losses = new List<OeeLossLine>();

        void Attribute(string state, DateTime segStart, DateTime segEnd, DateTime occurredAt)
        {
            // 윈도 밖으로 삐져나온 구간은 클램프하고, 0/음수 길이는 버린다.
            if (segStart < windowStart) segStart = windowStart;
            if (segEnd > windowEnd) segEnd = windowEnd;
            var minutes = (decimal)(segEnd - segStart).TotalMinutes;
            if (minutes <= 0m) return;

            attributed += minutes;
            var cat = categories.TryGetValue(state, out var c) ? c : unknownCategory;
            if (cat.IsScheduled) planned += minutes;
            else unscheduled += minutes;
            if (cat.IsProductive) operating += minutes;
            if (cat.IsDowntime)
            {
                downtime += minutes;
                losses.Add(new OeeLossLine(cat.Category, Round(minutes), occurredAt, segEnd));
            }
        }

        if (transitions.Count > 0)
        {
            // 정렬 보장(호출부가 ASC로 주더라도 방어적으로 정렬).
            var ordered = transitions.OrderBy(t => t.ChangedAt).ToList();
            // [윈도시작, 첫 전이) = 첫 전이의 이전 상태(FromState).
            Attribute(ordered[0].FromState, windowStart, ordered[0].ChangedAt, windowStart);
            for (int i = 0; i < ordered.Count; i++)
            {
                var segEnd = i + 1 < ordered.Count ? ordered[i + 1].ChangedAt : windowEnd;
                Attribute(ordered[i].ToState, ordered[i].ChangedAt, segEnd, ordered[i].ChangedAt);
            }
        }

        // 계획시간 우선순위: 작업조/근무달력 override > 상태이력 파생 스케줄 > 목표 계획시간(폴백).
        // Calendar time is a ceiling. Do not add planned stops back into the OEE denominator.
        if (plannedOverride > 0m)
        {
            planned = attributed > 0m
                ? Math.Max(0m, plannedOverride - unscheduled)
                : plannedOverride;
        }
        else if (planned <= 0m)
        {
            planned = target.PlannedMinutes;
        }

        var total = lots.TotalQty;
        var defect = lots.DefectQty;
        var good = total - defect;
        if (good < 0m) good = 0m;

        var availability = planned > 0m ? Clamp01(operating / planned) : 0m;
        var quality = total > 0m ? Clamp01(good / total) : 0m;
        var performance = (operating > 0m && target.IdealCycleTimeSec > 0m)
            ? Clamp01((target.IdealCycleTimeSec * total) / (operating * 60m))
            : 0m;
        var oee = availability * performance * quality;

        return new OeeResult(
            Round(planned), Round(operating), Round(downtime),
            total, good, defect,
            Round(availability), Round(performance), Round(quality), Round(oee),
            losses.OrderBy(static loss => loss.OccurredAt).ToArray());
    }

    private static decimal Clamp01(decimal v) => v < 0m ? 0m : v > 1m ? 1m : v;
    private static decimal Round(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}
