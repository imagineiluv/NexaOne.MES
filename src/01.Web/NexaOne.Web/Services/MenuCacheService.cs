using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services;

/// <summary>
/// 메뉴 목록 캐시 — NavMenu와 MDI 탭바(§20.7 탭 제목 메뉴명 조회)가 공유한다.
/// 동시 호출 시 단일 API 요청만 발생하도록 로드 Task 자체를 캐시한다.
/// </summary>
public sealed class MenuCacheService(IApiClient api)
{
    private Task<List<MenuItemDto>>? _loadTask;

    public Task<List<MenuItemDto>> GetMenusAsync(bool forceReload = false)
    {
        if (forceReload || _loadTask is null || _loadTask.IsFaulted || _loadTask.IsCanceled)
            _loadTask = api.GetMenuAsync();
        return _loadTask;
    }

    /// <summary>라우트 경로(ProgramId)로 메뉴를 찾는다. 선행 슬래시는 무시하고 비교한다.</summary>
    public async Task<MenuItemDto?> FindByProgramIdAsync(string path)
    {
        var menus = await GetMenusAsync();
        var target = path.TrimStart('/');
        return menus.FirstOrDefault(m =>
            m.MenuType != "Folder" &&
            string.Equals(m.ProgramId?.TrimStart('/'), target, StringComparison.OrdinalIgnoreCase));
    }
}
