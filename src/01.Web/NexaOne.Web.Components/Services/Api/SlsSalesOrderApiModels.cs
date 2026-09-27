namespace NexaOne.Web.Services.Api;

public sealed record SlsSalesOrderDraftRequest(
    string SalesOrderId, string? SalesOrderName, string PlantId,
    string CustomerId, string ProductId, DateTime? PlanStartDate,
    DateTime PlanEndDate, decimal PlanQty);

public sealed record SlsSalesOrderStateDto(string SalesOrderId, string Status);

public sealed record SlsSalesOrderActionResult(bool Success, string? Error, int StatusCode);
