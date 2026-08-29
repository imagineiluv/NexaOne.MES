using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 MRP v1 실행 엔드포인트(ADR-008 얇은 브리지). plugin-ALC POM의 IMrpPlanner를
/// IMrpBridge로 호출한다. 실행은 append-only(MRP_RUN/MRP_PLANNED_ORDER 런별 보존) — 원자료
/// (수주·재고·오더)는 건드리지 않아 재실행이 안전하다. 결과 조회는 명명 쿼리(POM.MrpPlannedOrderList/
/// MrpRunList). 쓰기 성격이라 pom:manage 권한을 요구한다(modules ON에서만 브리지가 등록된다).</summary>
[ApiController]
[Route("api/v1/pom/mrp")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class MrpController : ControllerBase
{
    private readonly IMrpBridge _bridge;

    public MrpController(IMrpBridge bridge) => _bridge = bridge;

    public sealed record RunRequest(int? BucketDays, int? HorizonBuckets);

    /// <summary>MRP 소요량 전개 실행 — 수요(확정 수주) 기준 gross-to-net → 계획오더 제안 생성.
    /// body 옵션(v2 3단): bucketDays/horizonBuckets 지정 시 기간 버킷(시간위상) 넷팅 — 제안이
    /// 품목×버킷 다행이 되고 DUE_DATE=버킷 시작일. 미지정=총량 넷팅(v1).</summary>
    [HttpPost("run")]
    [ProducesResponseType<MrpRunResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> Run([FromBody] RunRequest? request, CancellationToken ct)
    {
        var options = request is { BucketDays: not null } or { HorizonBuckets: not null }
            ? new MrpRunOptions(request!.BucketDays ?? 7, request.HorizonBuckets ?? 8)
            : null;
        var result = await _bridge.RunAsync(User.CurrentUserId() ?? "SYSTEM", options, ct);
        return Ok(result);
    }

    public sealed record ConvertRequest(
        string? RunId,
        IReadOnlyList<string>? PlannedOrderIds,
        IReadOnlyList<MrpProductionAssignment>? ProductionAssignments);

    /// <summary>구매 제안은 구매오더, 생산 제안은 생산계획과 생산관리지시로 전환한다.</summary>
    [HttpPost("convert")]
    [ProducesResponseType<MrpConvertResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> Convert([FromBody] ConvertRequest? request, CancellationToken ct)
    {
        var result = await _bridge.ConvertAsync(
            request?.RunId, request?.PlannedOrderIds, request?.ProductionAssignments,
            User.CurrentUserId() ?? "SYSTEM", ct);
        return Ok(result);
    }
}
