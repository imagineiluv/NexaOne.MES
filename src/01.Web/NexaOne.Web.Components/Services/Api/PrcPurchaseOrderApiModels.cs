namespace NexaOne.Web.Services.Api;

public sealed record PrcPurchaseOrderDraftRequest(
    string PurchaseOrderId, string PlantId, string? PurchaseOrderName,
    string? VendorId, decimal OrderQuantity);

public sealed record PrcPurchaseOrderStateDto(string PurchaseOrderId, string Status);

public sealed record PrcPurchaseOrderActionResult(bool Success, string? Error, int StatusCode);
