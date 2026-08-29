using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 수동/연동 설비 출력 기록 경계. HTTP 요청의 작업자는 payload를 신뢰하지 않고 JWT에서 덮어쓴다.
/// PLC/FDC 플러그인은 같은 bridge를 직접 호출하되 고유 source event와 idempotency key를 제공한다.
/// </summary>
[ApiController]
[Route("api/v1/est/equipment-output")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class EquipmentOutputController : ControllerBase
{
    private readonly IEquipmentOutputBridge _bridge;

    public EquipmentOutputController(IEquipmentOutputBridge bridge) => _bridge = bridge;

    [HttpPost]
    [RequirePermission(Permissions.EstManage)]
    [ProducesResponseType<EquipmentOutputDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Record([FromBody] EquipmentOutputCommand command, CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();

        var result = await _bridge.RecordAsync(command with { ActorId = actor }, ct);
        return result.ToActionResult();
    }
}
