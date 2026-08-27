using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.IVT.Application.Materials;

/// <summary>
/// FDC가 소유한 영속 TRACE 읽기 계약과 IVT가 소유한 inbox 사이를 연결한다. 원천 SQL과
/// IVT 바인딩 SQL은 각각의 모듈 안에 남고, 이 서비스는 계약 DTO를 바인딩 스냅샷으로만 변환한다.
/// </summary>
public sealed class TraceIngestionService
{
    private readonly IFdcTraceSource _source;
    private readonly ITraceProjectionRepository _repository;

    public TraceIngestionService(
        IFdcTraceSource source,
        ITraceProjectionRepository repository)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<int> EnqueueAsync(int batchSize, CancellationToken ct = default)
    {
        var limit = Math.Clamp(batchSize, 1, 5000);
        var bindings = await _repository.GetSourceBindingsAsync(ct);
        if (bindings.Count == 0) return 0;

        var byId = bindings.ToDictionary(binding => binding.BindingId, StringComparer.Ordinal);
        var samples = await _source.ReadAsync(
            bindings.Select(binding => new FdcTraceReadScope(
                binding.BindingId,
                binding.EquipmentId,
                binding.ParameterId,
                binding.EffectiveFrom,
                binding.EffectiveTo,
                binding.LastEnqueuedAt,
                binding.LastEnqueuedCollectId)).ToArray(),
            limit,
            ct);
        if (samples.Count == 0) return 0;

        var items = new List<TraceProjectionItem>(samples.Count);
        foreach (var sample in samples)
        {
            if (!byId.TryGetValue(sample.ScopeId, out var binding))
            {
                throw new InvalidOperationException(
                    $"FDC TRACE source returned unknown scope '{sample.ScopeId}'.");
            }

            items.Add(binding.Snapshot(new TraceSourceObservation(
                sample.ScopeId,
                sample.CollectId,
                sample.EquipmentId,
                sample.ParameterId,
                sample.Value,
                sample.Quality,
                sample.CollectedAt)));
        }

        return await _repository.AddToInboxAsync(items, ct);
    }
}
