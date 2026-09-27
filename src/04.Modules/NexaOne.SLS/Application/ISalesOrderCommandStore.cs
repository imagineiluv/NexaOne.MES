namespace NexaOne.SLS.Application;

internal sealed record SalesOrderDraft(
    string SalesOrderId, string SalesOrderName, string PlantId, string CustomerId,
    string ProductId, DateTime? PlanStartDate, DateTime PlanEndDate,
    decimal PlanQty, string ActorId);

internal enum SalesOrderDraftOutcome
{
    Saved,
    InvalidReference,
    NotEditable,
    IdentityConflict,
}

internal enum SalesOrderChangeOutcome
{
    Applied,
    NotFound,
    NotAllowed,
}

internal interface ISalesOrderCommandStore
{
    Task<SalesOrderDraftOutcome> TrySaveDraftAsync(SalesOrderDraft draft, CancellationToken ct);
    Task<SalesOrderChangeOutcome> TryDeleteDraftAsync(string salesOrderId, string actorId, CancellationToken ct);
    Task<SalesOrderChangeOutcome> TryConfirmAsync(string salesOrderId, string actorId, CancellationToken ct);
    Task<SalesOrderChangeOutcome> TryCloseAsync(string salesOrderId, string actorId, CancellationToken ct);
}
