using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Extensions;
using NexaOne.Common;
using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Domain;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/shp")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public class ShpController(ShpService dlvService) : ControllerBase
{
    // ── Delivery Orders ───────────────────────────────────────────────────────

    [HttpGet("orders")]
    [ProducesResponseType<IReadOnlyList<DeliveryOrder>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string plantId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var result = await dlvService.GetByPlantAsync(plantId, from, to, ct);
        return result.ToActionResult();
    }

    [HttpPost("orders")]
    [Authorize(Policy = "perm:shp:manage")]   // ADR-003 — 출하 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    [ProducesResponseType<DeliveryOrder>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateDeliveryOrderRequest req, CancellationToken ct)
    {
        var result = await dlvService.CreateOrderAsync(req.OrderId, req.CustomerName, req.PlantId, req.RequestedDate, ct);
        return result.ToActionResult();
    }

    [HttpPut("orders/{orderId}/confirm")]
    [Authorize(Policy = "perm:shp:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmOrder(string orderId, CancellationToken ct)
    {
        var result = await dlvService.ConfirmOrderAsync(orderId, ct);
        return result.ToActionResult();
    }

    [HttpPut("orders/{orderId}/ship")]
    [Authorize(Policy = "perm:shp:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ShipOrder(string orderId, [FromBody] ShipOrderRequest req, CancellationToken ct)
    {
        var result = await dlvService.ShipOrderAsync(orderId, req.ShippedDate, ct);
        return result.ToActionResult();
    }

    [HttpPut("orders/{orderId}/cancel")]
    [Authorize(Policy = "perm:shp:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        var result = await dlvService.CancelOrderAsync(orderId, ct);
        return result.ToActionResult();
    }

    // ── Delivery Items ────────────────────────────────────────────────────────

    [HttpGet("orders/{orderId}/items")]
    [ProducesResponseType<IReadOnlyList<DeliveryItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetItems(string orderId, CancellationToken ct)
        => Ok(await dlvService.GetItemsByOrderAsync(orderId, ct));

    [HttpPost("orders/{orderId}/items")]
    [Authorize(Policy = "perm:shp:manage")]
    [ProducesResponseType<DeliveryItem>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AddItem(string orderId, [FromBody] AddDeliveryItemRequest req, CancellationToken ct)
    {
        var result = await dlvService.AddItemAsync(req.ItemId, orderId, req.ProductId, req.PlannedQty, req.LotId, ct);
        return result.ToActionResult();
    }

    [HttpPut("items/{itemId}/actual-qty")]
    [Authorize(Policy = "perm:shp:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetActualQty(string itemId, [FromBody] SetActualQtyRequest req, CancellationToken ct)
    {
        var result = await dlvService.SetItemActualQtyAsync(itemId, req.ActualQty, ct);
        return result.ToActionResult();
    }

    // ── Shipment History ──────────────────────────────────────────────────────

    [HttpGet("orders/{orderId}/shipment-history")]
    [ProducesResponseType<IReadOnlyList<ShipmentHistory>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetShipmentHistory(string orderId, CancellationToken ct)
        => Ok(await dlvService.GetShipmentHistoryAsync(orderId, ct));

    [HttpPost("orders/{orderId}/shipment-history")]
    [Authorize(Policy = "perm:shp:manage")]
    [ProducesResponseType<ShipmentHistory>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecordShipment(string orderId, [FromBody] RecordShipmentRequest req, CancellationToken ct)
    {
        var result = await dlvService.RecordShipmentAsync(req.HistoryId, orderId, req.ShippedQty, req.ShippedBy, req.Carrier, req.TrackingNo, ct);
        return result.ToActionResult();
    }
}

public record CreateDeliveryOrderRequest(string OrderId, string CustomerName, string PlantId, DateTime RequestedDate);
public record ShipOrderRequest(DateTime ShippedDate);
public record AddDeliveryItemRequest(string ItemId, string ProductId, decimal PlannedQty, string? LotId);
public record SetActualQtyRequest(decimal ActualQty);
public record RecordShipmentRequest(string HistoryId, decimal ShippedQty, string ShippedBy, string? Carrier, string? TrackingNo);
