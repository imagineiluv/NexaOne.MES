using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Hubs;
using NexaOne.EPT.Application.Ept;
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
    IEesHubNotifier notifier) : ControllerBase
{
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

    [HttpPost("equipment/start")]
    public async Task<IActionResult> StartAll(CancellationToken ct)
    {
        try { await plant.StartAsync(ct); return Ok(); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("equipment/stop")]
    public async Task<IActionResult> StopAll(CancellationToken ct)
    {
        try { await plant.StopAsync(ct); return Ok(); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("equipment/abort")]
    public async Task<IActionResult> AbortAll([FromBody] AbortRequest req, CancellationToken ct)
    {
        await plant.AbortAsync(req.Reason, ct);
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
