using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>사용자 개인화(§20.12 즐겨찾기/최근 메뉴 + §20.8 조건 저장/불러오기) — 게이트웨이식(무-브리지).
/// 명명 쿼리 레지스트리의 @currentUser 스코프 SQL을 토큰 사용자로 바인딩해 실행한다 — SQL은 XML 단일 출처
/// (방언 패리티 테스트 대상)고, 본 컨트롤러는 바인딩/검증/매핑만 한다. 쿼리의 requiredPermission="sys:manage"는
/// 원시 /command 직접 호출 잠금용이고, 정상 경로인 본 컨트롤러는 인증만 요구한다(자기 데이터 스코프라 안전).
/// 조건 검증 규칙('$' 예약·길이·한도)은 모듈 ConditionSettingService와 동일 상수를 미러한다 —
/// SYS_MENU_ROLE 권한 교차·모듈 서비스 승격은 메뉴-역할 관리 이식 후 과제.</summary>
[ApiController]
[Route("api/v1/sys")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SysPersonalizationController : ControllerBase
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _registry;

    public SysPersonalizationController(IRuleDispatcher dispatcher, IQueryRegistry registry)
    {
        _dispatcher = dispatcher;
        _registry = registry;
    }

    // ── 메뉴 트리(역할 필터) ──

    /// <summary>사이드바 메뉴 트리 — 미매핑 화면 메뉴=공개(하위호환), 매핑된 메뉴=사용자 역할 일치 시만,
    /// 전권('*') 역할은 전체. SYS.MenuTreeForUser가 @currentUser를 요구하므로 토큰 바인딩 경로인
    /// 이 엔드포인트가 정상 경로다(§20.12 개인화와 동일 규약).</summary>
    [HttpGet("menu-tree")]
    [ProducesResponseType<IReadOnlyList<IReadOnlyDictionary<string, object>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMenuTree(CancellationToken ct)
    {
        var rows = await QueryAsync("SYS.MenuTreeForUser", new(), ct);
        return Ok(rows);
    }

    // ── 사용자 언어(P3-14) ──

    private static readonly string[] SupportedLanguages = { "KoKr", "EnUs" };

    [HttpGet("language")]
    [ProducesResponseType<UserLanguageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLanguage(CancellationToken ct)
    {
        var rows = await QueryAsync("SYS.UserLanguage", new(), ct);
        var language = rows.Count > 0 ? Str(rows[0], "LANGUAGE") : "KoKr";
        return Ok(new UserLanguageResponse(language.Length > 0 ? language : "KoKr"));
    }

    [HttpPut("language")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetLanguage([FromBody] UserLanguageRequest req, CancellationToken ct)
    {
        var language = req.Language?.Trim() ?? "";
        if (!SupportedLanguages.Contains(language, StringComparer.Ordinal))
            return BadRequest(new Error("LANGUAGE_UNSUPPORTED", "지원하지 않는 언어입니다. (KoKr | EnUs)", ErrorType.Validation));
        await ExecuteAsync("SYS.UpdateUserLanguage", new() { ["language"] = language }, ct);
        return NoContent();
    }

    // ── 즐겨찾기 ──

    [HttpGet("favorites")]
    [ProducesResponseType<IReadOnlyList<FavoriteMenuResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFavorites(CancellationToken ct)
    {
        var rows = await QueryAsync("SYS.ListFavoriteMenus", new(), ct);
        return Ok(rows.Select(r => new FavoriteMenuResponse(
            Str(r, "MENU_ID"), Str(r, "MENU_NAME"), Str(r, "PROGRAM_ID"),
            NullableStr(r, "IMAGE_ID"), Str(r, "UI_ID"), Int(r, "DISPLAY_SEQUENCE"))).ToList());
    }

    /// <summary>추가 — 미존재/폴더/중복 메뉴는 0행(조용히 무시, §20.12 멱등 규칙). 항상 204.</summary>
    [HttpPost("favorites")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> AddFavorite([FromBody] MenuIdRequest req, CancellationToken ct)
    {
        await ExecuteAsync("SYS.AddFavoriteMenu", new() { ["menuId"] = req.MenuId?.Trim() ?? "" }, ct);
        return NoContent();
    }

    [HttpDelete("favorites")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveFavorite([FromQuery] string menuId, CancellationToken ct)
    {
        await ExecuteAsync("SYS.RemoveFavoriteMenu", new() { ["menuId"] = menuId?.Trim() ?? "" }, ct);
        return NoContent();
    }

    /// <summary>전달 순서대로 1..n 재부여 — 목록에 없는 즐겨찾기는 기존 순번 유지(표시 정렬이 결정적이라 수용).</summary>
    [HttpPut("favorites/order")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReorderFavorites([FromBody] ReorderFavoritesRequest req, CancellationToken ct)
    {
        if (req.MenuIds is null || req.MenuIds.Count == 0)
            return BadRequest(new Error("MENU_IDS_REQUIRED", "정렬할 메뉴 목록이 비어 있습니다.", ErrorType.Validation));

        var sequence = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in req.MenuIds)
        {
            var menuId = raw?.Trim() ?? "";
            if (menuId.Length == 0 || !seen.Add(menuId)) continue;
            sequence++;
            await ExecuteAsync("SYS.UpdateFavoriteMenuSequence",
                new() { ["menuId"] = menuId, ["sequence"] = sequence }, ct);
        }
        return NoContent();
    }

    // ── 최근 메뉴 ──

    [HttpGet("recent-menus")]
    [ProducesResponseType<IReadOnlyList<RecentMenuResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentMenus(CancellationToken ct)
    {
        var rows = await QueryAsync("SYS.ListRecentMenus", new(), ct);
        return Ok(rows.Select(r => new RecentMenuResponse(
            Str(r, "MENU_ID"), Str(r, "MENU_NAME"), Str(r, "PROGRAM_ID"),
            NullableStr(r, "IMAGE_ID"), Str(r, "UI_ID"), DateTimeVal(r, "LAST_USED_AT"))).ToList());
    }

    /// <summary>화면 사용 기록 — 재사용은 맨 앞으로, 10개 초과분은 즉시 정리(§20.12 RecentMenuCount=10).</summary>
    [HttpPost("recent-menus")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RecordRecentMenu([FromBody] MenuIdRequest req, CancellationToken ct)
    {
        var affected = await ExecuteAsync("SYS.RecordRecentMenu", new() { ["menuId"] = req.MenuId?.Trim() ?? "" }, ct);
        if (affected > 0)
            await ExecuteAsync("SYS.TrimRecentMenus", new(), ct);
        return NoContent();
    }

    // ── 조건 저장/불러오기(§20.8) — 규칙 단일 출처 = ServiceContracts.Sys.PersonalizationRules(모듈과 공유) ──

    private const string LatestName = NexaOne.ServiceContracts.Sys.PersonalizationRules.LatestConditionName;
    private const int MaxSavedConditions = NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxSavedConditions;
    private const int MaxValuesJsonLength = NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxValuesJsonLength;
    private const int MaxMenuIdLength = NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxMenuIdLength;

    [HttpGet("conditions")]
    [ProducesResponseType<ConditionSettingResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConditions([FromQuery] string menuId, CancellationToken ct)
    {
        var rows = await QueryAsync("SYS.ListConditionSettings",
            new() { ["menuId"] = NormalizeMenuId(menuId) }, ct);
        var items = rows.Select(r => new ConditionItemResponse(
            Str(r, "NAME"), DateTimeVal(r, "SAVED_AT"), ParseValues(Str(r, "VALUES_JSON")))).ToList();
        var latest = items.FirstOrDefault(i => string.Equals(i.Name, LatestName, StringComparison.OrdinalIgnoreCase));
        return Ok(new ConditionSettingResponse(
            latest, items.Where(i => !string.Equals(i.Name, LatestName, StringComparison.OrdinalIgnoreCase)).ToList()));
    }

    [HttpPost("conditions")]
    [ProducesResponseType<ConditionItemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveCondition([FromBody] SaveConditionRequest req, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0)
            return BadRequest(new Error("NAME_REQUIRED", "조건명을 입력해 주세요.", ErrorType.Validation));
        // DB 콜레이션(CI) 우회를 막기 위해 '$' 접두 조건명 전체를 예약한다($latest 자동 저장 행 보호)
        if (name.StartsWith('$'))
            return BadRequest(new Error("NAME_RESERVED", "'$'로 시작하는 조건명은 예약되어 사용할 수 없습니다.", ErrorType.Validation));
        if (name.Length > NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxConditionNameLength)
            return BadRequest(new Error("NAME_TOO_LONG",
                $"조건명은 {NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxConditionNameLength}자 이하여야 합니다.", ErrorType.Validation));

        var validated = ValidateAndSerialize(req.MenuId, req.Values);
        if (validated.Error is not null) return BadRequest(validated.Error);

        await ExecuteAsync("SYS.UpsertConditionSetting",
            new() { ["menuId"] = validated.MenuId, ["name"] = name, ["valuesJson"] = validated.Json }, ct);
        // 한도(10) 초과분은 오래된 것부터 자동 정리 — '$latest'는 한도 제외(§20.8)
        await ExecuteAsync("SYS.TrimConditionSettings", new() { ["menuId"] = validated.MenuId }, ct);
        return Ok(new ConditionItemResponse(name, DateTime.UtcNow, req.Values ?? new()));
    }

    [HttpPost("conditions/latest")]
    [ProducesResponseType<ConditionItemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveLatestCondition([FromBody] SaveLatestConditionRequest req, CancellationToken ct)
    {
        var validated = ValidateAndSerialize(req.MenuId, req.Values);
        if (validated.Error is not null) return BadRequest(validated.Error);

        await ExecuteAsync("SYS.UpsertConditionSetting",
            new() { ["menuId"] = validated.MenuId, ["name"] = LatestName, ["valuesJson"] = validated.Json }, ct);
        return Ok(new ConditionItemResponse(LatestName, DateTime.UtcNow, req.Values ?? new()));
    }

    [HttpDelete("conditions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCondition([FromQuery] string menuId, [FromQuery] string name, CancellationToken ct)
    {
        var trimmed = name?.Trim() ?? "";
        // $latest는 수동 삭제 대상에서 제외 — '최근 조건 초기화'(DELETE conditions/latest)로만 비운다(§20.8)
        if (string.Equals(trimmed, LatestName, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new Error("LATEST_PROTECTED", "최근 조건은 '최근 조건 초기화'로만 삭제할 수 있습니다.", ErrorType.Validation));

        var affected = await ExecuteAsync("SYS.DeleteConditionSetting",
            new() { ["menuId"] = NormalizeMenuId(menuId), ["name"] = trimmed }, ct);
        return affected == 0
            ? NotFound(new Error("CONDITION_NOT_FOUND", $"저장된 조건 '{trimmed}'을 찾을 수 없습니다.", ErrorType.NotFound))
            : NoContent();
    }

    [HttpDelete("conditions/latest")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearLatestCondition([FromQuery] string menuId, CancellationToken ct)
    {
        await ExecuteAsync("SYS.DeleteConditionSetting",
            new() { ["menuId"] = NormalizeMenuId(menuId), ["name"] = LatestName }, ct);
        return NoContent();
    }

    private (string MenuId, string Json, Error? Error) ValidateAndSerialize(
        string? menuId, Dictionary<string, string?>? values)
    {
        var normalized = NormalizeMenuId(menuId);
        if (normalized.Length > MaxMenuIdLength)
            return ("", "", new Error("MENU_ID_TOO_LONG", $"메뉴 ID는 {MaxMenuIdLength}자 이하여야 합니다.", ErrorType.Validation));
        var json = System.Text.Json.JsonSerializer.Serialize(values ?? new());
        if (json.Length > MaxValuesJsonLength)
            return ("", "", new Error("VALUES_TOO_LARGE", $"조건 값이 너무 큽니다 (최대 {MaxValuesJsonLength:N0}자).", ErrorType.Validation));
        return (normalized, json, null);
    }

    // 라우트 경로 기반 menuId 정규화 — 대소문자/후행 슬래시 변형이 다른 버킷으로 갈라지지 않게(모듈 서비스와 동일 규칙).
    private static string NormalizeMenuId(string? menuId)
    {
        var normalized = (menuId ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static Dictionary<string, string?> ParseValues(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? new();
        }
        catch { return new(); }
    }

    // ── 레지스트리 실행(토큰 사용자 바인딩) ──

    private async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(
        string queryId, Dictionary<string, object> parameters, CancellationToken ct)
        => await _dispatcher.QueryAsync(GetSql(queryId), Bind(parameters), ct);

    private async Task<int> ExecuteAsync(string queryId, Dictionary<string, object> parameters, CancellationToken ct)
        => await _dispatcher.ExecuteAsync(GetSql(queryId), Bind(parameters), ct);

    private string GetSql(string queryId)
        => _registry.TryGet(queryId, out var def) && def is not null
            ? def.Sql
            : throw new InvalidOperationException($"명명 쿼리 '{queryId}'가 레지스트리에 없습니다 — db/queries/*/SYS.xml 확인.");

    private Dictionary<string, object> Bind(Dictionary<string, object> parameters)
    {
        parameters["currentUser"] = User.CurrentUserId() ?? "SYSTEM";
        parameters["utcNow"] = DateTime.UtcNow;
        return parameters;
    }

    private static string Str(IReadOnlyDictionary<string, object?> r, string key)
        => r.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    private static string? NullableStr(IReadOnlyDictionary<string, object?> r, string key)
        => r.TryGetValue(key, out var v) && v is not null && v is not DBNull ? v.ToString() : null;

    private static int Int(IReadOnlyDictionary<string, object?> r, string key)
        => r.TryGetValue(key, out var v) && v is not null && int.TryParse(v.ToString(), out var i) ? i : 0;

    private static DateTime DateTimeVal(IReadOnlyDictionary<string, object?> r, string key)
        => r.TryGetValue(key, out var v) && v is not null && DateTime.TryParse(v.ToString(), out var d) ? d : default;
}

public record MenuIdRequest(string MenuId);
public record UserLanguageResponse(string Language);
public record UserLanguageRequest(string Language);
public record ReorderFavoritesRequest(List<string> MenuIds);
public record FavoriteMenuResponse(
    string MenuId, string MenuName, string ProgramId, string? ImageId, string UiId, int DisplaySequence);
public record RecentMenuResponse(
    string MenuId, string MenuName, string ProgramId, string? ImageId, string UiId, DateTime LastUsedAt);
public record SaveConditionRequest(string MenuId, string Name, Dictionary<string, string?>? Values);
public record SaveLatestConditionRequest(string MenuId, Dictionary<string, string?>? Values);
public record ConditionItemResponse(string Name, DateTime SavedAt, Dictionary<string, string?> Values);
public record ConditionSettingResponse(ConditionItemResponse? Latest, List<ConditionItemResponse> Items);
