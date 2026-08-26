namespace NexaOne.EST.Domain.Takt;

/// <summary>특정 집계 기간의 생산 요구량과 가용 시간을 초 단위로 표현한다.</summary>
public sealed record TaktTargetDefinition(
    decimal NetAvailableSeconds,
    decimal RequiredQty,
    decimal IdealCycleSecondsPerUnit,
    string QuantityUom,
    string TimeUom = "s/unit");

/// <summary>
/// 한 집계 범위의 TrackOut 실적이다. 전체 실적과 유효한 TrackIn/TrackOut 시간이 있는 측정 실적을
/// 분리해, 시간 근거가 없는 수량이 실제 사이클타임을 왜곡하지 않게 한다.
/// </summary>
public sealed record TaktActuals(
    decimal ActualQty,
    decimal MeasuredQty,
    decimal ActualRunSeconds,
    string QuantityUom);

/// <summary>목표 택트타임, 실제 사이클타임 및 OEE 시간가동률을 함께 제공하는 계산 결과다.</summary>
public sealed record TaktTimeResult(
    decimal TargetTaktSecondsPerUnit,
    decimal IdealCycleSecondsPerUnit,
    decimal? ActualCycleSecondsPerUnit,
    decimal? DeviationSecondsPerUnit,
    decimal? DeviationRatio,
    decimal AvailabilityRatio,
    decimal ActualQty,
    decimal MeasuredQty,
    decimal ActualRunSeconds,
    string QuantityUom,
    string TimeUom);

/// <summary>
/// 고객 수요 기준의 목표 택트, 설비 기준의 이상 사이클, 실적 기준의 실제 사이클을 서로 섞지 않고 계산한다.
/// OEE 시간가동률은 동일 기간의 OEE 집계 결과를 받아 사용하며 이 계산기에서 중복 산출하지 않는다.
/// </summary>
public static class TaktTimeCalculator
{
    /// <summary>
    /// 순가용시간/요구수량으로 목표 택트를 계산하고, 시간 측정이 유효한 TrackOut만으로 실제 사이클과 편차를 구한다.
    /// </summary>
    public static TaktTimeResult Compute(
        TaktTargetDefinition target,
        TaktActuals actual,
        decimal oeeAvailabilityRatio)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(actual);
        if (target.NetAvailableSeconds <= 0m)
            throw new ArgumentOutOfRangeException(nameof(target.NetAvailableSeconds));
        if (target.RequiredQty <= 0m)
            throw new ArgumentOutOfRangeException(nameof(target.RequiredQty));
        if (target.IdealCycleSecondsPerUnit <= 0m)
            throw new ArgumentOutOfRangeException(nameof(target.IdealCycleSecondsPerUnit));
        if (!string.Equals(target.TimeUom, "s/unit", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Takt time UOM must be s/unit.", nameof(target.TimeUom));
        if (string.IsNullOrWhiteSpace(target.QuantityUom)
            || !string.Equals(target.QuantityUom, actual.QuantityUom, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Target and TrackOut quantity UOM must match.", nameof(actual.QuantityUom));
        if (actual.ActualQty < 0m || actual.MeasuredQty < 0m || actual.ActualRunSeconds < 0m)
            throw new ArgumentOutOfRangeException(nameof(actual));
        if (actual.MeasuredQty > actual.ActualQty)
            throw new ArgumentException("Measured quantity cannot exceed actual TrackOut quantity.", nameof(actual));
        if ((actual.MeasuredQty == 0m) != (actual.ActualRunSeconds == 0m))
            throw new ArgumentException("Measured quantity and measured run seconds must both be zero or positive.", nameof(actual));
        if (oeeAvailabilityRatio is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(oeeAvailabilityRatio));

        var targetTakt = target.NetAvailableSeconds / target.RequiredQty;
        // 실제 생산수량 전체가 아닌 측정 가능한 수량만 분모로 써서 누락된 시간 데이터가 0초 생산처럼 보이지 않게 한다.
        decimal? actualCycle = actual.MeasuredQty > 0m
            ? actual.ActualRunSeconds / actual.MeasuredQty
            : null;
        decimal? deviation = actualCycle - targetTakt;
        decimal? deviationRatio = deviation / targetTakt;

        return new TaktTimeResult(
            Round4(targetTakt), Round4(target.IdealCycleSecondsPerUnit),
            Round4(actualCycle), Round4(deviation), Round6(deviationRatio), Round6(oeeAvailabilityRatio),
            Round4(actual.ActualQty), Round4(actual.MeasuredQty), Round4(actual.ActualRunSeconds),
            target.QuantityUom.Trim(), "s/unit");
    }

    /// <summary>화면·저장 결과가 환경별 은행가 반올림에 흔들리지 않도록 소수 넷째 자리에서 명시적으로 반올림한다.</summary>
    private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>측정값이 없는 상태를 유지하면서 소수 넷째 자리로 반올림한다.</summary>
    private static decimal? Round4(decimal? value) => value.HasValue ? Round4(value.Value) : null;

    /// <summary>비율 정밀도를 보존하기 위해 선택 값을 소수 여섯째 자리로 반올림한다.</summary>
    private static decimal? Round6(decimal? value) => value.HasValue ? Math.Round(value.Value, 6, MidpointRounding.AwayFromZero) : null;

    /// <summary>비율 값을 소수 여섯째 자리로 반올림한다.</summary>
    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
