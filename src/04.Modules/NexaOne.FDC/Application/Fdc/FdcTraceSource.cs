using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>
/// FDC 영속 수집 저장소를 모듈 중립 TRACE 읽기 계약으로 노출한다. 소비자의 범위 상관키는
/// 해석하지 않으며, 여러 범위의 결과를 하나의 결정적 시간순 페이지로 합친다.
/// </summary>
public sealed class FdcTraceSource : IFdcTraceSource
{
    private const int MaximumPageSize = 5000;
    private readonly IFdcCollectDataRepository _repository;
    private readonly IFdcTraceRetentionStateRepository _retentionState;

    public FdcTraceSource(IFdcCollectDataRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _retentionState = repository as IFdcTraceRetentionStateRepository
            ?? throw new ArgumentException(
                "FDC TRACE source repository must expose durable retention state.",
                nameof(repository));
    }

    public async Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
        IReadOnlyCollection<FdcTraceReadScope> scopes,
        int maxCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (maxCount is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "Page size must be between 1 and 5000.");
        if (scopes.Count == 0) return Array.Empty<FdcTraceSample>();

        var scopeIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedScopes = new List<FdcTraceReadScope>(scopes.Count);
        foreach (var scope in scopes)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var normalized = NormalizeUtc(scope);
            Validate(normalized, scopeIds);
            normalizedScopes.Add(normalized);
        }

        var retentionAtReadStart = await _retentionState.GetTraceRetentionStateAsync(ct);
        EnsureNoTraceGap(
            normalizedScopes,
            NormalizeUtc(retentionAtReadStart.CompletenessBoundary));

        var samples = new List<FdcTraceSample>();
        foreach (var scope in normalizedScopes)
        {
            ct.ThrowIfCancellationRequested();

            var rows = await _repository.GetTraceAsync(
                scope.EquipmentId,
                scope.ParameterId,
                scope.EffectiveFrom,
                scope.EffectiveTo,
                scope.AfterCollectedAt,
                scope.AfterCollectId,
                maxCount,
                ct);
            foreach (var row in rows)
            {
                if (!string.Equals(row.EquipmentId, scope.EquipmentId, StringComparison.Ordinal)
                    || !string.Equals(row.ParameterId, scope.ParameterId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"FDC TRACE repository returned sample '{row.Id}' outside scope '{scope.ScopeId}'.");
                }

                samples.Add(new FdcTraceSample(
                    scope.ScopeId,
                    row.Id,
                    row.EquipmentId,
                    row.ParameterId,
                    row.Value,
                    row.Quality,
                    NormalizeUtc(row.CollectedAt)));
            }
        }

        // A purge can advance the durable boundary after the first check but before a scope query
        // materializes. Re-read after all rows are materialized so that overlap becomes an explicit
        // gap instead of a plausible-looking partial page.
        var retentionAtReadEnd = await _retentionState.GetTraceRetentionStateAsync(ct);
        EnsureNoTraceGap(
            normalizedScopes,
            NormalizeUtc(retentionAtReadEnd.CompletenessBoundary));

        return samples
            .OrderBy(sample => sample.CollectedAt)
            .ThenBy(sample => sample.CollectId, StringComparer.Ordinal)
            .ThenBy(sample => sample.ScopeId, StringComparer.Ordinal)
            .Take(maxCount)
            .ToList();
    }

    private static void EnsureNoTraceGap(
        IEnumerable<FdcTraceReadScope> scopes,
        DateTime completenessBoundary)
    {
        foreach (var scope in scopes)
        {
            var requestedFrom = scope.AfterCollectedAt is { } cursor
                                && cursor > scope.EffectiveFrom
                ? cursor
                : scope.EffectiveFrom;
            if (requestedFrom < completenessBoundary)
                throw new FdcTraceGapException(scope.ScopeId, requestedFrom, completenessBoundary);
        }
    }

    private static void Validate(FdcTraceReadScope scope, ISet<string> scopeIds)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.ScopeId))
            throw new ArgumentException("Scope ID is required.", nameof(scope));
        if (!scopeIds.Add(scope.ScopeId))
            throw new ArgumentException($"Duplicate scope ID '{scope.ScopeId}'.", nameof(scope));
        if (string.IsNullOrWhiteSpace(scope.EquipmentId))
            throw new ArgumentException("Equipment ID is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(scope.ParameterId))
            throw new ArgumentException("Parameter ID is required.", nameof(scope));
        if (scope.EffectiveTo is { } effectiveTo && effectiveTo <= scope.EffectiveFrom)
            throw new ArgumentException("EffectiveTo must be later than EffectiveFrom.", nameof(scope));
        if (scope.AfterCollectedAt.HasValue != !string.IsNullOrWhiteSpace(scope.AfterCollectId))
            throw new ArgumentException(
                "AfterCollectedAt and AfterCollectId must be supplied together.",
                nameof(scope));
    }

    private static FdcTraceReadScope NormalizeUtc(FdcTraceReadScope scope) => scope with
    {
        EffectiveFrom = NormalizeUtc(scope.EffectiveFrom),
        EffectiveTo = scope.EffectiveTo is { } effectiveTo
            ? NormalizeUtc(effectiveTo)
            : null,
        AfterCollectedAt = scope.AfterCollectedAt is { } afterCollectedAt
            ? NormalizeUtc(afterCollectedAt)
            : null,
    };

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
