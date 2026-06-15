using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Extensions;
using NexaOne.RMS.Application.Rms;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/rms")]
[Authorize]
public class RmsController(RecipeService recipeService) : ControllerBase
{
    // §19 비-부인성 — 승인/배포자 신원은 본문이 아니라 인증 토큰에서 취한다. 본문 ApproverId를 신뢰하면
    // 임의 사용자가 타인 명의로 레시피를 승인할 수 있으므로(무결성·비-부인성 훼손), 토큰 주체로 강제한다.
    private string CurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value ?? string.Empty;

    [HttpGet("recipes")]
    public async Task<IActionResult> GetRecipes([FromQuery] string? equipmentClassId, [FromQuery] string? state, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(state) && Enum.TryParse<NexaOne.RMS.Domain.RecipeApprovalState>(state, out var approvalState))
        {
            var stateResult = await recipeService.GetByStateAsync(approvalState, ct);
            return stateResult.ToActionResult();
        }
        var classId = equipmentClassId ?? string.Empty;
        var result = await recipeService.GetByEquipmentClassAsync(classId, ct);
        return result.ToActionResult();
    }

    [HttpGet("recipes/{recipeId}")]
    public async Task<IActionResult> GetRecipe(string recipeId, CancellationToken ct)
    {
        var result = await recipeService.GetRecipeAsync(recipeId, ct);
        return result.ToActionResult();
    }

    [HttpPost("recipes")]
    [Authorize(Policy = "perm:rms:manage")]   // ADR-003 — 레시피 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    public async Task<IActionResult> CreateRecipe([FromBody] CreateRecipeRequest req, CancellationToken ct)
    {
        var result = await recipeService.CreateRecipeAsync(req.RecipeId, req.Name, req.Description, req.EquipmentClassId, ct);
        return result.ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/request-approval")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> RequestApproval(string recipeId, CancellationToken ct)
    {
        var result = await recipeService.RequestApprovalAsync(recipeId, ct);
        return result.ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/approve1")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> Approve1(string recipeId, CancellationToken ct)
    {
        // 승인자 = 토큰 주체(본문 ApproverId 신뢰 금지, 비-부인성).
        var result = await recipeService.Approve1Async(recipeId, CurrentUserId(), ct);
        return result.ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/approve2")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> Approve2(string recipeId, CancellationToken ct)
    {
        var result = await recipeService.Approve2Async(recipeId, CurrentUserId(), ct);
        return result.ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/release")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> Release(string recipeId, CancellationToken ct)
    {
        var result = await recipeService.ReleaseAsync(recipeId, CurrentUserId(), ct);
        return result.ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/reject")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> Reject(string recipeId, [FromBody] RejectRequest req, CancellationToken ct)
    {
        var result = await recipeService.RejectAsync(recipeId, req.Reason, ct);
        return result.ToActionResult();
    }

    [HttpPost("recipes/{recipeId}/new-version")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> CreateNewVersion(string recipeId, [FromBody] NewVersionRequest req, CancellationToken ct)
    {
        var result = await recipeService.CreateNewVersionAsync(recipeId, req.NewRecipeId, ct);
        return result.ToActionResult();
    }

    // ── Recipe Params ─────────────────────────────────────────────────────────

    [HttpGet("recipes/{recipeId}/params")]
    public async Task<IActionResult> GetParams(string recipeId, CancellationToken ct)
    {
        var list = await recipeService.GetParamsAsync(recipeId, ct);
        return Ok(list);
    }

    [HttpPost("recipes/{recipeId}/params")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> AddParam(string recipeId, [FromBody] AddParamRequest req, CancellationToken ct)
    {
        var result = await recipeService.AddParamAsync(
            req.ParamId, recipeId, req.ParamName, req.ParamValue, req.Unit, req.SortOrder, ct);
        return result.ToActionResult();
    }

    [HttpPut("recipes/params/{paramId}")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> UpdateParam(string paramId, [FromBody] UpdateParamRequest req, CancellationToken ct)
    {
        var result = await recipeService.UpdateParamAsync(paramId, req.NewValue, ct);
        return result.ToActionResult();
    }

    [HttpDelete("recipes/params/{paramId}")]
    [Authorize(Policy = "perm:rms:manage")]
    public async Task<IActionResult> DeleteParam(string paramId, CancellationToken ct)
    {
        var result = await recipeService.DeleteParamAsync(paramId, ct);
        return result.ToActionResult();
    }
}

public record CreateRecipeRequest(string RecipeId, string Name, string Description, string EquipmentClassId);
public record RejectRequest(string Reason);
public record NewVersionRequest(string NewRecipeId);
public record AddParamRequest(string ParamId, string ParamName, string ParamValue, string Unit, int SortOrder);
public record UpdateParamRequest(string NewValue);
