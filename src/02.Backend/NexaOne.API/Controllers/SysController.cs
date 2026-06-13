using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/sys")]
[Authorize]
public class SysController(
    UserService userService,
    MenuService menuService,
    ConditionSettingService conditionService,
    UserMenuService userMenuService) : ControllerBase
{
    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value ?? string.Empty;

    // ── Menu ──────────────────────────────────────────────────────────────────

    [HttpGet("menu")]
    public async Task<IActionResult> GetMenu(CancellationToken ct)
    {
        var userId = CurrentUserId;

        try
        {
            var tree = await menuService.GetAuthorizedMenuTreeAsync(userId, ct);
            return Ok(FlattenTree(tree));
        }
        catch
        {
            // SYS_MENU table may not exist yet — return empty so the app still boots
            return Ok(Array.Empty<MenuItemDto>());
        }
    }

    private static IEnumerable<MenuItemDto> FlattenTree(IReadOnlyList<MenuNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return new MenuItemDto(
                node.Item.MenuId, node.Item.MenuName, node.Item.ParentMenuId,
                node.Item.DisplaySequence, node.Item.MenuType.ToString(),
                node.Item.ProgramId, node.Item.ImageId, node.Depth, node.IsOrphan);
            foreach (var child in FlattenTree(node.Children))
                yield return child;
        }
    }


    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("users")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> GetUsers(CancellationToken ct)
    {
        var result = await userService.GetAllUsersAsync(ct);
        return result.IsSuccess ? Ok(result.Value.Select(ToUserDto)) : BadRequest(result.Error);
    }

    [HttpGet("users/{userId}")]
    public async Task<IActionResult> GetUser(string userId, CancellationToken ct)
    {
        var result = await userService.GetUserAsync(userId, ct);
        return result.IsSuccess ? Ok(ToUserDto(result.Value)) : NotFound(result.Error);
    }

    [HttpPost("users")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        var result = await userService.CreateUserAsync(
            req.UserId, req.UserName, req.PasswordHash, req.Email, req.RoleId,
            Enum.TryParse<LanguageType>(req.Language, out var lang) && Enum.IsDefined(lang)
                ? lang : LanguageType.KoKr, ct);
        return result.IsSuccess ? Ok(ToUserDto(result.Value)) : BadRequest(result.Error);
    }

    [HttpPut("users/{userId}/deactivate")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> DeactivateUser(string userId, CancellationToken ct)
    {
        var result = await userService.DeactivateUserAsync(userId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    /// <summary>§20.10 — 관리자 잠금 해제 (해제는 시간 만료 또는 관리자만 가능).</summary>
    [HttpPut("users/{userId}/unlock")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> UnlockUser(string userId, CancellationToken ct)
    {
        var result = await userService.UnlockUserAsync(userId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // 도메인 User를 그대로 직렬화하면 PASSWORD_HASH가 응답에 노출된다 — 반드시 DTO로 매핑한다.
    private static UserDto ToUserDto(User u) => new(
        u.Id, u.UserName, u.Email, u.RoleId, u.Language.ToString(),
        u.IsActive, u.LastLoginAt, u.LockedUntil, u.PasswordState.ToString());

    // ── Roles ─────────────────────────────────────────────────────────────────

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles(CancellationToken ct)
    {
        var result = await userService.GetAllRolesAsync(ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("roles/{roleId}")]
    public async Task<IActionResult> GetRole(string roleId, CancellationToken ct)
    {
        var result = await userService.GetRoleAsync(roleId, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost("roles")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest req, CancellationToken ct)
    {
        var result = await userService.CreateRoleAsync(req.RoleId, req.RoleName, req.Description ?? "", ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("roles/{roleId}/permissions")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> AddPermission(string roleId, [FromBody] PermissionRequest req, CancellationToken ct)
    {
        var result = await userService.AddPermissionAsync(roleId, req.Permission, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpDelete("roles/{roleId}/permissions/{permission}")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> RemovePermission(string roleId, string permission, CancellationToken ct)
    {
        var result = await userService.RemovePermissionAsync(roleId, permission, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // ── MultiLanguageResource ─────────────────────────────────────────────────

    [HttpGet("languages")]
    public async Task<IActionResult> GetLanguageResources([FromQuery] string? menuId, [FromQuery] string? language, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(menuId))
        {
            var result = await userService.GetResourcesByMenuAsync(menuId, ct);
            return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
        }

        if (!string.IsNullOrEmpty(language) && Enum.TryParse<LanguageType>(language, out var lang) && Enum.IsDefined(lang))
        {
            var result = await userService.GetResourcesByLanguageAsync(lang, ct);
            return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
        }

        var allResult = await userService.GetResourcesByLanguageAsync(LanguageType.KoKr, ct);
        return allResult.IsSuccess ? Ok(allResult.Value) : BadRequest(allResult.Error);
    }

    [HttpPost("languages")]
    [Authorize(Policy = "perm:sys:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    public async Task<IActionResult> UpsertLanguageResource([FromBody] UpsertLanguageResourceRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<LanguageType>(req.Language, out var lang) || !Enum.IsDefined(lang))
            return BadRequest($"Invalid language: {req.Language}");

        var result = await userService.UpsertResourceAsync(req.ResourceKey, req.MenuId, lang, req.Value, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── ConditionSetting (설계서 20.8 조건 저장/불러오기) ─────────────────────

    [HttpGet("conditions")]
    public async Task<IActionResult> GetConditions([FromQuery] string menuId, CancellationToken ct)
    {
        var result = await conditionService.GetConditionsAsync(CurrentUserId, menuId, ct);
        if (result.IsFailure) return BadRequest(result.Error);

        var latest = result.Value.FirstOrDefault(s => s.IsLatest);
        var items = result.Value
            .Where(s => !s.IsLatest)
            .Select(ToItemDto)
            .ToList();
        return Ok(new ConditionSettingDto(latest is null ? null : ToItemDto(latest), items));
    }

    [HttpPost("conditions")]
    public async Task<IActionResult> SaveCondition([FromBody] SaveConditionRequest req, CancellationToken ct)
    {
        var result = await conditionService.SaveConditionAsync(
            CurrentUserId, req.MenuId, req.Name, SerializeValues(req.Values), ct);
        return result.IsSuccess ? Ok(ToItemDto(result.Value)) : BadRequest(result.Error);
    }

    [HttpPost("conditions/latest")]
    public async Task<IActionResult> SaveLatestCondition([FromBody] SaveLatestConditionRequest req, CancellationToken ct)
    {
        var result = await conditionService.SaveLatestAsync(
            CurrentUserId, req.MenuId, SerializeValues(req.Values), ct);
        return result.IsSuccess ? Ok(ToItemDto(result.Value)) : BadRequest(result.Error);
    }

    [HttpDelete("conditions")]
    public async Task<IActionResult> DeleteCondition([FromQuery] string menuId, [FromQuery] string name, CancellationToken ct)
    {
        var result = await conditionService.DeleteConditionAsync(CurrentUserId, menuId, name, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpDelete("conditions/latest")]
    public async Task<IActionResult> ClearLatestCondition([FromQuery] string menuId, CancellationToken ct)
    {
        var result = await conditionService.ClearLatestAsync(CurrentUserId, menuId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // ── Favorite / Recent menu (설계서 20.12 즐겨찾기/최근 메뉴) ──────────────

    [HttpGet("favorites")]
    public async Task<IActionResult> GetFavorites(CancellationToken ct)
    {
        var result = await userMenuService.GetFavoritesAsync(CurrentUserId, ct);
        return result.IsSuccess
            ? Ok(result.Value.Select(e => new FavoriteMenuDto(
                e.Menu.MenuId, e.Menu.MenuName, e.Menu.ProgramId, e.Menu.ImageId, e.DisplaySequence)))
            : BadRequest(result.Error);
    }

    [HttpPost("favorites")]
    public async Task<IActionResult> AddFavorite([FromBody] FavoriteMenuRequest req, CancellationToken ct)
    {
        var result = await userMenuService.AddFavoriteAsync(CurrentUserId, req.MenuId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpDelete("favorites")]
    public async Task<IActionResult> RemoveFavorite([FromQuery] string menuId, CancellationToken ct)
    {
        var result = await userMenuService.RemoveFavoriteAsync(CurrentUserId, menuId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    /// <summary>즐겨찾기 표시 순서 일괄 변경 — 현행 드래그 정렬의 웹 적응(up/down 버튼).</summary>
    [HttpPut("favorites/order")]
    public async Task<IActionResult> ReorderFavorites([FromBody] ReorderFavoritesRequest req, CancellationToken ct)
    {
        var result = await userMenuService.ReorderFavoritesAsync(
            CurrentUserId, req.MenuIds ?? [], ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpGet("recent-menus")]
    public async Task<IActionResult> GetRecentMenus(CancellationToken ct)
    {
        var result = await userMenuService.GetRecentAsync(CurrentUserId, ct);
        return result.IsSuccess
            ? Ok(result.Value.Select(e => new RecentMenuDto(
                e.Menu.MenuId, e.Menu.MenuName, e.Menu.ProgramId, e.Menu.ImageId, e.LastUsedAt)))
            : BadRequest(result.Error);
    }

    [HttpPost("recent-menus")]
    public async Task<IActionResult> RecordRecentMenu([FromBody] RecentMenuRequest req, CancellationToken ct)
    {
        var result = await userMenuService.RecordRecentAsync(CurrentUserId, req.MenuId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // 키를 소문자로 정규화해 저장한다 — 복원 시 대소문자만 다른 중복 키 충돌을 막는다 (마지막 값 우선)
    private static string SerializeValues(Dictionary<string, string?>? values) =>
        JsonSerializer.Serialize(NormalizeKeys(values));

    private static ConditionItemDto ToItemDto(ConditionSetting s)
    {
        Dictionary<string, string?> values;
        try
        {
            // 정규화 이전에 저장된 데이터의 대소문자 중복 키도 여기서 흡수한다
            values = NormalizeKeys(JsonSerializer.Deserialize<Dictionary<string, string?>>(s.ValuesJson));
        }
        catch
        {
            values = new Dictionary<string, string?>();   // 손상 JSON은 빈 조건으로 취급
        }
        return new ConditionItemDto(s.Name, s.SavedAt, values);
    }

    private static Dictionary<string, string?> NormalizeKeys(Dictionary<string, string?>? values)
    {
        var normalized = new Dictionary<string, string?>();
        foreach (var pair in values ?? new Dictionary<string, string?>())
            normalized[pair.Key.ToLowerInvariant()] = pair.Value;
        return normalized;
    }
}

public record CreateUserRequest(string UserId, string UserName, string PasswordHash, string Email, string RoleId, string Language = "KoKr");
public record UserDto(
    string Id, string UserName, string Email, string RoleId, string Language,
    bool IsActive, DateTime? LastLoginAt, DateTime? LockedUntil, string PasswordState);
public record SaveConditionRequest(string MenuId, string Name, Dictionary<string, string?>? Values);
public record SaveLatestConditionRequest(string MenuId, Dictionary<string, string?>? Values);
public record ConditionSettingDto(ConditionItemDto? Latest, List<ConditionItemDto> Items);
public record ConditionItemDto(string Name, DateTime SavedAt, Dictionary<string, string?> Values);
public record CreateRoleRequest(string RoleId, string RoleName, string? Description);
public record PermissionRequest(string Permission);
public record UpsertLanguageResourceRequest(string ResourceKey, string MenuId, string Language, string Value);
public record MenuItemDto(
    string MenuId, string MenuName, string? ParentMenuId,
    int DisplaySequence, string MenuType, string ProgramId,
    string? ImageId, int Depth, bool IsOrphan);
public record FavoriteMenuRequest(string MenuId);
public record ReorderFavoritesRequest(List<string>? MenuIds);
public record RecentMenuRequest(string MenuId);
public record FavoriteMenuDto(
    string MenuId, string MenuName, string ProgramId, string? ImageId, int DisplaySequence);
public record RecentMenuDto(
    string MenuId, string MenuName, string ProgramId, string? ImageId, DateTime LastUsedAt);
