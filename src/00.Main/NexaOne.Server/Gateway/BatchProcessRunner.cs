using System.Text.Json;
using System.Text.RegularExpressions;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;

namespace NexaOne.Server.Gateway;

/// <summary>배치 실행 엔진(V066/V068 후속) — BATCH_RULE = 레지스트리에 등록된 '명명 쓰기쿼리 ID'로 정의하고,
/// BATCH_INPUTDATA(JSON 객체)를 파라미터로 실행한다(저코드 아키텍처 정합: 배치 = 예약된 커맨드 실행).
/// 감사 토큰(@currentUser/@utcNow)은 게이트웨이와 동일하게 서버 주입(실행 주체/UTC), 미바인딩 @param은 DBNull.
/// SAVE_HISTORY=1이면 성공/실패 모두 SYS_BATCH_PROCESS_HISTORY에 기록한다. 수동 실행(관리 API)과
/// 워커(주기 실행)가 공유하는 단일 실행 경로 — 검증/기록 규칙이 갈라지지 않는다.</summary>
public sealed class BatchProcessRunner
{
    private static readonly Regex ParamToken = new(@"@([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _registry;
    private readonly ILogger<BatchProcessRunner> _logger;
    private readonly IServiceProvider _services;

    public BatchProcessRunner(
        IRuleDispatcher dispatcher, IQueryRegistry registry, ILogger<BatchProcessRunner> logger,
        IServiceProvider services)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _logger = logger;
        _services = services;
    }

    /// <summary>유효(Valid) 배치 정의 전체 — 워커 스케줄링/수동 실행 조회 공용(SYS.BatchProcessList 단일 출처).</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListDefinitionsAsync(CancellationToken ct)
        => await _dispatcher.QueryAsync(Sql("SYS.BatchProcessList"), new Dictionary<string, object>(), ct);

    public async Task<BatchRunResult?> RunAsync(string batchId, string executedBy, CancellationToken ct)
    {
        var def = (await ListDefinitionsAsync(ct))
            .FirstOrDefault(d => string.Equals(Col(d, "BATCH_ID"), batchId, StringComparison.OrdinalIgnoreCase));
        if (def is null) return null;   // 미존재/Invalid — 컨트롤러가 404로 매핑

        var startedAt = DateTime.UtcNow;
        var result = await ExecuteRuleAsync(Col(def, "BATCH_RULE"), Col(def, "BATCH_INPUTDATA"), executedBy, ct);

        if (IsTruthy(def.GetValueOrDefault("SAVE_HISTORY")))
            await SaveHistoryAsync(batchId, startedAt, result, executedBy, ct);

        if (!result.Success)
            _logger.LogWarning("배치 실행 실패 — batchId={BatchId}, rule={Rule}: {Error}",
                batchId, Col(def, "BATCH_RULE"), result.Error);
        return result;
    }

    private async Task<BatchRunResult> ExecuteRuleAsync(string rule, string inputData, string executedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return BatchRunResult.Failed("BATCH_RULE이 비어 있습니다 — 실행할 명명 쓰기쿼리 ID를 지정하세요.");
        // 브리지 룰(v2, MRP 스펙 백로그 ④) — 'bridge:{키}'는 명명쿼리가 아니라 등록된 모듈 브리지 호출.
        // 모듈 OFF(게이트웨이 전용 부팅)면 해당 브리지가 DI에 없어 실패로 보고한다(무음 스킵 금지).
        if (rule.Trim().StartsWith("bridge:", StringComparison.OrdinalIgnoreCase))
            return await ExecuteBridgeRuleAsync(rule.Trim()["bridge:".Length..].Trim(), executedBy, ct);
        if (!_registry.TryGet(rule.Trim(), out var def) || def is null)
            return BatchRunResult.Failed($"BATCH_RULE '{rule}'이(가) 쿼리 레지스트리에 없습니다.");
        if (!def.IsWrite)
            return BatchRunResult.Failed($"BATCH_RULE '{rule}'은(는) 조회 쿼리입니다 — 배치는 쓰기(command)만 실행합니다.");

        try
        {
            var p = ParseInputData(inputData);
            // 게이트웨이 command와 동일 규약 — 감사 토큰 서버 주입 + 미바인딩 @param은 DBNull.
            p["currentUser"] = executedBy;
            p["utcNow"] = DateTime.UtcNow;
            foreach (Match m in ParamToken.Matches(def.Sql))
                if (!p.ContainsKey(m.Groups[1].Value)) p[m.Groups[1].Value] = DBNull.Value;

            var affected = await _dispatcher.ExecuteAsync(def.Sql, p, ct);
            return BatchRunResult.Ok(affected);
        }
        catch (JsonException je)
        {
            return BatchRunResult.Failed($"BATCH_INPUTDATA JSON 파싱 실패: {je.Message}");
        }
        catch (Exception ex)
        {
            return BatchRunResult.Failed(ex.Message);
        }
    }

    // 브리지 룰 디스패치 — 지원 키만 명시(임의 리플렉션 호출 금지). 새 브리지 배치는 여기에 case 추가.
    private async Task<BatchRunResult> ExecuteBridgeRuleAsync(string key, string executedBy, CancellationToken ct)
    {
        try
        {
            switch (key.ToLowerInvariant())
            {
                case "pom.mrp.run":
                {
                    var bridge = _services.GetService<NexaOne.ServiceContracts.Pom.IMrpBridge>();
                    if (bridge is null)
                        return BatchRunResult.Failed("IMrpBridge 미등록 — 모듈 OFF 부팅에선 브리지 배치를 실행할 수 없습니다.");
                    var r = await bridge.RunAsync(executedBy, ct);
                    return r.Status == "Success"
                        ? BatchRunResult.Ok(r.PlannedOrderCount)
                        : BatchRunResult.Failed(r.Message ?? "MRP 실행 실패");
                }
                default:
                    return BatchRunResult.Failed($"알 수 없는 브리지 룰 '{key}' — 지원: pom.mrp.run");
            }
        }
        catch (Exception ex)
        {
            return BatchRunResult.Failed(ex.Message);
        }
    }

    private async Task SaveHistoryAsync(
        string batchId, DateTime startedAt, BatchRunResult result, string executedBy, CancellationToken ct)
    {
        try
        {
            await _dispatcher.ExecuteAsync(Sql("SYS.InsertBatchProcessHistory"), new Dictionary<string, object>
            {
                ["historyId"] = Guid.NewGuid().ToString("N"),
                ["batchId"] = batchId,
                ["startedAt"] = startedAt,
                ["finishedAt"] = DateTime.UtcNow,
                ["success"] = result.Success,
                ["affected"] = result.Affected,
                ["errorMessage"] = (object?)Truncate(result.Error, 1000) ?? DBNull.Value,
                ["currentUser"] = executedBy,
            }, ct);
        }
        catch (Exception ex)
        {
            // 이력 기록 실패가 배치 결과 자체를 뒤집지 않는다 — 실행은 이미 끝났고 결과는 호출자에 보고된다.
            _logger.LogWarning(ex, "배치 이력 기록 실패 — batchId={BatchId}", batchId);
        }
    }

    private string Sql(string queryId)
        => _registry.TryGet(queryId, out var def) && def is not null
            ? def.Sql
            : throw new InvalidOperationException($"명명 쿼리 '{queryId}'가 레지스트리에 없습니다.");

    // BATCH_INPUTDATA(JSON 객체) → 파라미터 dict. 빈 값은 빈 dict. 문자/숫자/불리언/널만 지원(중첩은 문자열화).
    private static Dictionary<string, object> ParseInputData(string? inputData)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(inputData)) return p;
        using var doc = JsonDocument.Parse(inputData);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("최상위는 JSON 객체여야 합니다.");
        foreach (var prop in doc.RootElement.EnumerateObject())
            p[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? (object)DBNull.Value,
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => DBNull.Value,
                _ => prop.Value.GetRawText(),
            };
        return p;
    }

    private static string Col(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    private static bool IsTruthy(object? v) => v switch
    {
        null or DBNull => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        _ => v.ToString() is "1" or "True" or "true",
    };

    private static string? Truncate(string? s, int max)
        => s is null ? null : (s.Length <= max ? s : s[..max]);
}

/// <summary>배치 1회 실행 결과 — 수동 실행 응답/이력 기록 공용.</summary>
public sealed record BatchRunResult(bool Success, int Affected, string? Error)
{
    public static BatchRunResult Ok(int affected) => new(true, affected, null);
    public static BatchRunResult Failed(string error) => new(false, 0, error);
}
