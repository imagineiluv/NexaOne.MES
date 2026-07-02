using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 QMS 품질 엔드포인트(ADR-008 얇은 브리지) — plugin-ALC QmsService를 IQmsBridge로 호출한다.
/// 단순 CRUD/조회는 게이트웨이(QMS.xml)가 커버하므로, 여기는 상태/불변식 연산만: 부적합 확정·SPC 관리한계 갱신.
/// 쓰기는 qms:manage 수동 검사. Result→HTTP(BridgeResultExtensions). (modules ON에서만 동작.)</summary>
[ApiController]
[Route("api/v1/qms")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class QmsBridgeController : ControllerBase
{
    private readonly IQmsBridge _bridge;
    public QmsBridgeController(IQmsBridge bridge) => _bridge = bridge;

    [HttpGet("defects")]
    [ProducesResponseType<IReadOnlyList<DefectDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDefects([FromQuery] string lotId, CancellationToken ct)
        => Ok(await _bridge.GetDefectsByLotAsync(lotId, ct));

    [HttpPost("defects/{defectId}/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> ConfirmDefect(string defectId, [FromBody] ConfirmDefectRequest req, CancellationToken ct)
    {
        return (await _bridge.ConfirmDefectAsync(defectId, req.ConfirmerId, ct)).ToActionResult();
    }

    [HttpPost("spc-params/{paramId}/control-limits")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> UpdateControlLimits(string paramId, [FromBody] UpdateControlLimitsRequest req, CancellationToken ct)
    {
        return (await _bridge.UpdateControlLimitsAsync(paramId, req.Mean, req.Ucl, req.Lcl, ct)).ToActionResult();
    }

}

public record ConfirmDefectRequest(string ConfirmerId);
public record UpdateControlLimitsRequest(decimal Mean, decimal Ucl, decimal Lcl);
