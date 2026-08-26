using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.Server.Gateway;

/// <summary>RMS 공통 assignment/실행 스냅샷 API. PLC mapping과 실제 다운로드는 프로젝트 플러그인 경계에 둔다.</summary>
[ApiController]
[Route("api/v1/rms")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class RmsRecipeExecutionController : ControllerBase
{
    private readonly IRecipeExecutionBridge _bridge;

    public RmsRecipeExecutionController(IRecipeExecutionBridge bridge) => _bridge = bridge;

    [HttpPost("assignments")]
    [RequirePermission(Permissions.RmsManage)]
    [ProducesResponseType<RecipeAssignmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Assign(
        [FromBody] RecipeAssignmentCommand command, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.AssignAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }

    [HttpGet("assignments")]
    [RequirePermission(Permissions.RmsRead)]
    [ProducesResponseType<IReadOnlyList<RecipeAssignmentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAssignments(
        [FromQuery] string? equipmentId,
        [FromQuery] string? equipmentClassId,
        [FromQuery] bool activeOnly = true,
        CancellationToken ct = default)
        => Ok(await _bridge.GetAssignmentsAsync(equipmentId, equipmentClassId, activeOnly, ct));

    [HttpPost("executions")]
    [RequirePermission(Permissions.RmsManage)]
    [ProducesResponseType<RecipeExecutionSnapshotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecordExecution(
        [FromBody] RecipeExecutionCommand command, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        var headerKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
        return (await _bridge.RecordExecutionAsync(command with
        {
            IdempotencyKey = string.IsNullOrWhiteSpace(headerKey)
                ? command.IdempotencyKey
                : headerKey,
            ActorId = actor,
        }, ct)).ToActionResult();
    }

    [HttpGet("executions/{executionId}")]
    [RequirePermission(Permissions.RmsRead)]
    [ProducesResponseType<RecipeExecutionSnapshotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetExecution(string executionId, CancellationToken ct)
        => (await _bridge.GetExecutionAsync(executionId, ct)).ToActionResult();

    private bool TryGetExternalActor(out string actor)
    {
        actor = User.CurrentUserId()?.Trim() ?? string.Empty;
        return actor.Length > 0;
    }
}
