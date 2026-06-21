using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 FDC 설정관리 엔드포인트(ADR-008 얇은 브리지). plugin-ALC FdcParameterGroupService/
/// FdcAlarmService/FdcInterlockService를 IFdcBridge로 호출한다. 쓰기(파라미터그룹·알람설정·인터락규칙 생성)는
/// fdc:manage 수동 검사. Result→HTTP(BridgeResultExtensions: Conflict→409·NotFound→404·Validation→400·성공→200). (modules ON에서만 동작.)
/// 순수 설정·이력 조회는 게이트웨이(/api/v1/query/FDC.*)로. 실시간 수집/평가/제어(start/stop)·OPC-UA는 워커 소유라 본 컨트롤러 비대상(ADR-006).</summary>
[ApiController]
[Route("api/v1/fdc")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class FdcBridgeController : ControllerBase
{
    private readonly IFdcBridge _bridge;
    public FdcBridgeController(IFdcBridge bridge) => _bridge = bridge;

    // ── 파라미터그룹(FdcParameterGroup) ──

    [HttpPost("parameter-groups")]
    [ProducesResponseType<FdcParameterGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateParameterGroup([FromBody] CreateFdcParameterGroupRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.FdcManage)) return Forbid();
        return (await _bridge.CreateParameterGroupAsync(
            req.GroupId, req.GroupName, req.EquipmentId, req.Description, req.DisplayOrder, ct)).ToActionResult();
    }

    // ── 임계치 알람설정(FdcAlarmConfig) ──

    [HttpPost("alarm-configs")]
    [ProducesResponseType<FdcAlarmConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAlarmConfig([FromBody] CreateFdcAlarmConfigRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.FdcManage)) return Forbid();
        return (await _bridge.CreateAlarmConfigAsync(
            req.AlarmConfigId, req.EquipmentId, req.ParameterId, req.AlarmLevel, req.Operator, req.Threshold, ct)).ToActionResult();
    }

    // ── 인터락규칙(FdcInterlockRule) ──

    [HttpPost("interlock-rules")]
    [ProducesResponseType<FdcInterlockRuleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateInterlockRule([FromBody] CreateFdcInterlockRuleRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.FdcManage)) return Forbid();
        return (await _bridge.CreateInterlockRuleAsync(
            req.RuleId, req.RuleName, req.EquipmentId, req.ParameterId, req.Operator, req.Threshold, req.Action, req.Priority, ct)).ToActionResult();
    }

}

public record CreateFdcParameterGroupRequest(
    string GroupId, string GroupName, string EquipmentId, string? Description, int DisplayOrder);
public record CreateFdcAlarmConfigRequest(
    string AlarmConfigId, string EquipmentId, string ParameterId, string AlarmLevel, string Operator, decimal Threshold);
public record CreateFdcInterlockRuleRequest(
    string RuleId, string RuleName, string EquipmentId, string ParameterId, string Operator, decimal Threshold, string Action, int Priority);
