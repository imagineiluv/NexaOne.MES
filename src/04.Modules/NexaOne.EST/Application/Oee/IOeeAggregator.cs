namespace NexaOne.EST.Application.Oee;

/// <summary>OEE 집계 계약(EST 모듈 소유) — 원자료(상태이력·POM_LOT)를 설정(상태분류·목표)과 결합해 OEE 마트를 계산·적재한다.
/// 구현은 Infrastructure의 OeeAggregationRepository(EesDataSource·명명 SQL). 워커/수동 트리거가 이 계약에 의존한다.</summary>
public interface IOeeAggregator
{
    /// <summary>일자(UTC)를 작업조 인식으로 집계한다. 그 날 작업조 윈도가 있으면 작업조별(계획시간=작업조 길이),
    /// 없으면 일자 전체를 1윈도로 집계한다. 적재 행 수를 반환한다.</summary>
    Task<int> AggregateDayAsync(DateTime date, CancellationToken ct = default);

    /// <summary>[windowStart, windowEnd) 구간을 목표 등록 설비별로 집계·적재한다. shiftId가 있으면 작업조 행으로,
    /// plannedOverride&gt;0이면 그 값을 계획시간으로 쓴다. 적재 행 수를 반환한다.</summary>
    Task<int> AggregateWindowAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId = null, decimal plannedOverride = 0m,
        CancellationToken ct = default);
}
