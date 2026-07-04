namespace NexaOne.Web.Services;

/// <summary>
/// UI 공통 문구 다국어 서비스(P3-14 v1, 회로 수명 Scoped).
/// <para>
/// 원천은 SYS_MULTI_LANGUAGE_RESOURCE(V031) — 셸이 사용자 언어(SYS_USER.LANGUAGE)로 리소스를 1회 로드해
/// <see cref="Load"/>로 주입한다. 한국어가 기본 언어라 리소스에는 비-한국어(EnUs 등)만 시드하고, 미로드/
/// 미존재 키는 호출부 인라인 폴백(한국어)을 그대로 쓴다 — 리소스가 없어도 화면이 절대 깨지지 않는다.
/// 화면 제목·메뉴명 등 DB 데이터 문구는 대상이 아니다(메뉴 다국어는 후속).
/// </para>
/// </summary>
public sealed class UiTextService
{
    private Dictionary<string, string> _map = new(StringComparer.Ordinal);

    /// <summary>현재 언어(LanguageType 이름 문자열, 기본 KoKr).</summary>
    public string Language { get; private set; } = "KoKr";

    /// <summary>리소스 교체 통지 — 구독 컴포넌트가 StateHasChanged를 호출하도록.</summary>
    public event Action? Changed;

    /// <summary>키의 번역을 반환한다. 미로드/미존재/빈 값이면 폴백(한국어 기본 문구).</summary>
    public string T(string key, string fallback)
        => _map.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    /// <summary>언어 리소스 로드(셸이 로그인 후·언어 전환 시 호출). KoKr은 빈 맵 = 전부 폴백.</summary>
    public void Load(string language, Dictionary<string, string> map)
    {
        Language = string.IsNullOrWhiteSpace(language) ? "KoKr" : language;
        _map = map;
        Changed?.Invoke();
    }
}
