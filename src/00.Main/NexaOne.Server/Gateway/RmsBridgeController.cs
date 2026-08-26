using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 RMS 레시피 승인 엔드포인트(ADR-008 얇은 브리지). plugin-ALC RecipeService를
/// IRecipeApprovalBridge로 호출한다. 조회는 rms:read, 쓰기는 rms:manage 정책으로 분리한다.
/// 쓰기 주체는 식별 가능한 토큰 주체만 허용해 SYSTEM 감사 폴백을 차단한다.
/// (modules ON에서만 IRecipeApprovalBridge가 등록되므로 동작.)</summary>
[ApiController]
[Route("api/v1/rms")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class RmsBridgeController : ControllerBase
{
    private readonly IRecipeApprovalBridge _bridge;

    public RmsBridgeController(IRecipeApprovalBridge bridge) => _bridge = bridge;

    [HttpGet("recipes")]
    [RequirePermission(Permissions.RmsRead)]
    [ProducesResponseType<IReadOnlyList<RecipeDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecipes([FromQuery] string? equipmentClassId, [FromQuery] string? state, CancellationToken ct)
        => Ok(await _bridge.GetRecipesAsync(equipmentClassId, state, ct));

    [HttpGet("recipes/{recipeId}")]
    [RequirePermission(Permissions.RmsRead)]
    [ProducesResponseType<RecipeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecipe(string recipeId, CancellationToken ct)
        => (await _bridge.GetRecipeAsync(recipeId, ct)).ToActionResult();

    [HttpPost("recipes")]
    [ProducesResponseType<RecipeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> CreateRecipe([FromBody] CreateRecipeRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out _)) return Unauthorized();
        return (await _bridge.CreateRecipeAsync(req.RecipeId, req.Name, req.Description, req.EquipmentClassId, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/request-approval")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> RequestApproval(string recipeId, CancellationToken ct)
    {
        if (!TryGetExternalActor(out _)) return Unauthorized();
        return (await _bridge.RequestApprovalAsync(recipeId, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/approve1")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> Approve1(string recipeId, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.Approve1Async(recipeId, actor, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/approve2")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> Approve2(string recipeId, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.Approve2Async(recipeId, actor, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> Release(string recipeId, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.ReleaseAsync(recipeId, actor, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/reject")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> Reject(string recipeId, [FromBody] RejectRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out _)) return Unauthorized();
        return (await _bridge.RejectAsync(recipeId, req.Reason, ct)).ToActionResult();
    }

    [HttpPost("recipes/{recipeId}/new-version")]
    [ProducesResponseType<RecipeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> CreateNewVersion(string recipeId, [FromBody] NewVersionRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out _)) return Unauthorized();
        return (await _bridge.CreateNewVersionAsync(recipeId, req.NewRecipeId, ct)).ToActionResult();
    }

    [HttpGet("recipes/{recipeId}/params")]
    [RequirePermission(Permissions.RmsRead)]
    [ProducesResponseType<IReadOnlyList<RecipeParamDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetParams(string recipeId, CancellationToken ct)
        => Ok(await _bridge.GetParamsAsync(recipeId, ct));

    [HttpPost("recipes/{recipeId}/params")]
    [ProducesResponseType<RecipeParamDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> AddParam(string recipeId, [FromBody] AddParamRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out _)) return Unauthorized();
        return (await _bridge.AddParamAsync(req.ParamId, recipeId, req.ParamName, req.ParamValue, req.Unit, req.SortOrder, ct)).ToActionResult();
    }

    [HttpPut("recipes/params/{paramId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> UpdateParam(string paramId, [FromBody] UpdateParamRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out _)) return Unauthorized();
        return (await _bridge.UpdateParamAsync(paramId, req.NewValue, ct)).ToActionResult();
    }

    [HttpDelete("recipes/params/{paramId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.RmsManage)]
    public async Task<IActionResult> DeleteParam(string paramId, CancellationToken ct)
    {
        if (!TryGetExternalActor(out _)) return Unauthorized();
        return (await _bridge.DeleteParamAsync(paramId, ct)).ToActionResult();
    }

    private bool TryGetExternalActor(out string actor)
    {
        actor = User.CurrentUserId()?.Trim() ?? string.Empty;
        return actor.Length > 0;
    }
}

public record CreateRecipeRequest(string RecipeId, string Name, string Description, string EquipmentClassId);
public record RejectRequest(string Reason);
public record NewVersionRequest(string NewRecipeId);
public record AddParamRequest(string ParamId, string ParamName, string ParamValue, string Unit, int SortOrder);
public record UpdateParamRequest(string NewValue);
