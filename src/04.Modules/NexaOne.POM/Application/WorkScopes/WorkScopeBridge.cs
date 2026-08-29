using NexaOne.Common;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkScopes;

/// <summary>작업 대상 애플리케이션 서비스와 호스트 계약 사이의 얇은 어댑터입니다.</summary>
public sealed class WorkScopeBridge : IWorkScopeBridge
{
    private readonly WorkScopeService _service;

    public WorkScopeBridge(WorkScopeService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public async Task<Result<IReadOnlyList<WorkScopeDto>>> ListAsync(
        string? plantId = null,
        string? scopeType = null,
        string? targetId = null,
        string? parentScopeId = null,
        string? status = null,
        CancellationToken ct = default)
    {
        var result = await _service.ListAsync(plantId, scopeType, targetId, parentScopeId, status, ct);
        return result.IsSuccess
            ? Result.Success<IReadOnlyList<WorkScopeDto>>(result.Value.Select(ToDto).ToList())
            : Result.Failure<IReadOnlyList<WorkScopeDto>>(result.Error);
    }

    public async Task<Result<IReadOnlyList<WorkScopeMemberDto>>> ListMembersAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workScopeId))
            return Result.Failure<IReadOnlyList<WorkScopeMemberDto>>(
                Error.Validation(nameof(workScopeId), "Work scope ID is required."));
        var members = await _service.ListMembersAsync(workScopeId.Trim(), ct);
        return members.IsSuccess
            ? Result.Success<IReadOnlyList<WorkScopeMemberDto>>(
                members.Value.Select(ToDto).ToList())
            : Result.Failure<IReadOnlyList<WorkScopeMemberDto>>(members.Error);
    }

    public async Task<Result<IReadOnlyList<WorkScopeExecutionDto>>> ListExecutionsAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workScopeId))
            return Result.Failure<IReadOnlyList<WorkScopeExecutionDto>>(
                Error.Validation(nameof(workScopeId), "Work scope ID is required."));
        var executions = await _service.ListExecutionsAsync(workScopeId.Trim(), ct);
        return executions.IsSuccess
            ? Result.Success<IReadOnlyList<WorkScopeExecutionDto>>(
                executions.Value.Select(ToDto).ToList())
            : Result.Failure<IReadOnlyList<WorkScopeExecutionDto>>(executions.Error);
    }

    public async Task<Result<WorkScopeDto>> CreateAsync(
        WorkScopeCreateCommand command,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(command.ScopeType))
            return Result.Failure<WorkScopeDto>(Error.Validation(nameof(command.ScopeType), "Scope type is invalid."));
        var result = await _service.CreateAsync(new WorkScopeCreateInput(
            command.WorkScopeId, command.PlantId, ToDomain(command.ScopeType), command.TargetId,
            command.Name, command.ParentScopeId, command.EquipmentId, command.ProductId,
            command.ProcessId, command.RecipeId, command.RecipeVersion, command.PlanQty,
            command.OwnerId, command.Description, command.ActorId ?? "SYSTEM",
            command.WorkOrderId, command.CarrierId, command.IdempotencyKey), ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<WorkScopeDto>(result.Error);
    }

    public async Task<Result<WorkScopeDto>> ExecuteAsync(
        string workScopeId,
        WorkScopeOperationCommand command,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(command.Action))
            return Result.Failure<WorkScopeDto>(Error.Validation(nameof(command.Action), "Work scope action is invalid."));
        var result = await _service.ExecuteAsync(workScopeId, new WorkScopeOperationContext(
            ToDomain(command.Action), command.ActorId ?? "SYSTEM", command.ClientChannel,
            command.IdempotencyKey, command.ExpectedVersion, command.GoodQty, command.DefectQty,
            command.DeviceId, command.Remark, command.CarrierId, command.ResultCode,
            command.ResultMetadataJson), ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<WorkScopeDto>(result.Error);
    }

    private static PomWorkScopeType ToDomain(WorkScopeType value) => value switch
    {
        WorkScopeType.Batch => PomWorkScopeType.Batch,
        WorkScopeType.Campaign => PomWorkScopeType.Campaign,
        WorkScopeType.Carrier => PomWorkScopeType.Carrier,
        WorkScopeType.Lot => PomWorkScopeType.Lot,
        WorkScopeType.Equipment => PomWorkScopeType.Equipment,
        WorkScopeType.Other => PomWorkScopeType.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Scope type is invalid.")
    };

    private static PomWorkScopeAction ToDomain(WorkScopeAction value) => value switch
    {
        WorkScopeAction.Release => PomWorkScopeAction.Release,
        WorkScopeAction.Start => PomWorkScopeAction.Start,
        WorkScopeAction.Report => PomWorkScopeAction.Report,
        WorkScopeAction.Hold => PomWorkScopeAction.Hold,
        WorkScopeAction.ReleaseHold => PomWorkScopeAction.ReleaseHold,
        WorkScopeAction.Complete => PomWorkScopeAction.Complete,
        WorkScopeAction.Cancel => PomWorkScopeAction.Cancel,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Work scope action is invalid.")
    };

    private static WorkScopeDto ToDto(PomWorkScope scope) => new(
        scope.Id, scope.PlantId, scope.ScopeType.ToString(), scope.TargetId, scope.Name,
        scope.ParentScopeId, scope.EquipmentId, scope.ProductId, scope.ProcessId,
        scope.RecipeId, scope.RecipeVersion, scope.PlanQty, scope.StartQty,
        scope.CompleteQty, scope.ScrapQty, scope.OwnerId, scope.Status.ToString(),
        scope.IsHold, scope.StartedAt, scope.CompletedAt, scope.Description,
        scope.VersionNo, scope.CreatedAt, scope.CreatedBy, scope.UpdatedAt, scope.UpdatedBy,
        scope.WorkOrderId, scope.CarrierId);

    private static WorkScopeMemberDto ToDto(PomWorkScopeMember member) => new(
        member.MemberId, member.WorkScopeId, member.MemberScopeId,
        member.MemberType.ToString(), member.MemberTargetId, member.SequenceNo,
        member.CreatedAt);

    private static WorkScopeExecutionDto ToDto(PomWorkScopeExecution execution) => new(
        execution.ExecutionId, execution.WorkScopeId, execution.IdempotencyKey,
        execution.Action.ToString(), execution.FromStatus.ToString(), execution.ToStatus.ToString(),
        execution.GoodQty, execution.DefectQty, execution.UserId, execution.EquipmentId,
        execution.ClientChannel, execution.DeviceId, execution.OccurredAt, execution.Remark,
        execution.ExpectedVersion, execution.ResultVersion, execution.CarrierId,
        execution.ResultCode, execution.ResultMetadataJson);
}
