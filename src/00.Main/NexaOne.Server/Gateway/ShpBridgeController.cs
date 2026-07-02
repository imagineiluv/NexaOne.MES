using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Shp;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 SHP 출하 엔드포인트(ADR-008 얇은 브리지). plugin-ALC ShpService를 IShipmentBridge로 호출한다.
/// 쓰기(생성/확정/출하/취소)는 shp:manage 수동 검사. Result→HTTP(BridgeResultExtensions). (modules ON에서만 동작.)</summary>
[ApiController]
[Route("api/v1/shp")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class ShpBridgeController : ControllerBase
{
    private readonly IShipmentBridge _bridge;
    public ShpBridgeController(IShipmentBridge bridge) => _bridge = bridge;

    [HttpGet("orders")]
    [ProducesResponseType<IReadOnlyList<DeliveryOrderDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string plantId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => Ok(await _bridge.GetOrdersByPlantAsync(plantId, from, to, ct));

    [HttpPost("orders")]
    [ProducesResponseType<DeliveryOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.ShpManage)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateDeliveryOrderRequest req, CancellationToken ct)
    {
        return (await _bridge.CreateOrderAsync(req.OrderId, req.CustomerName, req.PlantId, req.RequestedDate, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.ShpManage)]
    public async Task<IActionResult> ConfirmOrder(string orderId, CancellationToken ct)
    {
        return (await _bridge.ConfirmOrderAsync(orderId, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/ship")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.ShpManage)]
    public async Task<IActionResult> ShipOrder(string orderId, [FromBody] ShipDeliveryOrderRequest req, CancellationToken ct)
    {
        return (await _bridge.ShipOrderAsync(orderId, req.ShippedDate, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.ShpManage)]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        return (await _bridge.CancelOrderAsync(orderId, ct)).ToActionResult();
    }

}

public record CreateDeliveryOrderRequest(string OrderId, string CustomerName, string PlantId, DateTime RequestedDate);
public record ShipDeliveryOrderRequest(DateTime ShippedDate);
