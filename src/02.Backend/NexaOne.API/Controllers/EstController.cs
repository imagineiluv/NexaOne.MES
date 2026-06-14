using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Hubs;
using NexaOne.EST.Application.Est;
using NexaOne.MDM.Application.Equipments;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/est")]
[Authorize]
public class EstController(
    EquipmentAlarmService alarmService,
    EquipmentStateService stateService,
    EquipmentService equipmentService,
    IEesHubNotifier notifier) : ControllerBase
{
    // ── State Matrix ──────────────────────────────────────────────────────────

    [HttpGet("state-matrix")]
    public async Task<IActionResult> GetStateMatrix([FromQuery] string plantId, CancellationToken ct)
        => Ok(await stateService.GetMatrixAsync(plantId, ct));

    [HttpGet("state-matrix/allowed")]
    public async Task<IActionResult> GetAllowedTransitions(
        [FromQuery] string plantId, [FromQuery] string fromState, CancellationToken ct)
        => Ok(await stateService.GetAllowedTransitionsAsync(plantId, fromState, ct));

    [HttpPost("state-matrix")]
    [Authorize(Policy = "perm:est:manage")]   // ADR-003 — 설비상태 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    public async Task<IActionResult> UpsertMatrix([FromBody] UpsertMatrixRequest req, CancellationToken ct)
    {
        var result = await stateService.UpsertMatrixAsync(
            req.PlantId, req.FromStateId, req.ToStateId,
            req.AllowFlag, req.SetStateId, req.RequireReason, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── Equipment State ───────────────────────────────────────────────────────

    [HttpGet("equipment-state")]
    public async Task<IActionResult> GetEquipmentStates([FromQuery] string plantId, CancellationToken ct)
        => Ok(await stateService.GetEquipmentStatesAsync(plantId, ct));

    [HttpPost("equipment-state/change")]
    [Authorize(Policy = "perm:est:manage")]
    public async Task<IActionResult> ChangeState([FromBody] ChangeStateRequest req, CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("sub")?.Value ?? "SYSTEM";

        // 미등록 설비는 상태 추적 대상이 아니다 — EST_EQUIPMENT_STATE.EQUIPMENT_ID는 MDM_EQUIPMENT FK라
        // 운영(FK 강제)에서 미등록 설비 호출 시 500(FK 위반)이 된다. 사전 검증으로 명확한 400을 돌려준다.
        var equipment = await equipmentService.GetEquipmentAsync(req.EquipmentId, ct);
        if (equipment.IsFailure)
            return BadRequest(equipment.Error);

        var result = await stateService.ChangeStateAsync(
            req.EquipmentId, req.PlantId, req.ToState,
            userId, req.Reason ?? "", "UI",
            req.ExpectedVersion, ct);

        if (result.IsSuccess)
            await notifier.NotifyEquipmentStateChangedAsync(req.EquipmentId, result.Value.CurrentStateId, ct);

        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("equipment-state/{equipmentId}/history")]
    public async Task<IActionResult> GetStateHistory(string equipmentId, CancellationToken ct)
        => Ok(await stateService.GetHistoryAsync(equipmentId, 50, ct));

    // ── Alarms ────────────────────────────────────────────────────────────────

    [HttpGet("alarms")]
    public async Task<IActionResult> GetActiveAlarms([FromQuery] string? plantId, CancellationToken ct)
    {
        var result = await alarmService.GetActiveAlarmsAsync(plantId ?? string.Empty, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("alarms")]
    [Authorize(Policy = "perm:est:manage")]
    public async Task<IActionResult> RecordAlarm([FromBody] RecordAlarmRequest req, CancellationToken ct)
    {
        var result = await alarmService.RecordAlarmAsync(
            req.AlarmId, req.EquipmentId, req.AlarmCode, req.AlarmName, req.Level, ct);
        if (result.IsSuccess)
            await notifier.NotifyAlarmUpdatedAsync(ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("alarms/{alarmId}/clear")]
    [Authorize(Policy = "perm:est:manage")]
    public async Task<IActionResult> ClearAlarm(string alarmId, [FromBody] ClearAlarmRequest req, CancellationToken ct)
    {
        var result = await alarmService.ClearAlarmAsync(alarmId, req.ClearedAt, ct);
        if (result.IsSuccess)
            await notifier.NotifyAlarmUpdatedAsync(ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }
}

public record RecordAlarmRequest(string AlarmId, string EquipmentId, string AlarmCode, string AlarmName, string Level);
public record ClearAlarmRequest(DateTime ClearedAt);
public record UpsertMatrixRequest(
    string PlantId, string FromStateId, string ToStateId,
    bool AllowFlag, string? SetStateId, bool RequireReason);
public record ChangeStateRequest(
    string EquipmentId, string PlantId, string ToState,
    string? Reason, int? ExpectedVersion);
