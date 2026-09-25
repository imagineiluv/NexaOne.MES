using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/ivt/stock/{tenantId:guid}/{organizationId:guid}")]
public sealed class StockController(IStockBridge bridge, ILogger<StockController> logger) : ControllerBase
{
    [HttpPost("exports/masters.csv")]
    public Task<IActionResult> ExportMasters(Guid tenantId, Guid organizationId,
        [FromBody] StockMasterExportQuery query, CancellationToken ct)
        => Execute(user => bridge.ExportMastersCsvAsync(user, tenantId, organizationId, query, ct),
            export => File(export.Content, export.ContentType, export.FileName));

    [HttpPost("exports/master-jobs")]
    public Task<IActionResult> QueueMasterExport(Guid tenantId, Guid organizationId,
        [FromBody] StockMasterExportJobRequest command, CancellationToken ct)
        => Execute(user => bridge.QueueMasterExportAsync(user, tenantId, organizationId, command, ct),
            job => AcceptedAtAction(nameof(GetMasterExportJob), new { tenantId, organizationId, jobId = job.Id }, job));

    [HttpGet("exports/master-jobs/{jobId:guid}")]
    public Task<IActionResult> GetMasterExportJob(Guid tenantId, Guid organizationId, Guid jobId, CancellationToken ct)
        => Execute(user => bridge.GetMasterExportJobAsync(user, tenantId, organizationId, jobId, ct));

    [HttpPost("exports/master-jobs/{jobId:guid}/retry")]
    public Task<IActionResult> RetryMasterExport(Guid tenantId, Guid organizationId, Guid jobId,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.RetryMasterExportAsync(user, tenantId, organizationId, jobId, command.Version, ct));

    [HttpGet("exports/master-jobs/{jobId:guid}/download")]
    public Task<IActionResult> DownloadMasterExport(Guid tenantId, Guid organizationId, Guid jobId, CancellationToken ct)
        => Execute(user => bridge.DownloadMasterExportAsync(user, tenantId, organizationId, jobId, ct),
            export => File(export.Content, export.ContentType, export.FileName));

    [HttpPost("imports/masters")]
    public Task<IActionResult> ImportMasters(Guid tenantId, Guid organizationId,
        [FromBody] StockMasterImportRequest command, CancellationToken ct)
        => Execute(user => bridge.ImportMastersAsync(user, tenantId, organizationId, command, ct),
            result => result.Applied ? Ok(result) : UnprocessableEntity(result));

    [HttpGet("products")]
    public Task<IActionResult> ListProducts(Guid tenantId, Guid organizationId,
        [FromQuery] InventoryQuery query, CancellationToken ct)
        => Execute(user => bridge.ListProductsAsync(user, tenantId, organizationId, PreserveQueryText(query), ct));

    [HttpPut("products/{productId}")]
    public Task<IActionResult> EnrollProduct(Guid tenantId, Guid organizationId, string productId,
        [FromBody] ProductEnrollment command, CancellationToken ct)
        => Execute(user => bridge.EnrollProductAsync(user, tenantId, organizationId, productId, command.Unit, ct));

    [HttpGet("products/{productId}")]
    public Task<IActionResult> GetProduct(Guid tenantId, Guid organizationId, string productId, CancellationToken ct)
        => Execute(user => bridge.GetProductAsync(user, tenantId, organizationId, productId, ct));

    [HttpGet("variants/{id:guid}")]
    public Task<IActionResult> GetVariant(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetVariantAsync(user, tenantId, organizationId, id, ct));

    [HttpGet("warehouses")]
    public Task<IActionResult> ListWarehouses(Guid tenantId, Guid organizationId,
        [FromQuery] InventoryQuery query, CancellationToken ct)
        => Execute(user => bridge.ListWarehousesAsync(user, tenantId, organizationId, PreserveQueryText(query), ct));

    [HttpPost("warehouses")]
    public Task<IActionResult> CreateWarehouse(Guid tenantId, Guid organizationId, [FromBody] WarehouseCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateWarehouseAsync(user, tenantId, organizationId, command.OperationId, command.Code, command.Name, ct));

    [HttpPut("warehouses/{id:guid}")]
    public Task<IActionResult> UpdateWarehouse(Guid tenantId, Guid organizationId, Guid id, [FromBody] WarehouseChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateWarehouseAsync(user, tenantId, organizationId, id, command.Version, command.Code, command.Name, ct));

    [HttpGet("warehouses/{id:guid}")]
    public Task<IActionResult> GetWarehouse(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetWarehouseAsync(user, tenantId, organizationId, id, ct));

    [HttpPut("warehouses/{id:guid}/active")]
    public Task<IActionResult> SetWarehouseActive(Guid tenantId, Guid organizationId, Guid id, [FromBody] ActiveChange command, CancellationToken ct)
        => Execute(user => bridge.SetWarehouseActiveAsync(user, tenantId, organizationId, id, command.Version, command.Active, ct));

    [HttpGet("warehouses/{id:guid}/balances")]
    public Task<IActionResult> ListWarehouseBalances(Guid tenantId, Guid organizationId, Guid id, [FromQuery] Guid? variantId,
        CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListBalancesAsync(user, tenantId, organizationId, id, variantId, offset, limit, ct));

    [HttpGet("balances")]
    public Task<IActionResult> GetBalance(Guid tenantId, Guid organizationId, [FromQuery] Guid variantId, [FromQuery] Guid warehouseId, CancellationToken ct)
        => Execute(async user => await bridge.GetBalanceAsync(user, tenantId, organizationId, variantId, warehouseId, ct)
            // A missing balance row is a definite answer (nothing recorded), reported as 404 rather than an empty 200 body.
            ?? throw new BusinessException("STOCK_BALANCE_NOT_FOUND"));

    [HttpGet("reports/balances")]
    public Task<IActionResult> BalanceReport(Guid tenantId, Guid organizationId, [FromQuery] Guid? warehouseId,
        [FromQuery] Guid? variantId, CancellationToken ct)
        => Execute(user => bridge.BuildBalanceReportAsync(user, tenantId, organizationId, warehouseId, variantId, ct));

    [HttpGet("reports/balances/export.csv")]
    public Task<IActionResult> ExportBalanceReport(Guid tenantId, Guid organizationId, [FromQuery] Guid? warehouseId,
        [FromQuery] Guid? variantId, CancellationToken ct)
        => Execute(user => bridge.ExportBalanceReportCsvAsync(user, tenantId, organizationId, warehouseId, variantId, ct),
            export => File(export.Content, export.ContentType, export.FileName));

    [HttpPost("movements")]
    public Task<IActionResult> Post(Guid tenantId, Guid organizationId, [FromBody] StockPosting posting, CancellationToken ct)
        => Execute(user => bridge.PostAsync(user, tenantId, organizationId, posting, ct));

    [HttpGet("movements/{id:guid}")]
    public Task<IActionResult> GetMovement(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetMovementAsync(user, tenantId, organizationId, id, ct));

    [HttpGet("movements")]
    public Task<IActionResult> ListMovements(Guid tenantId, Guid organizationId, [FromQuery] Guid variantId,
        CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListMovementsAsync(user, tenantId, organizationId, variantId, offset, limit, ct));

    [HttpPost("movements/{id:guid}/reverse")]
    public Task<IActionResult> Reverse(Guid tenantId, Guid organizationId, Guid id, [FromBody] ReversalCommand command, CancellationToken ct)
        => Execute(user => bridge.ReverseAsync(user, tenantId, organizationId, id, command.Version, command.OperationId, command.Reference, ct));

    [HttpPost("reservations")]
    public Task<IActionResult> Reserve(Guid tenantId, Guid organizationId, [FromBody] ReservationCommand command, CancellationToken ct)
        => Execute(user => bridge.ReserveAsync(user, tenantId, organizationId, command.OperationId, command.VariantId,
            command.WarehouseId, command.Quantity, command.Reference, ct));

    [HttpGet("reservations/{id:guid}")]
    public Task<IActionResult> GetReservation(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetReservationAsync(user, tenantId, organizationId, id, ct));

    [HttpGet("reservations")]
    public Task<IActionResult> ListReservations(Guid tenantId, Guid organizationId, [FromQuery] Guid? variantId, [FromQuery] Guid? warehouseId,
        [FromQuery] StockReservationState? state, CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListReservationsAsync(user, tenantId, organizationId, variantId, warehouseId, state, offset, limit, ct));

    [HttpPost("reservations/{id:guid}/release")]
    public Task<IActionResult> ReleaseReservation(Guid tenantId, Guid organizationId, Guid id, [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.ReleaseReservationAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("reservations/{id:guid}/consume")]
    public Task<IActionResult> ConsumeReservation(Guid tenantId, Guid organizationId, Guid id, [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.ConsumeReservationAsync(user, tenantId, organizationId, id, command.Version, ct));

    private InventoryQuery PreserveQueryText(InventoryQuery query)
    {
        // MVC converts whitespace-only strings to null. Retain literal search text and
        // its length while keeping the same prefix and first-value rules as MVC binding.
        var values = new QueryStringValueProvider(BindingSource.Query, Request.Query, CultureInfo.InvariantCulture);
        var text = values.GetValue(values.ContainsPrefix(nameof(query)) ? "query.Text" : "Text");
        return text == ValueProviderResult.None ? query : query with { Text = text.FirstValue };
    }

    private async Task<IActionResult> Execute<T>(Func<string, Task<T>> action, Func<T, IActionResult>? success = null)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try
        {
            var value = await action(userId);
            return success is null ? Ok(value) : success(value);
        }
        catch (BusinessException error)
        {
            if (error.Code == "BUSINESS_ACCESS_DENIED") return Forbid();
            var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404
                : error.Code is "STOCK_REPORT_TOO_LARGE" or "STOCK_MASTER_EXPORT_TOO_LARGE" ? StatusCodes.Status413PayloadTooLarge
                : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) || error.Code == "EXPLICIT_CREATE_OR_VERSIONED_UPDATE_REQUIRED" ? 400 : 409;
            return StatusCode(status, new { code = error.Code });
        }
        catch (DBConcurrencyException) { return Conflict(new { code = "BUSINESS_VERSION_CONFLICT" }); }
        catch (Exception error) when (error is DbException
            || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException))
        {
            logger.LogError(error, "Stock persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "Stock storage is unavailable.",
                detail: "A write outcome may be unknown. Read the warehouse, movement or reservation by ID before retrying; "
                    + "for master import, warehouse creation, stock posting or reservation, retry with the same operation ID and original payload.");
        }
    }

    public sealed record ProductEnrollment(string Unit);
    public sealed record WarehouseCreate(Guid OperationId, string Code, string Name);
    public sealed record WarehouseChange(Guid Version, string Code, string Name);
    public sealed record ActiveChange(Guid Version, bool Active);
    public sealed record ReversalCommand(Guid Version, Guid OperationId, string Reference);
    public sealed record ReservationCommand(Guid OperationId, Guid VariantId, Guid WarehouseId, decimal Quantity, string Reference);
    public sealed record VersionedCommand(Guid Version);
}
