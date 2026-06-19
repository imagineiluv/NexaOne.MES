using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Controllers.Models;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public partial class RuleController : ControllerBase
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _queryRegistry;

    public RuleController(IRuleDispatcher dispatcher, IQueryRegistry queryRegistry)
    {
        _dispatcher = dispatcher;
        _queryRegistry = queryRegistry;
    }

    [HttpPost("rule/{ruleName}")]
    [ProducesResponseType<RuleResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExecuteRule(
        [FromRoute] string ruleName,
        [FromBody] RuleRequest request)
    {
        request.Head.RuleName = ruleName;
        var result = await _dispatcher.DispatchAsync(ruleName, request.Body ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    // 파일 기반 쿼리 레지스트리 실행(UI 연동) — 사전 등록된 쿼리 ID만 실행하므로 원시 SQL 노출이 없다.
    // 따라서 query(원시 SQL, sys:manage)와 달리 인증 사용자면 실행 가능하다. 파라미터는 @바인딩.
    [HttpPost("query/{queryId}")]
    [ProducesResponseType<IReadOnlyList<Dictionary<string, object>>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteRegisteredQuery(
        [FromRoute] string queryId,
        [FromBody] Dictionary<string, object>? parameters,
        CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var def) || def is null)
            return NotFound(new { code = "QUERY_NOT_FOUND", message = $"Query '{queryId}' is not registered." });

        // 쓰기 쿼리는 읽기 게이트웨이로 실행할 수 없다(SELECT 전용) — /command/{id}를 쓰도록 거부.
        if (def.IsWrite)
            return BadRequest(new { code = "WRITE_QUERY_VIA_QUERY", message = $"Query '{queryId}' is a write query. Use POST /api/v1/command/{queryId}." });

        // 쿼리가 필요 권한을 선언했으면 토큰 권한 클레임(또는 전체권한 "*")으로 집행한다(데이터 주도 인가).
        if (!string.IsNullOrEmpty(def.RequiredPermission) && !HasPermission(def.RequiredPermission))
            return Forbid();

        var p = BuildParameters(def.Sql, parameters, injectAudit: false);
        var rows = await _dispatcher.QueryAsync(def.Sql, p, ct);
        return Ok(rows);
    }

    // 파일 기반 쓰기 쿼리 실행(UI 폼 저장 등) — 사전 등록된 쓰기 쿼리 ID만 실행하므로 원시 SQL 노출이 없다.
    // 감사 컬럼은 @currentUser(토큰)·@utcNow(UTC)로 게이트웨이가 주입한다(방언 무관 바운드 파라미터).
    [HttpPost("command/{queryId}")]
    [ProducesResponseType<AffectedRowsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteRegisteredCommand(
        [FromRoute] string queryId,
        [FromBody] Dictionary<string, object>? parameters,
        CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var def) || def is null)
            return NotFound(new { code = "QUERY_NOT_FOUND", message = $"Query '{queryId}' is not registered." });

        // 읽기 쿼리는 command 게이트웨이로 실행할 수 없다 — 의도치 않은 종류 혼동을 차단(/query/{id}를 쓰도록).
        if (!def.IsWrite)
            return BadRequest(new { code = "READ_QUERY_VIA_COMMAND", message = $"Query '{queryId}' is a read query. Use POST /api/v1/query/{queryId}." });

        if (!string.IsNullOrEmpty(def.RequiredPermission) && !HasPermission(def.RequiredPermission))
            return Forbid();

        var p = BuildParameters(def.Sql, parameters, injectAudit: true);
        var affected = await _dispatcher.ExecuteAsync(def.Sql, p, ct);
        return Ok(new AffectedRowsResponse(affected));
    }

    // 본문 JSON(JsonElement) → Dapper 바인딩용 CLR 값으로 변환하고, SQL이 참조하는 @파라미터 중 본문에 없는
    // 것은 DBNull로 채운다(선택 필터 누락 방지; 등록 쿼리는 신뢰된 파일이라 토큰 스캔이 안전). 쓰기 경로는
    // 감사 파라미터(@currentUser/@utcNow)를 주입한다 — 본문이 같은 키를 보내도 서버 값으로 덮어쓴다(위변조 방지).
    private Dictionary<string, object> BuildParameters(
        string sql, IReadOnlyDictionary<string, object>? parameters, bool injectAudit)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal);
        if (parameters is not null)
            foreach (var (k, v) in parameters)
                p[k] = JsonToClr(v) ?? (object)DBNull.Value;

        if (injectAudit)
        {
            p["currentUser"] = CurrentUserId;
            p["utcNow"] = DateTime.UtcNow;
        }

        foreach (Match m in ParamToken().Matches(sql))
            if (!p.ContainsKey(m.Groups[1].Value)) p[m.Groups[1].Value] = DBNull.Value;

        return p;
    }

    // 감사 컬럼 주입용 현재 사용자(토큰 sub). 다른 컨트롤러(LotController/DeployController)와 동일 규약.
    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.Identity?.Name ?? "SYSTEM";

    // 데이터 주도 권한 검사 — 토큰의 permission 클레임에 요구 권한 또는 전체권한("*")이 있으면 통과.
    private bool HasPermission(string permission) =>
        User.FindAll(NexaOne.Common.Security.Permissions.ClaimType)
            .Any(c => c.Value == NexaOne.Common.Security.Permissions.All
                   || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    // JSON 본문 값(JsonElement) → Dapper 바인딩용 CLR 값. null/Null은 null(상위에서 DBNull로 치환).
    private static object? JsonToClr(object? value) => value switch
    {
        System.Text.Json.JsonElement je => je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => je.GetString(),
            System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => je.ToString(),
        },
        _ => value,
    };

    [GeneratedRegex(@"@(\w+)")]
    private static partial Regex ParamToken();

    // 임의 SQL/프로시저 실행은 관리자 전용으로 제한(인증만으로 통과하던 권한상승/RCE급 경로 봉쇄).
    [Authorize(Policy = "perm:sys:manage")]
    [HttpPost("query")]
    [ProducesResponseType<RuleResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExecuteQuery([FromBody] QueryRequest request)
    {
        var result = await _dispatcher.QueryAsync(request.Sql, request.Parameters ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    [Authorize(Policy = "perm:sys:manage")]
    [HttpPost("procedure")]
    [ProducesResponseType<RuleResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExecuteProcedure([FromBody] ProcedureRequest request)
    {
        var result = await _dispatcher.ProcedureAsync(request.ProcedureName, request.Parameters ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    [Authorize(Policy = "perm:sys:manage")]
    [HttpPost("procedure/dataset")]
    [ProducesResponseType<RuleResponse<Dictionary<string, object>>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExecuteProcedureDataSet([FromBody] ProcedureRequest request)
    {
        var result = await _dispatcher.ProcedureToDataSetAsync(request.ProcedureName, request.Parameters ?? new());
        return Ok(new RuleResponse<Dictionary<string, object>> { IsSuccess = true, Data = result });
    }
}
