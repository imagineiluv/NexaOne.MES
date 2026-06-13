using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.SHP.Application.Shp;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/shp")]
[Authorize]
public class ShpController(ShpService dlvService) : ControllerBase
{
    // ── Delivery Orders ───────────────────────────────────────────────────────

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string plantId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var result = await dlvService.GetByPlantAsync(plantId, from, to, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] CreateDeliveryOrderRequest req, CancellationToken ct)
    {
        var result = await dlvService.CreateOrderAsync(req.OrderId, req.CustomerName, req.PlantId, req.RequestedDate, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("orders/{orderId}/confirm")]
    public async Task<IActionResult> ConfirmOrder(string orderId, CancellationToken ct)
    {
        var result = await dlvService.ConfirmOrderAsync(orderId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("orders/{orderId}/ship")]
    public async Task<IActionResult> ShipOrder(string orderId, [FromBody] ShipOrderRequest req, CancellationToken ct)
    {
        var result = await dlvService.ShipOrderAsync(orderId, req.ShippedDate, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("orders/{orderId}/cancel")]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        var result = await dlvService.CancelOrderAsync(orderId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // ── Delivery Items ────────────────────────────────────────────────────────

    [HttpGet("orders/{orderId}/items")]
    public async Task<IActionResult> GetItems(string orderId, CancellationToken ct)
        => Ok(await dlvService.GetItemsByOrderAsync(orderId, ct));

    [HttpPost("orders/{orderId}/items")]
    public async Task<IActionResult> AddItem(string orderId, [FromBody] AddDeliveryItemRequest req, CancellationToken ct)
    {
        var result = await dlvService.AddItemAsync(req.ItemId, orderId, req.ProductId, req.PlannedQty, req.LotId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("items/{itemId}/actual-qty")]
    public async Task<IActionResult> SetActualQty(string itemId, [FromBody] SetActualQtyRequest req, CancellationToken ct)
    {
        var result = await dlvService.SetItemActualQtyAsync(itemId, req.ActualQty, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // ── Shipment History ──────────────────────────────────────────────────────

    [HttpGet("orders/{orderId}/shipment-history")]
    public async Task<IActionResult> GetShipmentHistory(string orderId, CancellationToken ct)
        => Ok(await dlvService.GetShipmentHistoryAsync(orderId, ct));

    [HttpPost("orders/{orderId}/shipment-history")]
    public async Task<IActionResult> RecordShipment(string orderId, [FromBody] RecordShipmentRequest req, CancellationToken ct)
    {
        var result = await dlvService.RecordShipmentAsync(req.HistoryId, orderId, req.ShippedQty, req.ShippedBy, req.Carrier, req.TrackingNo, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }
}

public record CreateDeliveryOrderRequest(string OrderId, string CustomerName, string PlantId, DateTime RequestedDate);
public record ShipOrderRequest(DateTime ShippedDate);
public record AddDeliveryItemRequest(string ItemId, string ProductId, decimal PlannedQty, string? LotId);
public record SetActualQtyRequest(decimal ActualQty);
public record RecordShipmentRequest(string HistoryId, decimal ShippedQty, string ShippedBy, string? Carrier, string? TrackingNo);
