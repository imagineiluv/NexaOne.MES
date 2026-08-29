using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Application.Materials;

internal interface IFeedSessionRepository
{
    Task<FeedSessionState?> GetAsync(
        string feedSessionId,
        CancellationToken ct = default);

    Task<FeedSessionWrite?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task<FeedSessionWrite?> GetBySourceEventAsync(
        string sourceSystem,
        string sourceEventId,
        CancellationToken ct = default);

    Task<bool> TryMountAsync(
        FeedSessionState session,
        FeedSessionWrite write,
        CancellationToken ct = default);

    Task<bool> TryCloseAsync(
        FeedSessionState session,
        int expectedVersion,
        FeedSessionWrite write,
        CancellationToken ct = default);
}
