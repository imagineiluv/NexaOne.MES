namespace NexaOne.SLS.Application;

internal interface ISalesOrderDeliveryStore
{
    Task<SalesOrderDeliveryOutcome> TryRequestAsync(
        string salesOrderId, string deliveryOrderId, string deliveryItemId, string actorId, CancellationToken ct);
}

internal enum SalesOrderDeliveryOutcome
{
    Requested,
    SalesOrderNotFound,
    NotRequestable,
    InvalidReference,
    DeliveryIdentityConflict,
}
