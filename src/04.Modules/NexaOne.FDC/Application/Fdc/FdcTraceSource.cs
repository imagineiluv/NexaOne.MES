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

    public FdcTraceSource(IFdcCollectDataRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
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
        var samples = new List<FdcTraceSample>();
        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();
            Validate(scope, scopeIds);

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
                    row.CollectedAt));
            }
        }

        return samples
            .OrderBy(sample => sample.CollectedAt)
            .ThenBy(sample => sample.CollectId, StringComparer.Ordinal)
            .ThenBy(sample => sample.ScopeId, StringComparer.Ordinal)
            .Take(maxCount)
            .ToList();
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
}
