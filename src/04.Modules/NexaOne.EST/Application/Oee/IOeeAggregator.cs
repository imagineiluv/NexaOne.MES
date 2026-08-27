namespace NexaOne.EST.Application.Oee;

/// <summary>OEE 집계 계약(EST 모듈 소유) — 원자료(상태이력·표준 설비 출력 이벤트)를 설정(상태분류·목표)과 결합해 OEE 마트를 계산·적재한다.
/// 구현은 Infrastructure의 OeeAggregationRepository(EesDataSource·명명 SQL). 워커/수동 트리거가 이 계약에 의존한다.</summary>
public interface IOeeAggregator
{
    /// <summary>plant 로컬 달력 일자를 작업조 인식으로 집계한다. 그 날 작업조 윈도가 있으면 작업조별(계획시간=작업조 길이),
    /// 없으면 일자 전체를 1윈도로 집계한다. 적재 행 수를 반환한다.</summary>
    Task<int> AggregateDayAsync(DateTime date, CancellationToken ct = default);

    /// <summary>각 plant의 현재 로컬 일자를 기준으로 최근 N일을 자동 재집계합니다.</summary>
    Task<int> AggregateRecentLocalDaysAsync(
        DateTime utcNow,
        int lookbackDays,
        CancellationToken ct = default);

    /// <summary>[windowStart, windowEnd) 구간을 목표 등록 설비별로 집계·적재한다. shiftId가 있으면 작업조 행으로,
    /// plannedOverride&gt;0이면 그 값을 계획시간으로 쓴다. 적재 행 수를 반환한다.</summary>
    Task<int> AggregateWindowAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId = null, decimal plannedOverride = 0m,
        CancellationToken ct = default);

    /// <summary>작업자와 요청 일자를 감사 원장에 남기는 수동 일 집계입니다.</summary>
    Task<int> AggregateDayManuallyAsync(
        DateTime localDate,
        string actorId,
        CancellationToken ct = default);

    /// <summary>다른 윈도와 충돌하지 않는 고유 scope로 적재하고 작업자/윈도를 감사하는 수동 집계입니다.</summary>
    Task<int> AggregateWindowManuallyAsync(
        DateTime windowStart,
        DateTime windowEnd,
        string? shiftId,
        decimal plannedOverride,
        string actorId,
        CancellationToken ct = default);
}
