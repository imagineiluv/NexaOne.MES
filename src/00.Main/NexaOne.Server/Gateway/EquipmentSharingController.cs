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
[Route("api/v1/ivt/shared-equipment/{tenantId:guid}/{organizationId:guid}")]
public sealed class EquipmentSharingController(IEquipmentSharingBridge bridge, ILogger<EquipmentSharingController> logger) : ControllerBase
{
    [HttpGet("binding")]
    [RequirePermission(Permissions.SysManage)]
    public Task<IActionResult> GetBinding(Guid tenantId, Guid organizationId, CancellationToken ct)
        => Execute(user => bridge.GetScopeBindingAsync(user, tenantId, organizationId, ct));

    [HttpPut("binding")]
    [RequirePermission(Permissions.SysManage)]
    public Task<IActionResult> Bind(Guid tenantId, Guid organizationId, [FromBody] ScopeChange change, CancellationToken ct)
        => Execute(user => bridge.BindScopeAsync(user, tenantId, organizationId, change.PlantId, change.ExpectedVersion, change.Active, ct));

    [HttpGet("assets")]
    public Task<IActionResult> ListEquipment(Guid tenantId, Guid organizationId,
        [FromQuery] InventoryQuery query, CancellationToken ct)
        => Execute(user => bridge.ListEquipmentAsync(user, tenantId, organizationId, PreserveQueryText(query), ct));

    [HttpPost("assets")]
    public Task<IActionResult> Create(Guid tenantId, Guid organizationId, [FromBody] EquipmentCreate change, CancellationToken ct)
        => Execute(user => bridge.CreateEquipmentAsync(user, tenantId, organizationId, change.OperationId, change.Code, change.Name,
            change.Capacity, change.RequiresApproval, ct));

    [HttpPut("assets/{id:guid}")]
    public Task<IActionResult> Update(Guid tenantId, Guid organizationId, Guid id, [FromBody] EquipmentChange change, CancellationToken ct)
        => Execute(user => bridge.UpdateEquipmentAsync(user, tenantId, organizationId, change.Code, change.Name,
            change.Capacity, change.RequiresApproval, id, change.ExpectedVersion ?? Guid.Empty, ct));

    [HttpGet("assets/{id:guid}")]
    public Task<IActionResult> Get(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetEquipmentAsync(user, tenantId, organizationId, id, ct));

    [HttpGet("assets/by-code")]
    public Task<IActionResult> GetByCode(Guid tenantId, Guid organizationId, [FromQuery] string code, CancellationToken ct)
        => Execute(user => bridge.GetEquipmentByCodeAsync(user, tenantId, organizationId, code, ct));

    [HttpPut("assets/{id:guid}/active")]
    public Task<IActionResult> Active(Guid tenantId, Guid organizationId, Guid id, [FromBody] ActiveChange change, CancellationToken ct)
        => Execute(user => bridge.SetEquipmentActiveAsync(user, tenantId, organizationId, id, change.Version, change.Active, ct));

    [HttpGet("bookings")]
    public Task<IActionResult> ListBookings(Guid tenantId, Guid organizationId, CancellationToken ct,
        [FromQuery] Guid? equipmentId = null, [FromQuery] EquipmentBookingState? state = null,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListBookingsAsync(user, tenantId, organizationId, equipmentId, state, offset, limit, ct));

    [HttpPut("bookings/{id:guid}")]
    public Task<IActionResult> RequestBooking(Guid tenantId, Guid organizationId, Guid id, [FromBody] BookingRequest request, CancellationToken ct)
        => Execute(user => bridge.RequestBookingAsync(user, tenantId, organizationId, id, request.EquipmentId,
            request.WorkerId, request.Start, request.End, request.Quantity, ct));

    [HttpGet("bookings/{id:guid}")]
    public Task<IActionResult> GetBooking(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetBookingAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("bookings/{id:guid}/decide")]
    public Task<IActionResult> Decide(Guid tenantId, Guid organizationId, Guid id, [FromBody] BookingDecision decision, CancellationToken ct)
        => Execute(user => bridge.DecideBookingAsync(user, tenantId, organizationId, id, decision.Version, decision.Approve, ct));

    [HttpPost("bookings/{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid tenantId, Guid organizationId, Guid id, [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.CancelBookingAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("bookings/{id:guid}/check-out")]
    public Task<IActionResult> CheckOut(Guid tenantId, Guid organizationId, Guid id, [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.CheckOutAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("bookings/{id:guid}/return")]
    public Task<IActionResult> Return(Guid tenantId, Guid organizationId, Guid id, [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.ReturnAsync(user, tenantId, organizationId, id, command.Version, ct));

    private InventoryQuery PreserveQueryText(InventoryQuery query)
    {
        // Preserve literal whitespace without changing MVC's prefix or first-value rules.
        var values = new QueryStringValueProvider(BindingSource.Query, Request.Query, CultureInfo.InvariantCulture);
        var text = values.GetValue(values.ContainsPrefix(nameof(query)) ? "query.Text" : "Text");
        return text == ValueProviderResult.None ? query : query with { Text = text.FirstValue };
    }

    private async Task<IActionResult> Execute<T>(Func<string, Task<T>> action)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try { return Ok(await action(userId)); }
        catch (BusinessException error)
        {
            if (error.Code == "BUSINESS_ACCESS_DENIED") return Forbid();
            var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404
                : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) || error.Code == "EXPLICIT_CREATE_OR_VERSIONED_UPDATE_REQUIRED" ? 400 : 409;
            return StatusCode(status, new { code = error.Code });
        }
        catch (DBConcurrencyException) { return Conflict(new { code = "BUSINESS_VERSION_CONFLICT" }); }
        catch (Exception error) when (error is DbException
            || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException))
        {
            logger.LogError(error, "Shared equipment persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "Shared equipment storage is unavailable.",
                detail: "A write outcome may be unknown. Read the booking or asset by ID before retrying; "
                    + "for asset creation, retry with the same operation ID and original payload.");
        }
    }

    public sealed record ScopeChange(string PlantId, Guid? ExpectedVersion = null, bool Active = true);
    public sealed record EquipmentCreate(Guid OperationId, string Code, string Name, int Capacity, bool RequiresApproval);
    public sealed record EquipmentChange(string Code, string Name, int Capacity, bool RequiresApproval, Guid? ExpectedVersion = null);
    public sealed record ActiveChange(Guid Version, bool Active);
    public sealed record BookingRequest(Guid EquipmentId, string WorkerId, DateTimeOffset Start, DateTimeOffset End, int Quantity);
    public sealed record BookingDecision(Guid Version, bool Approve);
    public sealed record VersionedCommand(Guid Version);
}
