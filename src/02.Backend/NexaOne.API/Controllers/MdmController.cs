using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.MDM.Application.Equipments;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/mdm")]
[Authorize]
public class MdmController(EquipmentService equipmentService, MdmMasterService masterService) : ControllerBase
{
    [HttpGet("equipment")]
    public async Task<IActionResult> GetEquipment(
        [FromQuery] string? plantId,
        CancellationToken ct)
    {
        var result = await equipmentService.GetEquipmentListAsync(plantId ?? string.Empty, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("equipment/{equipmentId}")]
    public async Task<IActionResult> GetEquipmentById(string equipmentId, CancellationToken ct)
    {
        var result = await equipmentService.GetEquipmentAsync(equipmentId, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost("equipment")]
    [Authorize(Policy = "perm:mdm:manage")]   // ADR-003 — 기준정보 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    public async Task<IActionResult> CreateEquipment([FromBody] CreateEquipmentRequest req, CancellationToken ct)
    {
        var result = await equipmentService.CreateEquipmentAsync(
            req.EquipmentId, req.EquipmentName, req.PlantId, req.AreaId,
            req.EquipmentType, req.ParentEquipmentId, req.Vendor ?? "", req.Model ?? "",
            req.EquipmentClassId ?? "", ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("equipment/{equipmentId}")]
    [Authorize(Policy = "perm:mdm:manage")]
    public async Task<IActionResult> UpdateEquipment(string equipmentId, [FromBody] UpdateEquipmentRequest req, CancellationToken ct)
    {
        var result = await equipmentService.UpdateEquipmentAsync(
            equipmentId, req.EquipmentName, req.Description ?? "", req.EquipmentType,
            req.Vendor ?? "", req.Model ?? "", ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpDelete("equipment/{equipmentId}")]
    [Authorize(Policy = "perm:mdm:manage")]
    public async Task<IActionResult> DeactivateEquipment(string equipmentId, CancellationToken ct)
    {
        var result = await equipmentService.DeactivateEquipmentAsync(equipmentId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // ── Plants ────────────────────────────────────────────────────────────────

    [HttpGet("plants")]
    public async Task<IActionResult> GetPlants(CancellationToken ct)
        => Ok(await masterService.GetPlantsAsync(ct));

    [HttpPost("plants")]
    [Authorize(Policy = "perm:mdm:manage")]
    public async Task<IActionResult> CreatePlant([FromBody] CreatePlantRequest req, CancellationToken ct)
    {
        var result = await masterService.CreatePlantAsync(req.PlantId, req.PlantName, req.Country, req.TimeZone, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── Areas ─────────────────────────────────────────────────────────────────

    [HttpGet("areas")]
    public async Task<IActionResult> GetAreas([FromQuery] string? plantId, CancellationToken ct)
        => Ok(await masterService.GetAreasByPlantAsync(plantId ?? string.Empty, ct));

    [HttpPost("areas")]
    [Authorize(Policy = "perm:mdm:manage")]
    public async Task<IActionResult> CreateArea([FromBody] CreateAreaRequest req, CancellationToken ct)
    {
        var result = await masterService.CreateAreaAsync(req.AreaId, req.AreaName, req.PlantId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── Products ──────────────────────────────────────────────────────────────

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(CancellationToken ct)
        => Ok(await masterService.GetProductsAsync(ct));

    [HttpPost("products")]
    [Authorize(Policy = "perm:mdm:manage")]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest req, CancellationToken ct)
    {
        var result = await masterService.CreateProductAsync(req.ProductId, req.ProductName, req.ProductType, req.Unit, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── Code Classes ──────────────────────────────────────────────────────────

    [HttpGet("code-classes")]
    public async Task<IActionResult> GetCodeClasses(CancellationToken ct)
        => Ok(await masterService.GetCodeClassesAsync(ct));

    [HttpPost("code-classes")]
    [Authorize(Policy = "perm:mdm:manage")]
    public async Task<IActionResult> CreateCodeClass([FromBody] CreateCodeClassRequest req, CancellationToken ct)
    {
        var result = await masterService.CreateCodeClassAsync(req.CodeClassId, req.CodeClassName, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── Codes ─────────────────────────────────────────────────────────────────

    [HttpGet("codes")]
    public async Task<IActionResult> GetCodes([FromQuery] string codeClassId, CancellationToken ct)
        => Ok(await masterService.GetCodesByClassAsync(codeClassId, ct));

    [HttpPost("codes")]
    [Authorize(Policy = "perm:mdm:manage")]
    public async Task<IActionResult> CreateCode([FromBody] CreateCodeRequest req, CancellationToken ct)
    {
        var result = await masterService.CreateCodeAsync(req.CodeId, req.CodeClassId, req.CodeName, req.SortOrder, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }
}

public record CreateEquipmentRequest(
    string EquipmentId, string EquipmentName, string PlantId, string AreaId,
    string EquipmentType, string? ParentEquipmentId, string? Vendor, string? Model, string? EquipmentClassId);

public record UpdateEquipmentRequest(
    string EquipmentName, string? Description, string EquipmentType, string? Vendor, string? Model);

public record CreatePlantRequest(string PlantId, string PlantName, string Country, string TimeZone);
public record CreateAreaRequest(string AreaId, string AreaName, string PlantId);
public record CreateProductRequest(string ProductId, string ProductName, string ProductType, string Unit);
public record CreateCodeClassRequest(string CodeClassId, string CodeClassName);
public record CreateCodeRequest(string CodeId, string CodeClassId, string CodeName, int SortOrder = 0);
