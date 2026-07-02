using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 EST 설비상태 엔드포인트(ADR-008 얇은 브리지). plugin-ALC EquipmentStateService를
/// IEquipmentStateBridge로 호출한다. 라우트/상태코드는 NexaOne.API EstController와 동일. 쓰기는 est:manage 수동 검사.
/// (modules ON에서만 IEquipmentStateBridge가 등록되므로 동작한다.)</summary>
[ApiController]
[Route("api/v1/est")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class EstBridgeController : ControllerBase
{
    private readonly IEquipmentStateBridge _bridge;
    private readonly IEquipmentAlarmBridge _alarmBridge;

    public EstBridgeController(IEquipmentStateBridge bridge, IEquipmentAlarmBridge alarmBridge)
    {
        _bridge = bridge;
        _alarmBridge = alarmBridge;
    }

    [HttpGet("state-matrix")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateMatrixDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStateMatrix([FromQuery] string plantId, CancellationToken ct)
        => Ok(await _bridge.GetMatrixAsync(plantId, ct));

    [HttpGet("state-matrix/allowed")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateMatrixDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllowedTransitions(
        [FromQuery] string plantId, [FromQuery] string fromState, CancellationToken ct)
        => Ok(await _bridge.GetAllowedTransitionsAsync(plantId, fromState, ct));

    [HttpPost("state-matrix")]
    [ProducesResponseType<EquipmentStateMatrixDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> UpsertMatrix([FromBody] UpsertMatrixRequest req, CancellationToken ct)
    {
        var result = await _bridge.UpsertMatrixAsync(
            req.PlantId, req.FromStateId, req.ToStateId, req.AllowFlag, req.SetStateId, req.RequireReason, ct);
        return result.ToActionResult();
    }

    [HttpGet("equipment-state")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEquipmentStates([FromQuery] string plantId, CancellationToken ct)
        => Ok(await _bridge.GetEquipmentStatesAsync(plantId, ct));

    [HttpPost("equipment-state/change")]
    [ProducesResponseType<EquipmentStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> ChangeState([FromBody] ChangeStateRequest req, CancellationToken ct)
    {
        // requestedBy는 토큰 주체에서 취한다(비-부인성). 감사 사용자는 AuditUserContextMiddleware가 CurrentUserContext에 이미 설정.
        var result = await _bridge.ChangeStateAsync(
            req.EquipmentId, req.PlantId, req.ToState, CurrentUserId, req.Reason, "UI", req.ExpectedVersion, ct);
        return result.ToActionResult();
    }

    [HttpGet("equipment-state/{equipmentId}/history")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateHistoryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStateHistory(string equipmentId, CancellationToken ct)
        => Ok(await _bridge.GetHistoryAsync(equipmentId, 50, ct));

    // ===== 설비알람(ADR-008 얇은 브리지) — 쓰기는 est:manage, 조회는 인증만. =====

    [HttpGet("alarms")]
    [ProducesResponseType<IReadOnlyList<EquipmentAlarmDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveAlarms([FromQuery] string plantId, CancellationToken ct)
        => Ok(await _alarmBridge.GetActiveAlarmsAsync(plantId, ct));

    [HttpGet("alarms/count")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveAlarmCount(CancellationToken ct)
        => Ok(await _alarmBridge.GetActiveAlarmCountAsync(ct));

    [HttpPost("alarms")]
    [ProducesResponseType<EquipmentAlarmDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> RecordAlarm([FromBody] RecordAlarmRequest req, CancellationToken ct)
    {
        var result = await _alarmBridge.RecordAlarmAsync(
            req.AlarmId, req.EquipmentId, req.AlarmCode, req.AlarmName, req.Level, ct);
        return result.ToActionResult();
    }

    [HttpPost("alarms/{alarmId}/clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> ClearAlarm(string alarmId, CancellationToken ct)
    {
        var result = await _alarmBridge.ClearAlarmAsync(alarmId, DateTime.UtcNow, ct);
        return result.ToActionResult();
    }

    private string CurrentUserId => User.CurrentUserId() ?? "SYSTEM";
}

public record ChangeStateRequest(string EquipmentId, string PlantId, string ToState, string? Reason, int? ExpectedVersion);
public record UpsertMatrixRequest(string PlantId, string FromStateId, string ToStateId, bool AllowFlag, string? SetStateId, bool RequireReason);
public record RecordAlarmRequest(string AlarmId, string EquipmentId, string AlarmCode, string AlarmName, string Level);
