using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 OEE 수동 집계 엔드포인트(ADR-008 얇은 브리지). plugin-ALC OEE 집계(IOeeAggregator)를
/// IOeeAggregationBridge로 호출한다. 운영자가 워커(기본 OFF)를 기다리지 않고 특정 일자/윈도를 즉시 재집계한다.
/// 파생 마트(EST_OEE_SUMMARY/LOSS)의 워커 산출물(AGG_/AGL_)만 delete+insert하므로 멱등하고 원자료는 건드리지 않는다.
/// 쓰기 성격이라 est:manage 권한을 요구한다. (modules ON에서만 IOeeAggregationBridge가 등록되므로 동작한다.)</summary>
[ApiController]
[Route("api/v1/oee")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class OeeAggregationController : ControllerBase
{
    private readonly IOeeAggregationBridge _bridge;

    public OeeAggregationController(IOeeAggregationBridge bridge) => _bridge = bridge;

    /// <summary>특정 일자(UTC)를 작업조 인식으로 재집계한다. body: { "date": "2026-07-01" }.</summary>
    [HttpPost("aggregate-day")]
    [ProducesResponseType<OeeAggregateResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> AggregateDay([FromBody] OeeAggregateDayRequest? request, CancellationToken ct)
    {
        if (request is null || request.Date == default)
            return BadRequest(Error.Validation("OEE_DATE_REQUIRED", "date 는 필수입니다."));
        var affected = await _bridge.AggregateDayAsync(request.Date, ct);
        return Ok(new OeeAggregateResult(affected));
    }

    /// <summary>임의 윈도 [from, to)를 재집계한다. shiftId/plannedMinutes 선택. body: { "from": "...", "to": "..." }.</summary>
    [HttpPost("aggregate")]
    [ProducesResponseType<OeeAggregateResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EstManage)]
    public async Task<IActionResult> AggregateWindow([FromBody] OeeAggregateWindowRequest? request, CancellationToken ct)
    {
        if (request is null || request.From == default || request.To == default || request.To <= request.From)
            return BadRequest(Error.Validation("OEE_WINDOW_INVALID", "from < to 인 유효한 윈도가 필요합니다."));
        var affected = await _bridge.AggregateWindowAsync(
            request.From, request.To, request.ShiftId, request.PlannedMinutes ?? 0m, ct);
        return Ok(new OeeAggregateResult(affected));
    }
}

public record OeeAggregateDayRequest(DateTime Date);
public record OeeAggregateWindowRequest(DateTime From, DateTime To, string? ShiftId, decimal? PlannedMinutes);
public record OeeAggregateResult(int Affected);
