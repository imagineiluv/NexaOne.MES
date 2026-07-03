using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>배치 수동 실행(V066/V068 실행 엔진) — 관리 화면에서 정의를 즉시 실행한다(워커 주기와 무관).
/// 실행은 BatchProcessRunner 단일 경로(BATCH_RULE=명명 쓰기쿼리 검증, 감사 주입, SAVE_HISTORY 기록).
/// 게이트웨이식(무-브리지, plugin 무관) — modules OFF에서도 동작한다.</summary>
[ApiController]
[Route("api/v1/sys/admin/batch")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SysBatchController : ControllerBase
{
    private readonly BatchProcessRunner _runner;
    public SysBatchController(BatchProcessRunner runner) => _runner = runner;

    [HttpPost("{batchId}/run")]
    [ProducesResponseType<BatchRunResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> Run(string batchId, CancellationToken ct)
    {
        var result = await _runner.RunAsync(batchId, User.CurrentUserId() ?? "SYSTEM", ct);
        return result is null
            ? NotFound(new Error("BATCH_NOT_FOUND", $"배치 정의 '{batchId}'를 찾을 수 없습니다.", ErrorType.NotFound))
            : Ok(result);   // 실행 실패도 200 + Success=false(정의는 존재, 실행 결과 보고) — 이력과 동일 의미.
    }
}
