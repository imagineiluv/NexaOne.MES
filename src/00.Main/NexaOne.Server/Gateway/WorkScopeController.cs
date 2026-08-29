using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 생산 W/O가 없는 Carrier 세척과 Batch/Campaign 그룹 작업을 위한 작업 대상 API입니다.
/// 기존 <c>/api/v1/pom/work-orders</c>와 분리해 작업 대상이 생산 LOT나 W/O에 종속되지 않도록 합니다.
/// </summary>
[ApiController]
[Route("api/v1/pom/work-scopes")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class WorkScopeController : ControllerBase
{
    private readonly IWorkScopeBridge _bridge;

    public WorkScopeController(IWorkScopeBridge bridge)
        => _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    /// <summary>작업 대상과 현재 실행 상태를 조회합니다.</summary>
    [HttpGet]
    [RequirePermission(Permissions.PomRead)]
    [ProducesResponseType<IReadOnlyList<WorkScopeDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? plantId,
        [FromQuery] string? scopeType,
        [FromQuery] string? targetId,
        [FromQuery] string? parentScopeId,
        [FromQuery] string? status,
        CancellationToken ct)
        => (await _bridge.ListAsync(plantId, scopeType, targetId, parentScopeId, status, ct)).ToActionResult();

    /// <summary>Batch/Campaign에 편성된 하위 작업 대상을 순서대로 조회합니다.</summary>
    [HttpGet("{id}/members")]
    [RequirePermission(Permissions.PomRead)]
    [ProducesResponseType<IReadOnlyList<WorkScopeMemberDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMembers(string id, CancellationToken ct)
        => (await _bridge.ListMembersAsync(id, ct)).ToActionResult();

    /// <summary>작업 대상의 상태 전이·Carrier 세척 결과 이력을 조회합니다.</summary>
    [HttpGet("{id}/executions")]
    [RequirePermission(Permissions.PomRead)]
    [ProducesResponseType<IReadOnlyList<WorkScopeExecutionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListExecutions(string id, CancellationToken ct)
        => (await _bridge.ListExecutionsAsync(id, ct)).ToActionResult();

    /// <summary>생산 W/O 없이도 실행 가능한 작업 대상을 등록합니다.</summary>
    [HttpPost]
    [RequirePermission(Permissions.PomManage)]
    [ProducesResponseType<WorkScopeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(
        [FromBody] WorkScopeCreateCommand command,
        CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CreateAsync(command with { ActorId = actor }, ct)).ToActionResult();
    }

    /// <summary>
    /// 작업 대상의 상태를 전이합니다. <paramref name="actionName"/>은 release, start, report,
    /// hold, release-hold(resume), complete, cancel 중 하나이며 실행 기록은 멱등 키로 중복을 차단합니다.
    /// </summary>
    [HttpPost("{id}/{actionName}")]
    [RequirePermission(Permissions.PomExecute)]
    [ProducesResponseType<WorkScopeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Execute(
        string id,
        string actionName,
        [FromBody] WorkScopeOperationRequest request,
        CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (!TryParseAction(actionName, out var action))
            return Result.Failure<WorkScopeDto>(Error.Validation(
                nameof(actionName), "Action must be release, start, report, hold, release-hold, resume, complete, or cancel.")).ToActionResult();

        return (await _bridge.ExecuteAsync(id, new WorkScopeOperationCommand(
            action, request.IdempotencyKey, request.ExpectedVersion, request.GoodQty,
            request.DefectQty, request.ClientChannel, request.DeviceId, request.Remark, actor,
            request.CarrierId, request.ResultCode, request.ResultMetadataJson), ct))
            .ToActionResult();
    }

    private static bool TryParseAction(string value, out WorkScopeAction action)
    {
        action = value.Trim().ToLowerInvariant() switch
        {
            "release" => WorkScopeAction.Release,
            "start" => WorkScopeAction.Start,
            "report" => WorkScopeAction.Report,
            "hold" => WorkScopeAction.Hold,
            "release-hold" or "releasehold" or "resume" => WorkScopeAction.ReleaseHold,
            "complete" => WorkScopeAction.Complete,
            "cancel" => WorkScopeAction.Cancel,
            _ => default
        };
        return value.Trim().Length > 0 &&
               (action != default || value.Trim().Equals("release", StringComparison.OrdinalIgnoreCase));
    }

    // 상태 변경은 반드시 외부 작업자 JWT를 요구한다. SYSTEM 대체는 책임 추적성을 훼손한다.
    private bool TryGetExternalActor(out string actor)
    {
        actor = User.CurrentUserId()?.Trim() ?? string.Empty;
        return actor.Length > 0;
    }
}

/// <summary>작업 대상 상태 전이 요청입니다. 수량은 증분이 아닌 현재 절대 누계입니다.</summary>
public sealed record WorkScopeOperationRequest(
    string IdempotencyKey,
    int ExpectedVersion,
    decimal? GoodQty = null,
    decimal? DefectQty = null,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? Remark = null,
    string? CarrierId = null,
    string? ResultCode = null,
    string? ResultMetadataJson = null);
