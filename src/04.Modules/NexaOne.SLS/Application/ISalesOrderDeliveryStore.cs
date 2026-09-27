namespace NexaOne.SLS.Application;

internal interface ISalesOrderDeliveryStore
{
    Task<SalesOrderDeliveryOutcome> TryRequestAsync(
        string salesOrderId, string deliveryOrderId, string deliveryItemId, string actorId, CancellationToken ct);

    Task<SalesOrderDeliveryConfirmationResult> TryConfirmAsync(
        string salesOrderId, string actorId, CancellationToken ct);
}

internal enum SalesOrderDeliveryConfirmationOutcome
{
    Confirmed,
    AlreadyConfirmed,
    SalesOrderNotFound,
    NotConfirmable,
    ShipmentNotShipped,
    ShipmentMismatch,
}

internal sealed record SalesOrderDeliveryConfirmationResult(
    SalesOrderDeliveryConfirmationOutcome Outcome, string? DeliveryOrderId = null, decimal DeliveredQty = 0);

internal enum SalesOrderDeliveryOutcome
{
    Requested,
    SalesOrderNotFound,
    NotRequestable,
    InvalidReference,
    DeliveryIdentityConflict,
}
