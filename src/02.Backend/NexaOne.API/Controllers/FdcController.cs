using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Hubs;
using NexaOne.EST.Application.Est;
using NexaOne.FDC.Application.Fdc;
using NexusFramework;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/fdc")]
[Authorize]
public class FdcController(
    FdcInterlockService interlockService,
    FdcDataService dataService,
    FdcParameterGroupService groupService,
    FdcAlarmService fdcAlarmService,
    EquipmentAlarmService alarmService,
    PlantController plant,
    IEesHubNotifier notifier,
    NexaOne.Infrastructure.Persistence.IOutboxRepository outbox,
    ILogger<FdcController> logger) : ControllerBase
{
    // ADR-002 — 설비 상태 변경을 Event Bus로 발행(outbox). 디스패처가 Kafka로 발행하고 구독자가 SignalR로 푸시한다.
    // best-effort: outbox 적재 실패(테이블/디스패처 미가동)가 설비 제어 자체를 막지 않도록 한다.
    private async Task PublishEquipmentStateAsync(string state, CancellationToken ct)
    {
        try
        {
            await outbox.EnqueueAsync("EquipmentStateChanged", "FDC", "PLANT", state, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbox enqueue failed for EquipmentStateChanged={State} (제어는 계속)", state);
        }
    }

    // ── Equipment Control (§10.4.4 — PlantController 수동 제어) ─────────────────

    [HttpGet("equipment/state")]
    public IActionResult GetPlantState()
        => Ok(new
        {
            State = plant.StateMachine?.Current.ToString() ?? "Uninitialized",
            OperationMode = plant.OperationMode.ToString(),
            MachineCount = plant.Machines.Count,
            Machines = plant.Machines.Select(m => new { m.Name, State = m.State.ToString() })
        });

    // 설비 lifecycle 변경(기동/정지/비상정지)은 안전 영향 동작 — 역할 인가로 일반 인증 사용자 차단(§9.3)
    [HttpPost("equipment/start")]
    [Authorize(Policy = "perm:fdc:control")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN/OPERATOR 기본 매핑 보유)
    public async Task<IActionResult> StartAll(CancellationToken ct)
    {
        try
        {
            // 수집기 비활성(기본) 등으로 StateMachine 미초기화면 수동 제어가 영구 실패하므로 멱등 초기화
            if (plant.StateMachine is null) await plant.InitializeAsync(ct);
            await plant.StartAsync(ct);
            await PublishEquipmentStateAsync("Running", ct);
            return Ok();
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("equipment/stop")]
    [Authorize(Policy = "perm:fdc:control")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN/OPERATOR 기본 매핑 보유)
    public async Task<IActionResult> StopAll(CancellationToken ct)
    {
        try
        {
            if (plant.StateMachine is null) await plant.InitializeAsync(ct);
            await plant.StopAsync(ct);
            await PublishEquipmentStateAsync("Stopped", ct);
            return Ok();
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("equipment/abort")]
    [Authorize(Policy = "perm:fdc:control")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN/OPERATOR 기본 매핑 보유)
    public async Task<IActionResult> AbortAll([FromBody] AbortRequest req, CancellationToken ct)
    {
        await plant.AbortAsync(req.Reason, ct);
        await PublishEquipmentStateAsync("Aborted", ct);
        return Ok();
    }

    [HttpGet("interlock-rules")]
    public async Task<IActionResult> GetInterlockRules([FromQuery] string? equipmentId, CancellationToken ct)
    {
        var rules = await interlockService.GetRulesAsync(equipmentId ?? string.Empty, ct);
        return Ok(rules);
    }

    [HttpGet("interlock-history")]
    public async Task<IActionResult> GetInterlockHistory(
        [FromQuery] string equipmentId,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken ct)
    {
        var history = await interlockService.GetHistoryAsync(equipmentId, from, to, ct);
        return Ok(history);
    }

    [HttpPost("interlock-rules")]
    public async Task<IActionResult> CreateRule([FromBody] CreateRuleRequest req, CancellationToken ct)
    {
        var result = await interlockService.CreateRuleAsync(
            req.RuleId, req.RuleName, req.EquipmentId, req.ParameterId,
            req.Operator, req.ThresholdValue, req.Action, req.Priority, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("interlock/evaluate")]
    public async Task<IActionResult> Evaluate([FromBody] EvaluateRequest req, CancellationToken ct)
    {
        var result = await interlockService.EvaluateAsync(req.EquipmentId, req.ParameterId, req.Value, ct);
        return Ok(result);
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    [HttpGet("parameters")]
    public async Task<IActionResult> GetParameters([FromQuery] string? equipmentId, CancellationToken ct)
    {
        var list = await dataService.GetParametersAsync(equipmentId ?? string.Empty, ct);
        return Ok(list);
    }

    [HttpPost("parameters")]
    public async Task<IActionResult> CreateParameter([FromBody] CreateParameterRequest req, CancellationToken ct)
    {
        var result = await dataService.CreateParameterAsync(
            req.ParameterId, req.ParameterName, req.EquipmentId, req.Unit,
            req.LowerLimit, req.UpperLimit, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    /// <summary>파라미터를 그룹에 배정/해제한다(GroupId=null이면 해제).</summary>
    [HttpPut("parameters/{parameterId}/group")]
    public async Task<IActionResult> AssignParameterGroup(
        string parameterId, [FromBody] AssignParameterGroupRequest req, CancellationToken ct)
    {
        var result = await dataService.AssignParameterToGroupAsync(parameterId, req.GroupId, ct);
        return result.IsSuccess ? Ok() : BadRequest(result.Error);
    }

    // ── Parameter Groups ──────────────────────────────────────────────────────

    [HttpGet("parameter-groups")]
    public async Task<IActionResult> GetParameterGroups([FromQuery] string equipmentId, CancellationToken ct)
    {
        var groups = await groupService.GetGroupsAsync(equipmentId, ct);
        return Ok(groups);
    }

    [HttpPost("parameter-groups")]
    public async Task<IActionResult> CreateParameterGroup([FromBody] CreateParameterGroupRequest req, CancellationToken ct)
    {
        var result = await groupService.CreateGroupAsync(
            req.GroupId, req.GroupName, req.EquipmentId, req.Description, req.DisplayOrder, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── Alarms (FDC threshold) ────────────────────────────────────────────────

    [HttpGet("alarm-configs")]
    public async Task<IActionResult> GetAlarmConfigs([FromQuery] string equipmentId, CancellationToken ct)
    {
        var configs = await fdcAlarmService.GetConfigsAsync(equipmentId, ct);
        return Ok(configs);
    }

    [HttpPost("alarm-configs")]
    public async Task<IActionResult> CreateAlarmConfig([FromBody] CreateAlarmConfigRequest req, CancellationToken ct)
    {
        var result = await fdcAlarmService.CreateConfigAsync(
            req.AlarmConfigId, req.EquipmentId, req.ParameterId, req.AlarmLevel, req.Operator, req.Threshold, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("alarm-history")]
    public async Task<IActionResult> GetAlarmHistory(
        [FromQuery] string equipmentId,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken ct)
    {
        var history = await fdcAlarmService.GetHistoryAsync(equipmentId, from, to, ct);
        return Ok(history);
    }

    // ── Collect Data ──────────────────────────────────────────────────────────

    [HttpGet("collect-data")]
    public async Task<IActionResult> GetCollectData(
        [FromQuery] string parameterId,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken ct)
    {
        var list = await dataService.GetCollectDataAsync(parameterId, from, to, ct);
        return Ok(list);
    }

    [HttpGet("collect-data/latest")]
    public async Task<IActionResult> GetLatestCollectData(
        [FromQuery] string parameterId,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        // 음수/0/과대값 방어 — 무검증 limit이 TOP(@limit) 쿼리로 직행하지 않도록 1..500으로 클램프
        limit = Math.Clamp(limit, 1, 500);
        var list = await dataService.GetLatestDataAsync(parameterId, limit, ct);
        return Ok(list);
    }

    /// <summary>
    /// 수집 → 인터록 평가 → (조건) 알람 발생 → SignalR 통보
    /// </summary>
    [HttpPost("collect-data")]
    public async Task<IActionResult> RecordData([FromBody] RecordDataRequest req, CancellationToken ct)
    {
        // 1. 데이터 수집 기록
        var recordResult = await dataService.RecordDataAsync(
            req.CollectId, req.EquipmentId, req.ParameterId, req.Value, req.Quality, ct);
        if (recordResult.IsFailure) return BadRequest(recordResult.Error);

        var collected = recordResult.Value;

        // 2. SignalR — 수집 데이터 실시간 통보
        await notifier.NotifyFdcDataReceivedAsync(
            collected.EquipmentId, collected.ParameterId,
            collected.Value, collected.IsOutOfSpec, ct);

        // 3. 인터록 평가
        var interlock = await interlockService.EvaluateAsync(
            req.EquipmentId, req.ParameterId, req.Value, ct);

        if (interlock.IsTriggered)
        {
            // 4. SignalR — 인터록 발동 통보
            await notifier.NotifyInterlockTriggeredAsync(
                req.EquipmentId, req.ParameterId,
                interlock.Action, interlock.Message, ct);

            // 5. ALARM / STOP 액션이면 EPT 알람 자동 생성
            if (interlock.Action is "ALARM" or "STOP")
            {
                var alarmId = $"FDC-{req.CollectId}";
                var alarmCode = $"INTERLOCK_{interlock.Action}";
                var alarmName = $"[FDC] {interlock.Message}";
                var level = interlock.Action == "STOP" ? "CRITICAL" : "WARNING";

                var alarmResult = await alarmService.RecordAlarmAsync(
                    alarmId, req.EquipmentId, alarmCode, alarmName, level, ct);

                if (alarmResult.IsSuccess)
                    await notifier.NotifyAlarmUpdatedAsync(ct);
            }
        }

        return Ok(new
        {
            CollectedData = collected,
            Interlock = interlock
        });
    }
}

public record CreateRuleRequest(
    string RuleId, string RuleName, string EquipmentId, string ParameterId,
    string Operator, decimal ThresholdValue, string Action, int Priority);

public record EvaluateRequest(string EquipmentId, string ParameterId, decimal Value);
public record CreateParameterRequest(
    string ParameterId, string ParameterName, string EquipmentId, string Unit,
    decimal LowerLimit, decimal UpperLimit);
public record RecordDataRequest(
    string CollectId, string EquipmentId, string ParameterId, decimal Value, string Quality);
public record CreateParameterGroupRequest(
    string GroupId, string GroupName, string EquipmentId, string? Description, int DisplayOrder);
public record CreateAlarmConfigRequest(
    string AlarmConfigId, string EquipmentId, string ParameterId,
    string AlarmLevel, string Operator, decimal Threshold);
public record AbortRequest(string Reason);
public record AssignParameterGroupRequest(string? GroupId);
