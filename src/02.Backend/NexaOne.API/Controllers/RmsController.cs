using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.RMS.Application.Rms;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/rms")]
[Authorize]
public class RmsController(RecipeService recipeService) : ControllerBase
{
    [HttpGet("recipes")]
    public async Task<IActionResult> GetRecipes([FromQuery] string? equipmentClassId, [FromQuery] string? state, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(state) && Enum.TryParse<NexaOne.RMS.Domain.RecipeApprovalState>(state, out var approvalState))
        {
            var stateResult = await recipeService.GetByStateAsync(approvalState, ct);
            return stateResult.IsSuccess ? Ok(stateResult.Value) : BadRequest(stateResult.Error);
        }
        var classId = equipmentClassId ?? string.Empty;
        var result = await recipeService.GetByEquipmentClassAsync(classId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("recipes/{recipeId}")]
    public async Task<IActionResult> GetRecipe(string recipeId, CancellationToken ct)
    {
        var result = await recipeService.GetRecipeAsync(recipeId, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost("recipes")]
    public async Task<IActionResult> CreateRecipe([FromBody] CreateRecipeRequest req, CancellationToken ct)
    {
        var result = await recipeService.CreateRecipeAsync(req.RecipeId, req.Name, req.Description, req.EquipmentClassId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("recipes/{recipeId}/request-approval")]
    public async Task<IActionResult> RequestApproval(string recipeId, CancellationToken ct)
    {
        var result = await recipeService.RequestApprovalAsync(recipeId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("recipes/{recipeId}/approve1")]
    public async Task<IActionResult> Approve1(string recipeId, [FromBody] ApproveRequest req, CancellationToken ct)
    {
        var result = await recipeService.Approve1Async(recipeId, req.ApproverId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("recipes/{recipeId}/approve2")]
    public async Task<IActionResult> Approve2(string recipeId, [FromBody] ApproveRequest req, CancellationToken ct)
    {
        var result = await recipeService.Approve2Async(recipeId, req.ApproverId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("recipes/{recipeId}/release")]
    public async Task<IActionResult> Release(string recipeId, [FromBody] ApproveRequest req, CancellationToken ct)
    {
        var result = await recipeService.ReleaseAsync(recipeId, req.ApproverId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("recipes/{recipeId}/reject")]
    public async Task<IActionResult> Reject(string recipeId, [FromBody] RejectRequest req, CancellationToken ct)
    {
        var result = await recipeService.RejectAsync(recipeId, req.Reason, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPost("recipes/{recipeId}/new-version")]
    public async Task<IActionResult> CreateNewVersion(string recipeId, [FromBody] NewVersionRequest req, CancellationToken ct)
    {
        var result = await recipeService.CreateNewVersionAsync(recipeId, req.NewRecipeId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    // ── Recipe Params ─────────────────────────────────────────────────────────

    [HttpGet("recipes/{recipeId}/params")]
    public async Task<IActionResult> GetParams(string recipeId, CancellationToken ct)
    {
        var list = await recipeService.GetParamsAsync(recipeId, ct);
        return Ok(list);
    }

    [HttpPost("recipes/{recipeId}/params")]
    public async Task<IActionResult> AddParam(string recipeId, [FromBody] AddParamRequest req, CancellationToken ct)
    {
        var result = await recipeService.AddParamAsync(
            req.ParamId, recipeId, req.ParamName, req.ParamValue, req.Unit, req.SortOrder, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("recipes/params/{paramId}")]
    public async Task<IActionResult> UpdateParam(string paramId, [FromBody] UpdateParamRequest req, CancellationToken ct)
    {
        var result = await recipeService.UpdateParamAsync(paramId, req.NewValue, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpDelete("recipes/params/{paramId}")]
    public async Task<IActionResult> DeleteParam(string paramId, CancellationToken ct)
    {
        var result = await recipeService.DeleteParamAsync(paramId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }
}

public record CreateRecipeRequest(string RecipeId, string Name, string Description, string EquipmentClassId);
public record ApproveRequest(string ApproverId);
public record RejectRequest(string Reason);
public record NewVersionRequest(string NewRecipeId);
public record AddParamRequest(string ParamId, string ParamName, string ParamValue, string Unit, int SortOrder);
public record UpdateParamRequest(string NewValue);
