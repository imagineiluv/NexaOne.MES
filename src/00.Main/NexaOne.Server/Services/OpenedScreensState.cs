namespace NexaOne.Server.Services;

/// <summary>
/// MDI 탭 상태(Scoped — Blazor 서킷당 1개). /meta 내비게이션으로 열린 화면(uiId,title)을 입력 순서대로
/// 보관하고, 활성 화면을 추적한다. MesShellLayout이 구독해 하단 탭 스트립을 렌더하고, 라우트 변경 시 Open을
/// 호출해 갱신한다. 단순/견고가 목표 — 중복 uiId는 한 번만 보관하고, 닫기 시 활성 탭이 사라지면 인접 탭으로
/// 활성을 이동한다(없으면 비운다).
/// </summary>
public sealed class OpenedScreensState
{
    private readonly List<OpenedScreen> _screens = new();

    /// <summary>열린 화면 목록(입력 순서 보존, 읽기 전용 스냅샷).</summary>
    public IReadOnlyList<OpenedScreen> Screens => _screens;

    /// <summary>현재 활성(포커스된) 화면 uiId. 없으면 null.</summary>
    public string? ActiveUiId { get; private set; }

    /// <summary>상태 변경 통지 — 구독자(MesShellLayout)가 StateHasChanged를 호출하도록.</summary>
    public event Action? Changed;

    /// <summary>화면을 연다(이미 있으면 제목만 갱신). 어느 경우든 활성으로 만든다.</summary>
    public void Open(string uiId, string title)
    {
        if (string.IsNullOrWhiteSpace(uiId)) return;

        var existing = _screens.FindIndex(s => string.Equals(s.UiId, uiId, StringComparison.OrdinalIgnoreCase));
        var changed = false;
        if (existing < 0)
        {
            _screens.Add(new OpenedScreen(uiId, string.IsNullOrWhiteSpace(title) ? uiId : title));
            changed = true;
        }
        else if (!string.IsNullOrWhiteSpace(title) && !string.Equals(_screens[existing].Title, title, StringComparison.Ordinal))
        {
            _screens[existing] = _screens[existing] with { Title = title };
            changed = true;
        }

        if (!string.Equals(ActiveUiId, uiId, StringComparison.OrdinalIgnoreCase))
        {
            ActiveUiId = uiId;
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>탭을 닫는다. 활성 탭을 닫으면 인접(이전 우선) 탭으로 활성을 옮기고, 옮길 다음 화면 uiId를
    /// 반환한다(없으면 null — 호출자가 기본 화면으로 내비게이트). 닫은 게 비활성 탭이면 null 반환.</summary>
    public string? Close(string uiId)
    {
        var idx = _screens.FindIndex(s => string.Equals(s.UiId, uiId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return null;

        var wasActive = string.Equals(ActiveUiId, uiId, StringComparison.OrdinalIgnoreCase);
        _screens.RemoveAt(idx);

        string? next = null;
        if (wasActive)
        {
            if (_screens.Count > 0)
            {
                var newIdx = Math.Min(idx, _screens.Count - 1);
                next = _screens[newIdx].UiId;
                ActiveUiId = next;
            }
            else
            {
                ActiveUiId = null;
            }
        }

        Changed?.Invoke();
        return wasActive ? next : null;
    }

    /// <summary>지정 탭만 남기고 모두 닫는다(우클릭 '다른 탭 모두 닫기'). 활성은 남긴 탭으로.</summary>
    public void CloseOthers(string uiId)
    {
        var keep = _screens.Find(s => string.Equals(s.UiId, uiId, StringComparison.OrdinalIgnoreCase));
        if (keep is null) return;
        _screens.Clear();
        _screens.Add(keep);
        ActiveUiId = keep.UiId;
        Changed?.Invoke();
    }

    /// <summary>모든 탭을 닫는다(우클릭 '모두 닫기'). 호출자가 기본 화면으로 내비게이트한다.</summary>
    public void CloseAll()
    {
        if (_screens.Count == 0) return;
        _screens.Clear();
        ActiveUiId = null;
        Changed?.Invoke();
    }
}

/// <summary>MDI 탭 한 칸 — 열린 화면의 식별자(uiId)와 표시 제목.</summary>
public sealed record OpenedScreen(string UiId, string Title);
