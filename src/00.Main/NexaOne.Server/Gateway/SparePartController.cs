using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/ems/spare-parts")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SparePartController : ControllerBase
{
    private readonly ISparePartBridge _bridge;
    public SparePartController(ISparePartBridge bridge) => _bridge = bridge;

    [HttpPut("stock-policies/{partId}")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> SaveStockPolicy(
        string partId,
        [FromBody] SparePartStockPolicyCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.SaveStockPolicyAsync(
            command with { PartId = partId, ActorId = actor }, ct)).ToActionResult();
    }

    [HttpPut("suppliers/{partSupplierId}")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> SaveSupplier(
        string partSupplierId,
        [FromBody] SparePartSupplierCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.SaveSupplierAsync(
            command with { PartSupplierId = partSupplierId, ActorId = actor }, ct)).ToActionResult();
    }

    [HttpPut("equipment-bom/{bomItemId}")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> SaveEquipmentBom(
        string bomItemId,
        [FromBody] EquipmentPartBomCommand command,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId()?.Trim();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        return (await _bridge.SaveEquipmentBomAsync(
            command with { BomItemId = bomItemId, ActorId = actor }, ct)).ToActionResult();
    }

    [HttpGet("{partId}/replenishment")]
    [RequirePermission(Permissions.EmsRead)]
    public async Task<IActionResult> RecommendReplenishment(string partId, CancellationToken ct)
        => (await _bridge.RecommendReplenishmentAsync(partId, ct)).ToActionResult();
}
