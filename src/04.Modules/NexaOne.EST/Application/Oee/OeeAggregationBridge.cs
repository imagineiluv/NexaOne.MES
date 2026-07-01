using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Application.Oee;

/// <summary>ADR-008 얇은 브리지 어댑터 — 호스트가 GetBean("Est","oeeAggregationBridge")로 IOeeAggregationBridge 캐스트.
/// 모듈 소유 <see cref="IOeeAggregator"/>(OeeAggregationRepository)로 위임할 뿐, 계산/데이터 접근 로직은 모듈에 있다.</summary>
public sealed class OeeAggregationBridge : IOeeAggregationBridge
{
    private readonly IOeeAggregator _aggregator;

    public OeeAggregationBridge(IOeeAggregator aggregator) => _aggregator = aggregator;

    public Task<int> AggregateDayAsync(DateTime date, CancellationToken ct = default)
        => _aggregator.AggregateDayAsync(date, ct);

    public Task<int> AggregateWindowAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId, decimal plannedOverride, CancellationToken ct = default)
        => _aggregator.AggregateWindowAsync(windowStart, windowEnd, shiftId, plannedOverride, ct);
}
