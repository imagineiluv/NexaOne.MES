using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>파일 기반 명명 쿼리 게이트웨이(하이브리드 데이터 경로). 사전 등록 쿼리 ID만 실행 — 원시 SQL 노출 없음.
/// 읽기는 /query, 쓰기는 /command(requiredPermission 집행 + @currentUser/@utcNow 서버 주입). RuleController와 동일 의미.</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed partial class QueryGatewayController : ControllerBase
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _queryRegistry;

    public QueryGatewayController(IRuleDispatcher dispatcher, IQueryRegistry queryRegistry)
    {
        _dispatcher = dispatcher;
        _queryRegistry = queryRegistry;
    }

    [HttpPost("query/{queryId}")]
    [ProducesResponseType<IReadOnlyList<Dictionary<string, object>>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteQuery(
        [FromRoute] string queryId, [FromBody] Dictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var def) || def is null)
            return NotFound(new Error("QUERY_NOT_FOUND", $"Query '{queryId}' is not registered.", ErrorType.NotFound));
        if (def.IsWrite)
            return BadRequest(new Error("WRITE_QUERY_VIA_QUERY", $"Query '{queryId}' is a write query. Use POST /api/v1/command/{queryId}.", ErrorType.Validation));
        if (!string.IsNullOrEmpty(def.RequiredPermission) && !User.HasPermission(def.RequiredPermission))
            return Forbid();

        var p = BuildParameters(def.Sql, parameters, injectAudit: false);
        var rows = await _dispatcher.QueryAsync(def.Sql, p, ct);
        return Ok(rows);
    }

    [HttpPost("command/{queryId}")]
    [ProducesResponseType<AffectedRowsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteCommand(
        [FromRoute] string queryId, [FromBody] Dictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var def) || def is null)
            return NotFound(new Error("QUERY_NOT_FOUND", $"Query '{queryId}' is not registered.", ErrorType.NotFound));
        if (!def.IsWrite)
            return BadRequest(new Error("READ_QUERY_VIA_COMMAND", $"Query '{queryId}' is a read query. Use POST /api/v1/query/{queryId}.", ErrorType.Validation));
        if (!string.IsNullOrEmpty(def.RequiredPermission) && !User.HasPermission(def.RequiredPermission))
            return Forbid();

        var p = BuildParameters(def.Sql, parameters, injectAudit: true);
        var affected = await _dispatcher.ExecuteAsync(def.Sql, p, ct);
        return Ok(new AffectedRowsResponse(affected));
    }

    private Dictionary<string, object> BuildParameters(string sql, IReadOnlyDictionary<string, object>? parameters, bool injectAudit)
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

    private string CurrentUserId => User.CurrentUserId() ?? "SYSTEM";

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
}

/// <summary>쓰기 게이트웨이 영향 행 수 응답(RuleController의 AffectedRowsResponse와 동일 형태).</summary>
public sealed record AffectedRowsResponse(int Affected);
