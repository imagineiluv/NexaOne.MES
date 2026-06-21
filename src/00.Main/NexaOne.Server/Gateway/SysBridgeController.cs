using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 SYS 관리 엔드포인트(ADR-008 얇은 브리지). plugin-ALC UserService/UserRegistrationService를
/// ISysBridge로 호출한다. 쓰기(역할 관리·신청 반려·사용자 비활성)는 sys:manage 수동 검사.
/// Result→HTTP(BridgeResultExtensions: Conflict→409·NotFound→404·Validation→400·성공→200/204). (modules ON에서만 동작.)
///
/// 경로는 api/v1/sys/admin — QueryCatalogController(api/v1/sys/queries)·AuthController(api/v1/auth)와 충돌하지 않는다.
/// 보안 가드(S7): 자격증명/비밀번호/로그인/리프레시·승인(다중 애그리거트)·잠금 해제는 본 컨트롤러에 없다 —
/// 인증은 격리 경로(AuthController + GatewayLoginService + db/queries-auth)가 소유한다. 순수 조회는 /api/v1/query/SYS.*
/// (공개 게이트웨이, PASSWORD_HASH 제외)로.</summary>
[ApiController]
[Route("api/v1/sys/admin")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SysBridgeController : ControllerBase
{
    private readonly ISysBridge _bridge;
    public SysBridgeController(ISysBridge bridge) => _bridge = bridge;

    // ── 역할(Role) 관리 ──

    [HttpPost("roles")]
    [ProducesResponseType<RoleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.SysManage)) return Forbid();
        return (await _bridge.CreateRoleAsync(req.RoleId, req.RoleName, req.Description, ct)).ToActionResult();
    }

    [HttpPost("roles/{roleId}/permissions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddPermission(string roleId, [FromBody] PermissionRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.SysManage)) return Forbid();
        return (await _bridge.AddPermissionAsync(roleId, req.Permission, ct)).ToActionResult();
    }

    [HttpDelete("roles/{roleId}/permissions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemovePermission(string roleId, [FromBody] PermissionRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.SysManage)) return Forbid();
        return (await _bridge.RemovePermissionAsync(roleId, req.Permission, ct)).ToActionResult();
    }

    // ── 사용자 등록 신청 반려 ──

    [HttpPost("user-requests/{requestId}/reject")]
    [ProducesResponseType<UserRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RejectRequest(string requestId, [FromBody] RejectRequestRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.SysManage)) return Forbid();
        var rejectedBy = User.CurrentUserId() ?? "SYSTEM";
        return (await _bridge.RejectRequestAsync(requestId, rejectedBy, req.Reason, ct)).ToActionResult();
    }

    // ── 사용자 비활성(상태전이) ──

    [HttpPost("users/{userId}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeactivateUser(string userId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.SysManage)) return Forbid();
        return (await _bridge.DeactivateUserAsync(userId, ct)).ToActionResult();
    }

}

public record CreateRoleRequest(string RoleId, string RoleName, string Description);
public record PermissionRequest(string Permission);
public record RejectRequestRequest(string Reason);
