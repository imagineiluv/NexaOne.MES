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
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;

    public QueryGatewayController(IRuleDispatcher dispatcher, IQueryRegistry queryRegistry,
        Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _dispatcher = dispatcher;
        _queryRegistry = queryRegistry;
        _config = config;
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
        if (!CanExecute(def))
            return Forbid();

        // @currentUser/@utcNow는 read에도 서버 주입 — 개인화 read(FDC.UserParameterList 등)가 범용
        // /query로 동작하고, 클라이언트가 보낸 currentUser 값은 덮어써 타인 데이터 스푸핑을 차단한다.
        var p = BuildParameters(def.Sql, parameters, injectAudit: true);
        var rows = await _dispatcher.QueryAsync(def.Sql, p, ct);
        return Ok(rows);
    }

    /// <summary>제네릭 서버 페이징(read 전용) — 등록 쿼리를 방언별 페이징 절로 감싸 {total, rows}를 반환한다.
    /// 쿼리 원문 무수정(PagedSqlBuilder). 자체 상한 보유 쿼리는 422 — 클라이언트(MetaScreen)가 전량 경로로 폴백.</summary>
    [HttpPost("query/{queryId}/paged")]
    [ProducesResponseType<PagedQueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExecuteQueryPaged(
        [FromRoute] string queryId, [FromBody] PagedQueryRequest request, CancellationToken ct)
    {
        if (!_queryRegistry.TryGet(queryId, out var def) || def is null)
            return NotFound(new Error("QUERY_NOT_FOUND", $"Query '{queryId}' is not registered.", ErrorType.NotFound));
        if (def.IsWrite)
            return BadRequest(new Error("WRITE_QUERY_VIA_QUERY", $"Query '{queryId}' is a write query. Use POST /api/v1/command/{queryId}.", ErrorType.Validation));
        if (!CanExecute(def))
            return Forbid();

        var provider = _config["Database:Provider"] ?? "Sqlite";
        if (!PagedSqlBuilder.TryBuild(def.Sql, provider, out var pageSql, out var countSql))
            return UnprocessableEntity(new Error("QUERY_NOT_PAGEABLE",
                $"Query '{queryId}' declares its own limit clause and cannot be server-paged.", ErrorType.Validation));

        var p = BuildParameters(def.Sql, request.Parameters, injectAudit: true);   // read 주입 — 위 /query와 동일 근거
        var countRows = await _dispatcher.QueryAsync(countSql, p, ct);
        var first = countRows.FirstOrDefault()?.Values.FirstOrDefault();
        var total = first switch { int i => i, long l => (int)l, _ => int.TryParse(first?.ToString(), out var n) ? n : 0 };

        var paged = new Dictionary<string, object>(p, StringComparer.Ordinal)
        {
            ["__limit"] = Math.Clamp(request.Limit, 1, 500),
            ["__offset"] = Math.Max(0, request.Offset),
        };
        var rows = await _dispatcher.QueryAsync(pageSql, paged, ct);
        return Ok(new PagedQueryResponse(total, rows));
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
        if (!CanExecute(def))
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
            // SQL이 토큰을 참조할 때만 주입 — Microsoft.Data.Sqlite는 문장에 없는 파라미터를 예외로 던진다.
            // 참조 시엔 클라이언트 제공값을 '덮어써' 개인화 스코프 스푸핑을 차단한다(read/write 공통).
            if (def_sql_has(sql, "currentUser")) p["currentUser"] = CurrentUserId;
            if (def_sql_has(sql, "utcNow")) p["utcNow"] = DateTime.UtcNow;

            static bool def_sql_has(string s, string token) => s.Contains("@" + token, StringComparison.Ordinal);
        }
        foreach (Match m in ParamToken().Matches(sql))
            if (!p.ContainsKey(m.Groups[1].Value)) p[m.Groups[1].Value] = DBNull.Value;
        return p;
    }

    private string CurrentUserId => User.CurrentUserId() ?? "SYSTEM";

    private bool CanExecute(QueryDefinition definition) =>
        definition.IsPublic
            ? !definition.IsWrite
            : !string.IsNullOrEmpty(definition.RequiredPermission)
              && User.HasPermission(definition.RequiredPermission);

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

/// <summary>제네릭 서버 페이징 요청 — 검색 파라미터 + 페이지 창(limit 1~500 클램프).</summary>
public sealed record PagedQueryRequest(Dictionary<string, object>? Parameters, int Limit, int Offset);

/// <summary>제네릭 서버 페이징 응답 — 총건수(페이저용) + 현재 페이지 행.</summary>
public sealed record PagedQueryResponse(int Total, IReadOnlyList<Dictionary<string, object?>> Rows);
