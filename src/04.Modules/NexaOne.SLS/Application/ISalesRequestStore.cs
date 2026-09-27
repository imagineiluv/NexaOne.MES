namespace NexaOne.SLS.Application;

internal interface ISalesRequestStore
{
    Task<SalesRequestDraftInsertOutcome> TryCreateDraftAsync(SalesRequestDraft draft, CancellationToken ct);
    Task<SalesRequestReceiptOutcome> TryReceiveAsync(SalesRequestReceipt receipt, CancellationToken ct);
    Task<SalesRequestWithdrawOutcome> TryWithdrawAsync(string salesRequestId, string actorId, CancellationToken ct);
}

internal sealed record SalesRequestDraft(
    string SalesRequestId,
    string SalesRequestName,
    string CustomerId,
    string ProductId,
    DateTime RequestDate,
    decimal RequestQty,
    string ActorId);

internal sealed record SalesRequestReceipt(
    string SalesRequestId,
    string SalesOrderId,
    string PlantId,
    string SalesOrderName,
    DateTime PlanStartDate,
    DateTime PlanEndDate,
    string ActorId);

internal enum SalesRequestDraftInsertOutcome { Created, InvalidReference, IdentityConflict }
internal enum SalesRequestReceiptOutcome { Received, RequestNotFound, NotReceivable, PlantNotFound, OrderIdentityConflict, InvalidReference }
internal enum SalesRequestWithdrawOutcome { Withdrawn, RequestNotFound, NotWithdrawable }
