using NexaOne.Common;

namespace NexaOne.QMS.Domain;

/// <summary>SPC 데이터 형태에 따라 적용할 관리도 유형.</summary>
public enum SpcControlChartType
{
    IndividualsMovingRange,
    XBarR,
    XBarS,
    P,
    Np,
    C,
    U
}

/// <summary>특정 관리한계 리비전으로 평가할 부분군 내 단일 관측값.</summary>
public sealed record SpcObservation(
    string ObservationId,
    string ParamId,
    string LimitRevisionId,
    string SubgroupId,
    int SampleIndex,
    decimal Value,
    DateTime ObservedAt);

/// <summary>같은 파라미터와 관리도에 속한 SPC 관측값 묶음.</summary>
public sealed record SpcSubgroup(
    string SubgroupId,
    string ParamId,
    SpcControlChartType ChartType,
    DateTime ObservedAt,
    IReadOnlyList<SpcObservation> Observations)
{
    /// <summary>부분군 관측값의 평균.</summary>
    public decimal Mean => Observations.Count == 0 ? 0m : Observations.Average(x => x.Value);

    /// <summary>부분군 관측값의 범위.</summary>
    public decimal Range => Observations.Count == 0 ? 0m : Observations.Max(x => x.Value) - Observations.Min(x => x.Value);
}

/// <summary>적용 시점이 명시되고 제자리 수정되지 않는 SPC 관리한계 리비전.</summary>
public sealed record SpcControlLimitRevision(
    string RevisionId,
    string ParamId,
    int RevisionNo,
    SpcControlChartType ChartType,
    decimal CenterLine,
    decimal Ucl,
    decimal Lcl,
    DateTime EffectiveFrom,
    string Reason)
{
    /// <summary>중심선에서 상한까지의 1시그마 간격.</summary>
    public decimal UpperSigma => (Ucl - CenterLine) / 3m;

    /// <summary>중심선에서 하한까지의 1시그마 간격.</summary>
    public decimal LowerSigma => (CenterLine - Lcl) / 3m;

    /// <summary>관리한계 순서와 효력 정보를 검증해 리비전을 생성한다.</summary>
    public static Result<SpcControlLimitRevision> Create(
        string revisionId, string paramId, int revisionNo, SpcControlChartType chartType,
        decimal centerLine, decimal ucl, decimal lcl, DateTime effectiveFrom, string reason)
    {
        if (string.IsNullOrWhiteSpace(revisionId))
            return Result.Failure<SpcControlLimitRevision>(Error.Validation(nameof(revisionId), "Revision ID is required."));
        if (string.IsNullOrWhiteSpace(paramId))
            return Result.Failure<SpcControlLimitRevision>(Error.Validation(nameof(paramId), "Parameter ID is required."));
        if (revisionNo <= 0)
            return Result.Failure<SpcControlLimitRevision>(Error.Validation(nameof(revisionNo), "Revision number must be positive."));
        if (ucl <= centerLine || centerLine <= lcl)
            return Result.Failure<SpcControlLimitRevision>(Error.Validation(nameof(ucl), "Limits must satisfy UCL > center line > LCL."));
        if (effectiveFrom == default)
            return Result.Failure<SpcControlLimitRevision>(Error.Validation(nameof(effectiveFrom), "Effective time is required."));

        return new SpcControlLimitRevision(revisionId, paramId, revisionNo, chartType,
            centerLine, ucl, lcl, effectiveFrom, reason ?? string.Empty);
    }
}

/// <summary>Western Electric·Nelson 기반으로 구분한 SPC 이상 신호.</summary>
public enum SpcRuleCode
{
    WesternElectric1,
    WesternElectric2,
    WesternElectric3,
    WesternElectric4,
    NelsonTrend,
    NelsonAlternating
}

/// <summary>특정 관측값에서 검출된 SPC 규칙 위반과 근거.</summary>
public sealed record SpcRuleViolation(
    string ViolationId,
    string ParamId,
    string LimitRevisionId,
    string ObservationId,
    SpcRuleCode RuleCode,
    DateTime DetectedAt,
    string Evidence);

/// <summary>관측 순서를 정규화한 뒤 순수 함수로 SPC 이상 신호를 평가한다.</summary>
public static class SpcRuleEngine
{
    /// <summary>해당 파라미터·한계 리비전의 관측값에 Western Electric·Nelson 규칙을 적용한다.</summary>
    public static IReadOnlyList<SpcRuleViolation> Evaluate(
        SpcControlLimitRevision limits, IEnumerable<SpcObservation> source)
    {
        // 시각·표본 순번으로 시계열을 정렬한다. 두 키까지 같은 관측값은 호출자가 제공한 순서를 유지한다.
        var points = source
            .Where(x => x.ParamId == limits.ParamId && x.LimitRevisionId == limits.RevisionId)
            .OrderBy(x => x.ObservedAt).ThenBy(x => x.SampleIndex).ToList();
        var violations = new List<SpcRuleViolation>();

        for (var end = 0; end < points.Count; end++)
        {
            var current = points[end];
            AddIf(OutsideSigma(current.Value, limits, 3m), SpcRuleCode.WesternElectric1, 1, "point outside 3 sigma");
            AddIf(SameSideBeyond(points, end, 3, 2, 2m, limits), SpcRuleCode.WesternElectric2, 3, "2 of 3 beyond 2 sigma on one side");
            AddIf(SameSideBeyond(points, end, 5, 4, 1m, limits), SpcRuleCode.WesternElectric3, 5, "4 of 5 beyond 1 sigma on one side");
            AddIf(SameSide(points, end, 8, limits.CenterLine), SpcRuleCode.WesternElectric4, 8, "8 consecutive points on one side");
            AddIf(StrictTrend(points, end, 6), SpcRuleCode.NelsonTrend, 6, "6 consecutive increasing or decreasing points");
            AddIf(StrictAlternating(points, end, 14), SpcRuleCode.NelsonAlternating, 14, "14 consecutive alternating points");

            void AddIf(bool condition, SpcRuleCode code, int window, string evidence)
            {
                if (!condition) return;
                // 재평가해도 같은 신호 ID가 나오도록 한계 리비전·관측값·규칙을 키로 삼는다.
                violations.Add(new SpcRuleViolation(
                    $"{limits.RevisionId}:{current.ObservationId}:{code}", limits.ParamId,
                    limits.RevisionId, current.ObservationId, code, current.ObservedAt,
                    $"{evidence}; window={window}"));
            }
        }
        return violations;
    }

    private static bool OutsideSigma(decimal value, SpcControlLimitRevision l, decimal sigma)
        => value > l.CenterLine + sigma * l.UpperSigma || value < l.CenterLine - sigma * l.LowerSigma;

    private static bool SameSideBeyond(
        IReadOnlyList<SpcObservation> p, int end, int window, int required,
        decimal sigma, SpcControlLimitRevision l)
    {
        if (end + 1 < window) return false;
        var upper = 0;
        var lower = 0;
        for (var i = end - window + 1; i <= end; i++)
        {
            if (p[i].Value > l.CenterLine + sigma * l.UpperSigma) upper++;
            if (p[i].Value < l.CenterLine - sigma * l.LowerSigma) lower++;
        }
        return upper >= required || lower >= required;
    }

    private static bool SameSide(IReadOnlyList<SpcObservation> p, int end, int window, decimal center)
    {
        if (end + 1 < window) return false;
        var firstSide = Math.Sign(p[end - window + 1].Value - center);
        if (firstSide == 0) return false;
        for (var i = end - window + 2; i <= end; i++)
            if (Math.Sign(p[i].Value - center) != firstSide) return false;
        return true;
    }

    private static bool StrictTrend(IReadOnlyList<SpcObservation> p, int end, int window)
    {
        if (end + 1 < window) return false;
        var direction = Math.Sign(p[end - window + 2].Value - p[end - window + 1].Value);
        if (direction == 0) return false;
        for (var i = end - window + 2; i <= end; i++)
            if (Math.Sign(p[i].Value - p[i - 1].Value) != direction) return false;
        return true;
    }

    private static bool StrictAlternating(IReadOnlyList<SpcObservation> p, int end, int window)
    {
        if (end + 1 < window) return false;
        var previousDirection = Math.Sign(p[end - window + 2].Value - p[end - window + 1].Value);
        if (previousDirection == 0) return false;
        for (var i = end - window + 3; i <= end; i++)
        {
            var direction = Math.Sign(p[i].Value - p[i - 1].Value);
            if (direction == 0 || direction != -previousDirection) return false;
            previousDirection = direction;
        }
        return true;
    }
}
