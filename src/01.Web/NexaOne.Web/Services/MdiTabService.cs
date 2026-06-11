using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace NexaOne.Web.Services;

/// <summary>
/// 설계서 §20.7 — 다중 탭(MDI) 폼 관리 (웹 적응: MainLayout + 탭 라우팅).
/// 중복 열기 정책은 menuId(라우트 경로) + 정규화 파라미터 해시를 기준으로 한다.
/// </summary>
public sealed class MdiTabService
{
    /// <summary>§20.7: 최대 탭 개수 기본값. 초과 시 가장 오래된 미사용 탭부터 자동 닫기를 시도한다.</summary>
    public const int MaxTabs = 20;

    private readonly List<MdiTab> _tabs = [];
    private long _seq;

    public IReadOnlyList<MdiTab> Tabs => _tabs;
    public MdiTab? ActiveTab { get; private set; }
    public IReadOnlyList<MdiTab> DirtyTabs => _tabs.Where(t => t.IsDirty).ToList();

    public event Action? Changed;

    // §20.7: 탭 제목 파라미터 요약 대상 키 — 메뉴(라우트)별 설정으로 관리, 없으면 메뉴명만 표시.
    private static readonly Dictionary<string, string[]> SummaryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/fdc/monitor"]      = ["parameterid", "equipmentid"],
        ["/ept/state-change"] = ["equipmentid"],
        ["/ept/alarms"]       = ["equipmentid"],
        ["/rms/approval"]     = ["recipeid"],
    };

    // §20.7: ISO 형식으로 정규화할 날짜성 파라미터 키
    private static readonly HashSet<string> DateKeys = new(StringComparer.OrdinalIgnoreCase)
        { "from", "to", "date", "startdate", "enddate" };

    /// <summary>탭 대상이 아닌 경로(인증/오류 화면).</summary>
    public static bool IsTabbablePath(string path) =>
        !path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/error", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 내비게이션 도착 시 호출. 동일 menuId + 동일 파라미터 조합이면 기존 탭을 활성화하고,
    /// 파라미터가 다르면 새 탭을 연다(§20.7 중복 열기 정책).
    /// </summary>
    public MdiTab Open(string path, string query, string menuName)
    {
        var normalized = NormalizeParameters(query);
        var hash  = BuildParametersHash(normalized);
        var tabId = $"{path.ToLowerInvariant()}#{hash}";

        var tab = _tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab is null)
        {
            EvictIfNeeded();
            tab = new MdiTab
            {
                TabId  = tabId,
                MenuId = path,
                Url    = string.IsNullOrEmpty(query) ? path : $"{path}{query}",
                Title  = BuildTitle(path, menuName, normalized),
            };
            _tabs.Add(tab);
        }

        Activate(tab);
        return tab;
    }

    public void Activate(MdiTab tab)
    {
        foreach (var t in _tabs) t.IsActive = false;
        tab.IsActive = true;
        tab.LastActivatedSeq = ++_seq;
        ActiveTab = tab;
        Changed?.Invoke();
    }

    /// <summary>탭 제거. §20.7: 닫기 시 OnClosed 리소스 정리 훅을 호출한다.</summary>
    public void Remove(MdiTab tab)
    {
        tab.OnClosed?.Invoke();
        _tabs.Remove(tab);
        if (ActiveTab == tab) ActiveTab = null;
        Changed?.Invoke();
    }

    /// <summary>탭을 닫은 뒤 활성화할 다음 탭(최근 활성 순).</summary>
    public MdiTab? NextAfterClose(MdiTab closing) =>
        _tabs.Where(t => t != closing)
             .OrderByDescending(t => t.LastActivatedSeq)
             .FirstOrDefault();

    /// <summary>§20.7: DirtyStateChanged 발생 시 활성 탭 제목에 '*' 반영.</summary>
    public void SetActiveDirty(bool dirty)
    {
        if (ActiveTab is null || ActiveTab.IsDirty == dirty) return;
        ActiveTab.IsDirty = dirty;
        Changed?.Invoke();
    }

    /// <summary>활성 탭의 표준 저장 명령 등록(§20.7: 저장 후 닫기). 페이지 Dispose 시 해제할 것.</summary>
    public void RegisterSaveHandler(Func<Task<bool>> handler)
    {
        if (ActiveTab is not null) ActiveTab.SaveHandler = handler;
    }

    public void UnregisterSaveHandler()
    {
        if (ActiveTab is not null) ActiveTab.SaveHandler = null;
    }

    /// <summary>전체 닫기. 각 탭의 OnClosed 정리 훅을 순서대로 호출한다.</summary>
    public void Clear()
    {
        foreach (var t in _tabs) t.OnClosed?.Invoke();
        _tabs.Clear();
        ActiveTab = null;
        Changed?.Invoke();
    }

    // §20.7: 최대 탭 수 초과 시 가장 오래된 미사용 탭부터 자동 닫기를 시도한다.
    // 더티 탭, 장시간 작업 중인 탭, 모달 자식 창이 열린 탭, 활성 탭은 제외 — 후보가 없으면 초과를 허용한다.
    private void EvictIfNeeded()
    {
        while (_tabs.Count >= MaxTabs)
        {
            var victim = _tabs
                .Where(t => t is { IsActive: false, IsDirty: false, IsBusy: false, HasModalChild: false })
                .OrderBy(t => t.LastActivatedSeq)
                .FirstOrDefault();
            if (victim is null) return;
            Remove(victim);
        }
    }

    // §20.7: 파라미터 해시 — 정렬된 키와 값으로 생성, 키 대소문자 무시, 날짜는 ISO 형식 정규화.
    private static SortedDictionary<string, string> NormalizeParameters(string query)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return result;

        var parsed = QueryHelpers.ParseQuery(query.TrimStart('?'));
        foreach (var (key, values) in parsed)
        {
            if (string.IsNullOrEmpty(key)) continue;

            var value = values.ToString();
            if (DateKeys.Contains(key) &&
                DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            {
                value = dt.ToString("O", CultureInfo.InvariantCulture);
            }
            result[key.ToLowerInvariant()] = value;
        }
        return result;
    }

    private static string BuildParametersHash(SortedDictionary<string, string> normalized)
    {
        if (normalized.Count == 0) return string.Empty;
        var canonical = string.Join("&", normalized.Select(kv => $"{kv.Key}={kv.Value}"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..16];
    }

    // §20.7: 탭 제목 = 메뉴명(다국어) + 파라미터 요약. 예: 설비 알람 이력 [EQ001]
    private static string BuildTitle(string path, string menuName, SortedDictionary<string, string> normalized)
    {
        if (!SummaryKeys.TryGetValue(path, out var keys)) return menuName;

        var parts = keys
            .Select(k => normalized.GetValueOrDefault(k))
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        return parts.Count == 0 ? menuName : $"{menuName} [{string.Join(", ", parts)}]";
    }
}

/// <summary>MDI 탭 1개의 상태. TabId = 경로 + 파라미터 해시.</summary>
public sealed class MdiTab
{
    public required string TabId  { get; init; }
    public required string MenuId { get; init; }   // 라우트 경로 (ProgramId)
    public required string Url    { get; init; }   // 쿼리 포함 상대 URL
    public required string Title  { get; set; }

    public bool IsActive      { get; set; }
    public bool IsDirty       { get; set; }
    public bool IsBusy        { get; set; }   // §20.7: 장시간 작업 중 → 자동 닫기 제외
    public bool HasModalChild { get; set; }   // §20.7: 모달 자식 창 열림 → 자동 닫기 제외
    public long LastActivatedSeq { get; set; }

    /// <summary>해당 폼의 표준 저장 명령. true 반환 시 저장 성공.</summary>
    public Func<Task<bool>>? SaveHandler { get; set; }

    /// <summary>탭 닫힘 시 리소스 정리 훅.</summary>
    public Action? OnClosed { get; set; }

    /// <summary>§20.7: 더티 탭은 제목 뒤에 '*' 표시.</summary>
    public string DisplayTitle => IsDirty ? $"{Title} *" : Title;
}
