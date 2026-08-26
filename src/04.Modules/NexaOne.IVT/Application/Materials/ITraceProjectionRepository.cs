using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Application.Materials;

public interface ITraceProjectionRepository
{
    /// <summary>활성 IVT 바인딩과 각 바인딩의 마지막 inbox 원천 커서를 반환한다.</summary>
    Task<IReadOnlyList<TraceProjectionBinding>> GetSourceBindingsAsync(
        CancellationToken ct = default);

    /// <summary>이미 Common 원천 계약으로 읽은 표본과 바인딩 스냅샷을 durable inbox에 멱등 추가한다.</summary>
    Task<int> AddToInboxAsync(
        IReadOnlyCollection<TraceProjectionItem> items,
        CancellationToken ct = default);

    Task<IReadOnlyList<TraceProjectionItem>> GetPendingAsync(
        int batchSize,
        CancellationToken ct = default);

    Task<TraceProjectionState?> GetStateAsync(
        string bindingId,
        CancellationToken ct = default);

    Task<IReadOnlyList<MaterialFeedSession>> GetFeedSessionsAsync(
        string plantId,
        string equipmentId,
        string feedPointId,
        DateTime collectedAt,
        CancellationToken ct = default);

    /// <summary>Marks an inbox row terminal and, when provided, advances its calculator checkpoint atomically.</summary>
    Task CompleteAsync(
        TraceProjectionItem item,
        TraceProjectionState? nextState,
        string status,
        string? consumptionId,
        string? detail,
        CancellationToken ct = default);

    Task MarkErrorAsync(
        TraceProjectionItem item,
        string error,
        CancellationToken ct = default);

    /// <summary>Releases a binding lease acquired by <see cref="GetPendingAsync"/>.</summary>
    Task ReleaseLeaseAsync(
        string bindingId,
        string leaseOwnerId,
        CancellationToken ct = default);
}
