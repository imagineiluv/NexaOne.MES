using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Extensions;
using NexaOne.API.Hubs;
using NexaOne.Common;
using NexaOne.EST.Application.Est;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.MDM.Application.Equipments;
using NexusFramework;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/fdc")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public class FdcController(
    FdcInterlockService interlockService,
    FdcDataService dataService,
    FdcParameterGroupService groupService,
    FdcAlarmService fdcAlarmService,
    EquipmentAlarmService alarmService,
    EquipmentService equipmentService,
    PlantController plant,
    IEesHubNotifier notifier,
    NexaOne.Infrastructure.Persistence.IOutboxRepository outbox,
    NexaOne.API.Services.RealtimeNotificationCoordinator notifyCoordinator,
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
    [ProducesResponseType<PlantStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetPlantState()
        => Ok(new PlantStateResponse(
            plant.StateMachine?.Current.ToString() ?? "Uninitialized",
            plant.OperationMode.ToString(),
            plant.Machines.Count,
            plant.Machines.Select(m => new MachineStateView(m.Name, m.State.ToString())).ToList()));

    // 설비 lifecycle 변경(기동/정지/비상정지)은 안전 영향 동작 — 역할 인가로 일반 인증 사용자 차단(§9.3)
    [HttpPost("equipment/start")]
    [Authorize(Policy = "perm:fdc:control")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN/OPERATOR 기본 매핑 보유)
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
        catch (InvalidOperationException ex) { return BadRequest(new Error("Fdc.ControlFailed", ex.Message, ErrorType.Failure)); }
    }

    [HttpPost("equipment/stop")]
    [Authorize(Policy = "perm:fdc:control")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN/OPERATOR 기본 매핑 보유)
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StopAll(CancellationToken ct)
    {
        try
        {
            if (plant.StateMachine is null) await plant.InitializeAsync(ct);
            await plant.StopAsync(ct);
            await PublishEquipmentStateAsync("Stopped", ct);
            return Ok();
        }
        catch (InvalidOperationException ex) { return BadRequest(new Error("Fdc.ControlFailed", ex.Message, ErrorType.Failure)); }
    }

    [HttpPost("equipment/abort")]
    [Authorize(Policy = "perm:fdc:control")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN/OPERATOR 기본 매핑 보유)
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AbortAll([FromBody] AbortRequest req, CancellationToken ct)
    {
        await plant.AbortAsync(req.Reason, ct);
        await PublishEquipmentStateAsync("Aborted", ct);
        return Ok();
    }

    [HttpGet("interlock-rules")]
    [ProducesResponseType<IReadOnlyList<FdcInterlockRule>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetInterlockRules([FromQuery] string? equipmentId, CancellationToken ct)
    {
        var rules = await interlockService.GetRulesAsync(equipmentId ?? string.Empty, ct);
        return Ok(rules);
    }

    [HttpGet("interlock-history")]
    [ProducesResponseType<IReadOnlyList<FdcInterlockHistory>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
    [Authorize(Policy = "perm:fdc:manage")]   // ADR-003 — 인터록 규칙 구성은 운영 제어보다 높은 구성 권한
    [ProducesResponseType<FdcInterlockRule>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateRule([FromBody] CreateRuleRequest req, CancellationToken ct)
    {
        var result = await interlockService.CreateRuleAsync(
            req.RuleId, req.RuleName, req.EquipmentId, req.ParameterId,
            req.Operator, req.ThresholdValue, req.Action, req.Priority, ct);
        return result.ToActionResult();
    }

    [HttpPost("interlock/evaluate")]
    [ProducesResponseType<InterlockResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Evaluate([FromBody] EvaluateRequest req, CancellationToken ct)
    {
        var result = await interlockService.EvaluateAsync(req.EquipmentId, req.ParameterId, req.Value, ct);
        return Ok(result);
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    [HttpGet("parameters")]
    [ProducesResponseType<IReadOnlyList<FdcParameter>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetParameters([FromQuery] string? equipmentId, CancellationToken ct)
    {
        var list = await dataService.GetParametersAsync(equipmentId ?? string.Empty, ct);
        return Ok(list);
    }

    [HttpPost("parameters")]
    [Authorize(Policy = "perm:fdc:manage")]
    [ProducesResponseType<FdcParameter>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateParameter([FromBody] CreateParameterRequest req, CancellationToken ct)
    {
        var result = await dataService.CreateParameterAsync(
            req.ParameterId, req.ParameterName, req.EquipmentId, req.Unit,
            req.LowerLimit, req.UpperLimit, ct);
        return result.ToActionResult();
    }

    /// <summary>파라미터를 그룹에 배정/해제한다(GroupId=null이면 해제).</summary>
    [HttpPut("parameters/{parameterId}/group")]
    [Authorize(Policy = "perm:fdc:manage")]
    [ProducesResponseType(StatusCodes.Status200OK)]              // ToActionResult(useNoContent:false) → 본문 없는 Ok()
    [ProducesResponseType(StatusCodes.Status404NotFound)]        // 파라미터 미존재 시 NotFound(Error)
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AssignParameterGroup(
        string parameterId, [FromBody] AssignParameterGroupRequest req, CancellationToken ct)
    {
        var result = await dataService.AssignParameterToGroupAsync(parameterId, req.GroupId, ct);
        return result.ToActionResult(useNoContent: false);
    }

    // ── Parameter Groups ──────────────────────────────────────────────────────

    [HttpGet("parameter-groups")]
    [ProducesResponseType<IReadOnlyList<FdcParameterGroup>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetParameterGroups([FromQuery] string equipmentId, CancellationToken ct)
    {
        var groups = await groupService.GetGroupsAsync(equipmentId, ct);
        return Ok(groups);
    }

    [HttpPost("parameter-groups")]
    [Authorize(Policy = "perm:fdc:manage")]
    [ProducesResponseType<FdcParameterGroup>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateParameterGroup([FromBody] CreateParameterGroupRequest req, CancellationToken ct)
    {
        var result = await groupService.CreateGroupAsync(
            req.GroupId, req.GroupName, req.EquipmentId, req.Description, req.DisplayOrder, ct);
        return result.ToActionResult();
    }

    // ── Alarms (FDC threshold) ────────────────────────────────────────────────

    [HttpGet("alarm-configs")]
    [ProducesResponseType<IReadOnlyList<FdcAlarmConfig>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAlarmConfigs([FromQuery] string equipmentId, CancellationToken ct)
    {
        var configs = await fdcAlarmService.GetConfigsAsync(equipmentId, ct);
        return Ok(configs);
    }

    [HttpPost("alarm-configs")]
    [Authorize(Policy = "perm:fdc:manage")]
    [ProducesResponseType<FdcAlarmConfig>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateAlarmConfig([FromBody] CreateAlarmConfigRequest req, CancellationToken ct)
    {
        var result = await fdcAlarmService.CreateConfigAsync(
            req.AlarmConfigId, req.EquipmentId, req.ParameterId, req.AlarmLevel, req.Operator, req.Threshold, ct);
        return result.ToActionResult();
    }

    [HttpGet("alarm-history")]
    [ProducesResponseType<IReadOnlyList<FdcAlarmHistory>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
    [ProducesResponseType<IReadOnlyList<FdcCollectData>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCollectData(
        [FromQuery] string parameterId,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] int limit = 1000,
        CancellationToken ct = default)
    {
        // 음수/0/과대값 방어 — 무검증 limit이 TOP/LIMIT 쿼리로 직행해 무제한 시계열을 끌어오지 않도록 클램프한다
        // (GetLatestCollectData와 동일 의도). 기간 조회는 차트 단위 다행 수신이 정상이므로 상한을 5000으로 둔다.
        limit = Math.Clamp(limit, 1, 5000);
        var list = await dataService.GetCollectDataAsync(parameterId, from, to, limit, ct);
        return Ok(list);
    }

    [HttpGet("collect-data/latest")]
    [ProducesResponseType<IReadOnlyList<FdcCollectData>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
    [Authorize(Policy = "perm:fdc:control")]   // 운영 데이터 수집/인터록 평가 — 설비 제어 권한(OPERATOR 보유)
    [ProducesResponseType<RecordDataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
                // 미등록 설비는 EST_EQUIPMENT_ALARM.EQUIPMENT_ID→MDM_EQUIPMENT FK 위반으로 운영(FK ON)에서
                // 500을 유발한다. 인터록 '발동 시에만'(저빈도) 설비 존재를 확인하고, 미등록이면 자동 알람 기록을
                // 생략한다(수집은 계속 200). 미등록 설비 알람은 활성목록 GET이 MDM 조인이라 어차피 표시되지
                // 않으므로 생략이 안전하며, 고빈도 수집 경로(EvaluateAsync 이전)에는 추가 조회 비용이 없다.
                var equipment = await equipmentService.GetEquipmentAsync(req.EquipmentId, ct);
                if (equipment.IsFailure)
                {
                    logger.LogWarning(
                        "인터록 발동({Action})이나 설비 {EquipmentId}가 MDM 미등록 — EST 자동 알람 기록 생략(FK 위반 500 방지)",
                        interlock.Action, req.EquipmentId);
                }
                else
                {
                    var alarmId = $"FDC-{req.CollectId}";
                    var alarmCode = $"INTERLOCK_{interlock.Action}";
                    var alarmName = $"[FDC] {interlock.Message}";
                    var level = interlock.Action == "STOP" ? "CRITICAL" : "WARNING";

                    var alarmResult = await alarmService.RecordAlarmAsync(
                        alarmId, req.EquipmentId, alarmCode, alarmName, level, ct);

                    // 버스 활성 시 EquipmentAlarmRaised 이벤트를 구독자가 알람 갱신으로 전달 — 직접호출 생략(이중 발행 방지).
                    // (FDC 수집 데이터·인터록 발동 알림은 대응 이벤트가 없어 위에서 항상 직접 발행한다.)
                    if (alarmResult.IsSuccess && !notifyCoordinator.BusDeliversEvents)
                        await notifier.NotifyAlarmUpdatedAsync(ct);
                }
            }
        }

        return Ok(new RecordDataResponse(collected, interlock));
    }
}

public record CreateRuleRequest(
    string RuleId, string RuleName, string EquipmentId, string ParameterId,
    string Operator, decimal ThresholdValue, string Action, int Priority);

// 응답 DTO(SPA 타입드 클라이언트) — 기존 익명 반환을 명명 타입으로 대체(JSON 형태 동일, camelCase).
public record PlantStateResponse(
    string State, string OperationMode, int MachineCount, IReadOnlyList<MachineStateView> Machines);
public record MachineStateView(string Name, string State);
public record RecordDataResponse(FdcCollectData CollectedData, InterlockResult Interlock);

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
