using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 MDM 기준정보 엔드포인트(ADR-008 얇은 브리지). plugin-ALC MDM 서비스를
/// IMdmEquipmentBridge/IMdmMasterBridge로 호출한다. 불변식 있는 생성/상태전이/갱신만 노출(순수 조회는 게이트웨이).
/// 쓰기는 mdm:manage 수동 검사. Result→HTTP(BridgeResultExtensions: Conflict→409·NotFound→404·Validation→400).
/// (modules ON에서만 동작.)</summary>
[ApiController]
[Route("api/v1/mdm")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class MdmBridgeController : ControllerBase
{
    private readonly IMdmEquipmentBridge _equipment;
    private readonly IMdmMasterBridge _master;

    public MdmBridgeController(IMdmEquipmentBridge equipment, IMdmMasterBridge master)
    {
        _equipment = equipment;
        _master = master;
    }

    // ── Equipment ───────────────────────────────────────────────────────────────

    [HttpPost("equipment")]
    [ProducesResponseType<EquipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> CreateEquipment([FromBody] CreateEquipmentRequest req, CancellationToken ct)
    {
        return (await _equipment.CreateEquipmentAsync(
            req.EquipmentId, req.EquipmentName, req.PlantId, req.AreaId, req.EquipmentType,
            req.ParentEquipmentId, req.Vendor ?? "", req.Model ?? "", req.EquipmentClassId ?? "", ct)).ToActionResult();
    }

    [HttpPost("equipment/{equipmentId}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> DeactivateEquipment(string equipmentId, CancellationToken ct)
    {
        return (await _equipment.DeactivateEquipmentAsync(equipmentId, ct)).ToActionResult();
    }

    [HttpPut("equipment/{equipmentId}")]
    [ProducesResponseType<EquipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> UpdateEquipment(string equipmentId, [FromBody] UpdateEquipmentRequest req, CancellationToken ct)
    {
        return (await _equipment.UpdateEquipmentAsync(
            equipmentId, req.Name, req.Description ?? "", req.EquipmentType, req.Vendor ?? "", req.Model ?? "", ct)).ToActionResult();
    }

    // ── Master (Plant/Area/Product/CodeClass/Code) ───────────────────────────────

    [HttpPost("plants")]
    [ProducesResponseType<PlantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> CreatePlant([FromBody] CreatePlantRequest req, CancellationToken ct)
    {
        return (await _master.CreatePlantAsync(req.PlantId, req.PlantName, req.Country ?? "", req.TimeZone ?? "", ct)).ToActionResult();
    }

    [HttpPost("areas")]
    [ProducesResponseType<AreaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> CreateArea([FromBody] CreateAreaRequest req, CancellationToken ct)
    {
        return (await _master.CreateAreaAsync(req.AreaId, req.AreaName, req.PlantId, ct)).ToActionResult();
    }

    [HttpPost("products")]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest req, CancellationToken ct)
    {
        return (await _master.CreateProductAsync(req.ProductId, req.ProductName, req.ProductType ?? "", req.Unit ?? "", ct)).ToActionResult();
    }

    [HttpPost("code-classes")]
    [ProducesResponseType<CodeClassDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> CreateCodeClass([FromBody] CreateCodeClassRequest req, CancellationToken ct)
    {
        return (await _master.CreateCodeClassAsync(req.CodeClassId, req.CodeClassName, ct)).ToActionResult();
    }

    [HttpPost("codes")]
    [ProducesResponseType<CodeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequirePermission(Permissions.MdmManage)]
    public async Task<IActionResult> CreateCode([FromBody] CreateCodeRequest req, CancellationToken ct)
    {
        return (await _master.CreateCodeAsync(req.CodeId, req.CodeClassId, req.CodeName, req.SortOrder, ct)).ToActionResult();
    }

}

public record CreateEquipmentRequest(
    string EquipmentId, string EquipmentName, string PlantId, string AreaId, string EquipmentType,
    string? ParentEquipmentId = null, string? Vendor = null, string? Model = null, string? EquipmentClassId = null);
public record UpdateEquipmentRequest(
    string Name, string? Description, string EquipmentType, string? Vendor, string? Model);
public record CreatePlantRequest(string PlantId, string PlantName, string? Country, string? TimeZone);
public record CreateAreaRequest(string AreaId, string AreaName, string PlantId);
public record CreateProductRequest(string ProductId, string ProductName, string? ProductType, string? Unit);
public record CreateCodeClassRequest(string CodeClassId, string CodeClassName);
public record CreateCodeRequest(string CodeId, string CodeClassId, string CodeName, int SortOrder = 0);
