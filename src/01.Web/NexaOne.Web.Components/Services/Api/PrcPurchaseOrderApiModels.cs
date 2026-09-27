namespace NexaOne.Web.Services.Api;

public sealed record PrcPurchaseOrderStateDto(string PurchaseOrderId, string Status);

public sealed record PrcPurchaseOrderActionResult(bool Success, string? Error, int StatusCode);
