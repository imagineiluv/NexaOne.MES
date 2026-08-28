namespace NexaOne.ServiceContracts.Est;

/// <summary>얇은 브리지(ADR-008) — OEE 수동 집계 트리거. plugin(EST)의 IOeeAggregator를 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록해 얇은 컨트롤러가 호출한다. 운영자가 워커(기본 OFF)를 기다리지 않고 특정 일자/윈도를 재집계한다.
/// 파생 마트(EST_OEE_SUMMARY/LOSS)의 워커 산출물(AGG_/AGL_)만 delete+insert하므로 멱등하고 원자료는 건드리지 않는다.
/// 적재된 행 수를 반환한다.</summary>
public interface IOeeAggregationBridge : INexaModuleBridge
{
    Task<int> AggregateDayManuallyAsync(
        DateTime localDate, string actorId, CancellationToken ct = default);
    Task<int> AggregateWindowManuallyAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId, decimal plannedOverride,
        string actorId, CancellationToken ct = default);
}
