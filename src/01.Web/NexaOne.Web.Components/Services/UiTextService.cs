namespace NexaOne.Web.Services;

/// <summary>
/// UI 공통 문구 다국어 서비스(P3-14 v1, 회로 수명 Scoped).
/// <para>
/// 원천은 SYS_MULTI_LANGUAGE_RESOURCE(V031) — 셸이 사용자 언어(SYS_USER.LANGUAGE)로 리소스를 1회 로드해
/// <see cref="Load"/>로 주입한다. 한국어가 기본 언어라 리소스에는 비-한국어(EnUs 등)만 시드하고, 미로드/
/// 미존재 키는 호출부 인라인 폴백(한국어)을 그대로 쓴다 — 리소스가 없어도 화면이 절대 깨지지 않는다.
/// 공통 문구뿐 아니라 menu.{MENU_ID}, screen.{UI_ID}.title 리소스도 같은 현재 언어 맵에서 조회한다.
/// 내부 메뉴 ID/모듈 코드는 유지하되 사용자에게 보이는 이름은 언어별 업무 용어로 분리한다.
/// </para>
/// </summary>
public sealed class UiTextService
{
    private static readonly IReadOnlyDictionary<string, string> FieldTokenLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ID"] = "ID",
            ["NO"] = "No.",
            ["NUM"] = "Number",
            ["QTY"] = "Quantity",
            ["AMT"] = "Amount",
            ["DT"] = "Date",
            ["TS"] = "Time",
            ["UOM"] = "Unit",
            ["LOT"] = "LOT",
            ["BOM"] = "BOM",
            ["OEE"] = "OEE",
            ["SPC"] = "SPC",
            ["AI"] = "AI",
            ["URL"] = "URL",
            ["API"] = "API",
        };

    private Dictionary<string, string> _map = new(StringComparer.Ordinal);

    /// <summary>현재 언어(LanguageType 이름 문자열, 기본 KoKr).</summary>
    public string Language { get; private set; } = "KoKr";

    /// <summary>리소스 교체 통지 — 구독 컴포넌트가 StateHasChanged를 호출하도록.</summary>
    public event Action? Changed;

    /// <summary>키의 번역을 반환한다. 미로드/미존재/빈 값이면 폴백(한국어 기본 문구).</summary>
    public string T(string key, string fallback)
        => _map.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    /// <summary>
    /// 메타데이터 필드의 현재 언어 라벨을 반환한다. 명시적인 <c>field.{KEY}</c> 리소스를 우선하고,
    /// 영문 모드에서 리소스가 없으면 안정적인 필드 키를 사람이 읽을 수 있는 영문으로 변환한다.
    /// 따라서 새 화면이 추가되어도 한국어 라벨이 영문 화면에 그대로 노출되지 않으며, 업무별 번역은
    /// 리소스를 추가하는 즉시 자동 변환보다 우선한다.
    /// </summary>
    public string Field(string fieldKey, string fallback)
    {
        if (string.IsNullOrWhiteSpace(fieldKey)) return fallback;

        var resourceKey = $"field.{fieldKey}";
        if (_map.TryGetValue(resourceKey, out var value) && value.Length > 0) return value;
        return string.Equals(Language, "EnUs", StringComparison.OrdinalIgnoreCase)
            ? HumanizeFieldKey(fieldKey)
            : fallback;
    }

    private static string HumanizeFieldKey(string fieldKey)
    {
        var tokens = fieldKey
            .Replace('-', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return fieldKey;

        return string.Join(' ', tokens.Select(token =>
        {
            if (FieldTokenLabels.TryGetValue(token, out var label)) return label;
            if (token.All(char.IsDigit)) return token;
            var lower = token.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower[1..];
        }));
    }

    /// <summary>언어 리소스 로드(셸이 로그인 후·언어 전환 시 호출). KoKr은 빈 맵 = 전부 폴백.</summary>
    public void Load(string language, Dictionary<string, string> map)
    {
        Language = string.IsNullOrWhiteSpace(language) ? "KoKr" : language;
        _map = map;
        Changed?.Invoke();
    }
}
