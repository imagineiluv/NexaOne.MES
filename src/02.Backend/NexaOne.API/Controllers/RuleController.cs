using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
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
    public async Task<IActionResult> ExecuteRegisteredQuery(
        [FromRoute] string queryId,
        [FromBody] Dictionary<string, object>? parameters,
        CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var sql))
            return NotFound(new { code = "QUERY_NOT_FOUND", message = $"Query '{queryId}' is not registered." });

        // 본문 JSON 값은 JsonElement로 역직렬화되므로 Dapper 바인딩 가능한 CLR 타입으로 변환한다.
        var p = new Dictionary<string, object>(StringComparer.Ordinal);
        if (parameters is not null)
            foreach (var (k, v) in parameters)
                p[k] = JsonToClr(v) ?? (object)DBNull.Value;

        // SQL이 참조하는 @파라미터 중 본문에 없는 것은 DBNull로 채운다 — 선택 필터((@p IS NULL OR ...))가
        // 파라미터 누락으로 실패하지 않도록 한다(등록 쿼리는 신뢰된 파일이라 토큰 스캔이 안전하다).
        foreach (Match m in ParamToken().Matches(sql))
            if (!p.ContainsKey(m.Groups[1].Value)) p[m.Groups[1].Value] = DBNull.Value;

        var rows = await _dispatcher.QueryAsync(sql, p, ct);
        return Ok(rows);
    }

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
    public async Task<IActionResult> ExecuteQuery([FromBody] QueryRequest request)
    {
        var result = await _dispatcher.QueryAsync(request.Sql, request.Parameters ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    [Authorize(Policy = "perm:sys:manage")]
    [HttpPost("procedure")]
    public async Task<IActionResult> ExecuteProcedure([FromBody] ProcedureRequest request)
    {
        var result = await _dispatcher.ProcedureAsync(request.ProcedureName, request.Parameters ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    [Authorize(Policy = "perm:sys:manage")]
    [HttpPost("procedure/dataset")]
    public async Task<IActionResult> ExecuteProcedureDataSet([FromBody] ProcedureRequest request)
    {
        var result = await _dispatcher.ProcedureToDataSetAsync(request.ProcedureName, request.Parameters ?? new());
        return Ok(new RuleResponse<Dictionary<string, object>> { IsSuccess = true, Data = result });
    }
}
