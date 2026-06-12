using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.PPM.Application.Lots;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/lots")]
[Authorize]
public class LotController(LotTrackingService trackingService) : ControllerBase
{
    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value ?? string.Empty;

    [HttpGet]
    public async Task<IActionResult> GetLots(
        [FromQuery] string plantId, [FromQuery] string? state, CancellationToken ct)
    {
        var result = await trackingService.GetLotsAsync(plantId, state, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("{lotId}")]
    public async Task<IActionResult> GetLot(string lotId, CancellationToken ct)
    {
        var result = await trackingService.GetRouteAsync(lotId, ct);
        return result.IsSuccess ? Ok(result.Value.Lot) : NotFound(result.Error);
    }

    /// <summary>Lot 진행 경로 + 이력 + Mixing 투입 관계 (설계 19.4.8 LotRoute).</summary>
    [HttpGet("{lotId}/route")]
    public async Task<IActionResult> GetRoute(string lotId, CancellationToken ct)
    {
        var result = await trackingService.GetRouteAsync(lotId, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    public async Task<IActionResult> CreateLot([FromBody] CreateLotRequest req, CancellationToken ct)
    {
        var result = await trackingService.CreateLotAsync(
            req.PlantId, req.LotId, req.WorkOrderId, req.ProductId,
            req.Qty, req.RouteSteps, CurrentUserId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("{lotId}/track-in")]
    public async Task<IActionResult> TrackIn(string lotId, [FromBody] TrackInRequest req, CancellationToken ct)
    {
        var result = await trackingService.TrackInAsync(new TrackInCommand(
            req.PlantId, lotId, req.EquipmentId, req.RecipeDefId, req.RecipeDefVersion, CurrentUserId), ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("{lotId}/track-out")]
    public async Task<IActionResult> TrackOut(string lotId, [FromBody] TrackOutRequest req, CancellationToken ct)
    {
        var defects = req.Defects?.Select(d => new DefectEntry(d.DefectCode, d.DefectQty)).ToList();
        var result = await trackingService.TrackOutAsync(new TrackOutCommand(
            req.PlantId, lotId, req.EquipmentId, req.Qty, defects, req.CarrierId, CurrentUserId), ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    /// <summary>Mixing TrackIn/Out — 투입 Lot 소비 + 출력 Lot 생성/가산 (설계 19.4.7).</summary>
    [HttpPost("mixing/track-in-out")]
    public async Task<IActionResult> MixingTrackInOut([FromBody] MixingTrackRequest req, CancellationToken ct)
    {
        var inputs = (req.Inputs ?? []).Select(i => new MixingInput(i.LotId, i.InQty)).ToList();
        var result = await trackingService.MixingTrackInOutAsync(new MixingTrackCommand(
            req.PlantId, req.OutputLotId, req.ProductId, req.EquipmentId,
            req.OutputRouteSteps ?? [], inputs, CurrentUserId), ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("{lotId}/hold")]
    public async Task<IActionResult> Hold(string lotId, CancellationToken ct)
    {
        var result = await trackingService.HoldAsync(lotId, CurrentUserId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("{lotId}/release-hold")]
    public async Task<IActionResult> ReleaseHold(string lotId, CancellationToken ct)
    {
        var result = await trackingService.ReleaseHoldAsync(lotId, CurrentUserId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    /// <summary>생산 추적 보고서 (설계 19.4.8) — Lot/설비/공정/기간 필터.</summary>
    [HttpGet("/api/v1/reports/lot-tracking")]
    public async Task<IActionResult> GetTrackingReport(
        [FromQuery] string plantId, [FromQuery] string? lotId, [FromQuery] string? equipmentId,
        [FromQuery] string? processId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int maxRows = LotTrackingService.DefaultReportRows, CancellationToken ct = default)
    {
        var result = await trackingService.GetTrackingReportAsync(
            plantId, lotId, equipmentId, processId, from, to, maxRows, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }
}

public record CreateLotRequest(
    string PlantId, string LotId, string? WorkOrderId, string ProductId,
    decimal Qty, IReadOnlyList<string> RouteSteps);
public record TrackInRequest(string PlantId, string EquipmentId, string? RecipeDefId, int? RecipeDefVersion);
public record DefectEntryRequest(string DefectCode, decimal DefectQty);
public record TrackOutRequest(
    string PlantId, string EquipmentId, decimal Qty,
    IReadOnlyList<DefectEntryRequest>? Defects, string? CarrierId);
public record MixingInputRequest(string LotId, decimal InQty);
public record MixingTrackRequest(
    string PlantId, string OutputLotId, string ProductId, string EquipmentId,
    IReadOnlyList<string>? OutputRouteSteps, IReadOnlyList<MixingInputRequest>? Inputs);
