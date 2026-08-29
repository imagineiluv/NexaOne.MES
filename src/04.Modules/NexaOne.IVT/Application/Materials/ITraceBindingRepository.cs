using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Application.Materials;

internal interface ITraceBindingRepository
{
    Task<TraceBindingState?> GetAsync(string bindingId, CancellationToken ct = default);

    Task<TraceBindingCursor?> GetIngestionCursorAsync(
        string bindingId,
        CancellationToken ct = default);

    Task<TraceBindingWrite?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task<TraceBindingWrite?> GetBySourceEventAsync(
        string sourceSystem,
        string sourceEventId,
        CancellationToken ct = default);

    Task<bool> TryCreateAsync(
        TraceBindingState binding,
        TraceBindingWrite write,
        CancellationToken ct = default);

    Task<bool> TryRetireAsync(
        TraceBindingState binding,
        int expectedVersion,
        TraceBindingWrite write,
        CancellationToken ct = default);
}
