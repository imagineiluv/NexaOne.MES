using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Extensions;
using NexaOne.QMS.Application.Qms;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/qms")]
[Authorize]
public class QmsController(QmsService qmsService) : ControllerBase
{
    // ── Defects ───────────────────────────────────────────────────────────────

    [HttpGet("defects")]
    public async Task<IActionResult> GetDefectsByLot([FromQuery] string lotId, CancellationToken ct)
    {
        var result = await qmsService.GetDefectsByLotAsync(lotId, ct);
        return result.ToActionResult();
    }

    [HttpPost("defects")]
    [Authorize(Policy = "perm:qms:manage")]   // ADR-003 — 품질 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    public async Task<IActionResult> RecordDefect([FromBody] RecordDefectRequest req, CancellationToken ct)
    {
        var result = await qmsService.RecordDefectAsync(
            req.DefectId, req.LotId, req.EquipmentId, req.DefectClassId,
            req.Count, req.Rate, req.InspectorId, ct);
        return result.ToActionResult();
    }

    [HttpPut("defects/{defectId}/confirm")]
    [Authorize(Policy = "perm:qms:manage")]
    public async Task<IActionResult> ConfirmDefect(string defectId, [FromBody] ConfirmDefectRequest req, CancellationToken ct)
    {
        var result = await qmsService.ConfirmDefectAsync(defectId, req.ConfirmerId, ct);
        return result.ToActionResult();
    }

    // ── Defect Classes ────────────────────────────────────────────────────────

    [HttpGet("defect-classes")]
    public async Task<IActionResult> GetDefectClasses(CancellationToken ct)
        => Ok(await qmsService.GetDefectClassesAsync(ct));

    [HttpPost("defect-classes")]
    [Authorize(Policy = "perm:qms:manage")]
    public async Task<IActionResult> CreateDefectClass([FromBody] CreateDefectClassRequest req, CancellationToken ct)
    {
        var result = await qmsService.CreateDefectClassAsync(req.DefectClassId, req.DefectClassName, req.Description, req.Severity, ct);
        return result.ToActionResult();
    }

    // ── Inspection Specs ──────────────────────────────────────────────────────

    [HttpGet("inspection-specs")]
    public async Task<IActionResult> GetInspectionSpecs([FromQuery] string? processId, CancellationToken ct)
        => Ok(await qmsService.GetInspectionSpecsAsync(processId, ct));

    [HttpPost("inspection-specs")]
    [Authorize(Policy = "perm:qms:manage")]
    public async Task<IActionResult> CreateInspectionSpec([FromBody] CreateInspectionSpecRequest req, CancellationToken ct)
    {
        var result = await qmsService.CreateInspectionSpecAsync(req.SpecId, req.SpecName, req.ProcessId,
            req.ItemName, req.MeasureType, req.NominalValue, req.TolerancePlus, req.ToleranceMinus, ct);
        return result.ToActionResult();
    }

    // ── Inspection Results ────────────────────────────────────────────────────

    [HttpGet("inspection-results")]
    public async Task<IActionResult> GetInspectionResults([FromQuery] string lotId, CancellationToken ct)
        => Ok(await qmsService.GetInspectionResultsByLotAsync(lotId, ct));

    [HttpPost("inspection-results")]
    [Authorize(Policy = "perm:qms:manage")]
    public async Task<IActionResult> RecordInspectionResult([FromBody] RecordInspectionResultRequest req, CancellationToken ct)
    {
        var result = await qmsService.RecordInspectionResultAsync(req.ResultId, req.SpecId, req.LotId,
            req.EquipmentId, req.InspectorId, req.MeasuredValue, req.AttributeResult, req.IsPass, req.Remark, ct);
        return result.ToActionResult();
    }

    // ── SPC Parameters ────────────────────────────────────────────────────────

    [HttpGet("spc-params")]
    public async Task<IActionResult> GetSpcParams([FromQuery] string equipmentId, CancellationToken ct)
        => Ok(await qmsService.GetSpcParamsAsync(equipmentId, ct));

    [HttpPost("spc-params")]
    [Authorize(Policy = "perm:qms:manage")]
    public async Task<IActionResult> CreateSpcParam([FromBody] CreateSpcParamRequest req, CancellationToken ct)
    {
        var result = await qmsService.CreateSpcParamAsync(req.ParamId, req.ParamName, req.EquipmentId,
            req.ProcessId, req.Mean, req.Ucl, req.Lcl, req.SampleSize, req.Usl, req.Lsl, ct);
        return result.ToActionResult();
    }

    [HttpPut("spc-params/{paramId}/limits")]
    [Authorize(Policy = "perm:qms:manage")]
    public async Task<IActionResult> UpdateSpcLimits(string paramId, [FromBody] UpdateSpcLimitsRequest req, CancellationToken ct)
    {
        var result = await qmsService.UpdateSpcControlLimitsAsync(paramId, req.Mean, req.Ucl, req.Lcl, ct);
        return result.ToActionResult();
    }
}

public record RecordDefectRequest(string DefectId, string LotId, string EquipmentId, string DefectClassId, int Count, decimal Rate, string InspectorId);
public record ConfirmDefectRequest(string ConfirmerId);
public record CreateDefectClassRequest(string DefectClassId, string DefectClassName, string Description, string Severity);
public record CreateInspectionSpecRequest(string SpecId, string SpecName, string ProcessId, string ItemName,
    string MeasureType, decimal? NominalValue, decimal? TolerancePlus, decimal? ToleranceMinus);
public record RecordInspectionResultRequest(string ResultId, string SpecId, string LotId, string EquipmentId,
    string InspectorId, decimal? MeasuredValue, string? AttributeResult, bool? IsPass, string? Remark);
public record CreateSpcParamRequest(string ParamId, string ParamName, string EquipmentId, string ProcessId,
    decimal Mean, decimal Ucl, decimal Lcl, int SampleSize, decimal? Usl, decimal? Lsl);
public record UpdateSpcLimitsRequest(decimal Mean, decimal Ucl, decimal Lcl);
