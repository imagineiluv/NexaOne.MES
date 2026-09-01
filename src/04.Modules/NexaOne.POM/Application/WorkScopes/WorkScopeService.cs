using System.Data.Common;
using System.Text.Json;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.POM.Application.WorkScopes;

/// <summary>작업 대상 등록과 상태 전이를 조정하는 입력 문맥입니다.</summary>
public sealed record WorkScopeCreateInput(
    string WorkScopeId,
    string PlantId,
    PomWorkScopeType ScopeType,
    string TargetId,
    string Name,
    string? ParentScopeId,
    string? EquipmentId,
    string? ProductId,
    string? ProcessId,
    string? RecipeId,
    int? RecipeVersion,
    decimal? PlanQty,
    string? OwnerId,
    string? Description,
    string User,
    string? WorkOrderId = null,
    string? CarrierId = null,
    string? IdempotencyKey = null);

public sealed record WorkScopeOperationContext(
    PomWorkScopeAction Action,
    string User,
    string ClientChannel,
    string IdempotencyKey,
    int ExpectedVersion,
    decimal? GoodQty = null,
    decimal? DefectQty = null,
    string? DeviceId = null,
    string? Remark = null,
    string? CarrierId = null,
    string? ResultCode = null,
    string? ResultMetadataJson = null);

/// <summary>
/// Batch/Campaign 그룹과 Carrier/Lot/Other 실행 대상을 같은 lifecycle로 관리합니다.
/// 부모 관계의 규칙과 멱등·낙관적 동시성은 이 모듈에 숨기고 호출자는 작은 계약만 사용합니다.
/// </summary>
public sealed class WorkScopeService
{
    private readonly IWorkScopeRepository _repository;
    private readonly IEquipmentOutputMasterDirectory? _masterDirectory;

    public WorkScopeService(
        IWorkScopeRepository repository,
        IEquipmentOutputMasterDirectory? masterDirectory = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _masterDirectory = masterDirectory;
    }

    public async Task<Result<IReadOnlyList<PomWorkScope>>> ListAsync(
        string? plantId,
        string? scopeType,
        string? targetId,
        string? parentScopeId,
        string? status,
        CancellationToken ct = default)
    {
        var parsedScope = ParseScopeType(scopeType);
        if (parsedScope.IsFailure)
            return Result.Failure<IReadOnlyList<PomWorkScope>>(parsedScope.Error);
        var parsedStatus = ParseStatus(status);
        if (parsedStatus.IsFailure)
            return Result.Failure<IReadOnlyList<PomWorkScope>>(parsedStatus.Error);

        var normalizedPlant = Text(plantId);
        var normalizedTarget = Text(targetId);
        var normalizedParent = Text(parentScopeId);
        if (normalizedPlant?.Length > PomStorageBoundary.IdentifierLength)
            return Result.Failure<IReadOnlyList<PomWorkScope>>(
                Error.Validation(nameof(plantId), "Plant ID is too long."));
        if (normalizedTarget?.Length > 100)
            return Result.Failure<IReadOnlyList<PomWorkScope>>(
                Error.Validation(nameof(targetId), "Target ID is too long."));
        if (normalizedParent?.Length > PomStorageBoundary.IdentifierLength)
            return Result.Failure<IReadOnlyList<PomWorkScope>>(
                Error.Validation(nameof(parentScopeId), "Parent scope ID is too long."));

        return Result.Success(await _repository.ListAsync(
            normalizedPlant, parsedScope.Value, normalizedTarget, normalizedParent, parsedStatus.Value, ct));
    }

    public async Task<Result<IReadOnlyList<PomWorkScopeMember>>> ListMembersAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        var id = workScopeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || id.Length > PomStorageBoundary.IdentifierLength)
            return Result.Failure<IReadOnlyList<PomWorkScopeMember>>(
                Error.Validation(nameof(workScopeId), "Work scope ID is required and cannot exceed 50 characters."));
        if (await _repository.GetByIdAsync(id, ct) is null)
            return Result.Failure<IReadOnlyList<PomWorkScopeMember>>(
                Error.NotFoundOf(nameof(PomWorkScope), id));
        return Result.Success(await _repository.ListMembersAsync(id, ct));
    }

    public async Task<Result<IReadOnlyList<PomWorkScopeExecution>>> ListExecutionsAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        var id = workScopeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || id.Length > PomStorageBoundary.IdentifierLength)
            return Result.Failure<IReadOnlyList<PomWorkScopeExecution>>(
                Error.Validation(nameof(workScopeId), "Work scope ID is required and cannot exceed 50 characters."));
        if (await _repository.GetByIdAsync(id, ct) is null)
            return Result.Failure<IReadOnlyList<PomWorkScopeExecution>>(
                Error.NotFoundOf(nameof(PomWorkScope), id));
        return Result.Success(await _repository.ListExecutionsAsync(id, ct));
    }

    public async Task<Result<PomWorkScope>> CreateAsync(
        WorkScopeCreateInput input,
        CancellationToken ct = default)
    {
        if (input is null)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(input), "Create input is required."));
        var actor = User(input.User);
        if (actor.Length > PomStorageBoundary.ActorLength)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(input.User), "User cannot exceed 50 characters."));
        if (!Enum.IsDefined(input.ScopeType))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(input.ScopeType), "Scope type is invalid."));
        if (string.IsNullOrWhiteSpace(input.WorkScopeId)
            || input.WorkScopeId.Trim().Length > PomStorageBoundary.IdentifierLength)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(input.WorkScopeId), "Work scope ID is required and cannot exceed 50 characters."));
        if (string.IsNullOrWhiteSpace(input.PlantId)
            || input.PlantId.Trim().Length > PomStorageBoundary.IdentifierLength)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(input.PlantId), "Plant ID is required and cannot exceed 50 characters."));
        if (string.IsNullOrWhiteSpace(input.TargetId) || input.TargetId.Trim().Length > 100)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(input.TargetId), "Target ID is required and cannot exceed 100 characters."));
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(input.Name), "Name is required and cannot exceed 200 characters."));

        var id = input.WorkScopeId?.Trim() ?? string.Empty;
        var idempotencyKey = Text(input.IdempotencyKey) ?? $"work-scope:create:{id}";
        if (idempotencyKey.Length > 100)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(input.IdempotencyKey), "Idempotency key cannot exceed 100 characters."));
        var requestHash = CanonicalRequestHash.Compute(
            id, input.PlantId?.Trim(), input.ScopeType.ToString(), input.TargetId?.Trim(),
            input.Name?.Trim(), Text(input.ParentScopeId), Text(input.WorkOrderId), Text(input.CarrierId),
            Text(input.EquipmentId), Text(input.ProductId), Text(input.ProcessId), Text(input.RecipeId),
            input.RecipeVersion, input.PlanQty, Text(input.OwnerId), Text(input.Description), actor);
        var replay = await _repository.GetByIdempotencyKeyAsync(idempotencyKey, ct);
        if (replay is not null)
        {
            return string.Equals(replay.CreateRequestHash, requestHash, StringComparison.Ordinal)
                ? Result.Success(replay)
                : Result.Failure<PomWorkScope>(Error.Conflict(
                    $"Idempotency key '{idempotencyKey}' was already used for a different work-scope."));
        }
        if (await _repository.GetByIdAsync(id, ct) is not null)
            return Result.Failure<PomWorkScope>(Error.Conflict(
                $"Work scope '{id}' already exists."));

        var parent = await ValidateParentAsync(input, ct);
        if (parent.IsFailure)
            return Result.Failure<PomWorkScope>(parent.Error);

        var created = PomWorkScope.Create(
            id, input.PlantId!.Trim(), input.ScopeType, input.TargetId!.Trim(), input.Name,
            input.ParentScopeId, input.EquipmentId, input.ProductId, input.ProcessId,
            input.RecipeId, input.RecipeVersion, input.PlanQty, input.OwnerId,
            input.Description, actor, input.WorkOrderId, input.CarrierId);
        if (created.IsFailure) return created;

        // Carrier cleaning is a physical equipment operation. When the host supplies the
        // MDM master directory, validate both sides of the equipment/carrier attribution
        // before the scope can become visible. Batch/Campaign planning can remain equipment-
        // agnostic, and older in-memory callers without the optional directory still work.
        if (created.Value.ScopeType == PomWorkScopeType.Carrier
            && _masterDirectory is not null
            && !string.IsNullOrWhiteSpace(created.Value.EquipmentId))
        {
            var master = await _masterDirectory.GetScopeAsync(
                created.Value.EquipmentId!, created.Value.CarrierId, ct);
            if (master is null || !master.IsEquipmentValid)
                return Result.Failure<PomWorkScope>(Error.Validation(
                    nameof(input.EquipmentId), "Carrier work scope requires an active equipment master."));
            if (!string.Equals(master.PlantId, created.Value.PlantId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<PomWorkScope>(Error.Conflict(
                    "Carrier work scope equipment must belong to the requested plant."));
            if (!master.CarrierExists)
                return Result.Failure<PomWorkScope>(Error.Validation(
                    nameof(input.CarrierId), "CarrierId does not reference a carrier master."));
        }
        created.Value.SetCreateIdentity(idempotencyKey, requestHash);

        try
        {
            await _repository.AddAsync(created.Value, ct);
        }
        catch (DbException)
        {
            var winner = await _repository.GetByIdempotencyKeyAsync(idempotencyKey, ct);
            if (winner is not null && string.Equals(winner.CreateRequestHash, requestHash, StringComparison.Ordinal))
                return Result.Success(winner);
            throw;
        }
        return created;
    }

    public async Task<Result<PomWorkScope>> ExecuteAsync(
        string workScopeId,
        WorkScopeOperationContext context,
        CancellationToken ct = default)
    {
        if (context is null)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(context), "Operation context is required."));
        var id = workScopeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(workScopeId), "Work scope ID is required."));
        if (!Enum.IsDefined(context.Action))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(context.Action), "Work scope action is invalid."));
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || context.IdempotencyKey.Trim().Length > 100)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(context.IdempotencyKey), "A non-empty idempotency key up to 100 characters is required."));
        if (context.ExpectedVersion < 1)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(context.ExpectedVersion), "Expected version must be at least 1."));

        var channel = context.ClientChannel?.Trim().ToUpperInvariant() ?? string.Empty;
        if (channel is not ("MES" or "MOBILE" or "POP"))
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(context.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        var actor = User(context.User);
        if (actor.Length > PomStorageBoundary.ActorLength)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(context.User), "User cannot exceed 50 characters."));
        var deviceId = Text(context.DeviceId);
        if (deviceId?.Length > PomStorageBoundary.DeviceIdLength)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(context.DeviceId), "Device ID is too long."));
        var remark = Text(context.Remark);
        if (remark?.Length > PomStorageBoundary.ReasonLength)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(context.Remark), "Remark is too long."));

        var scope = await _repository.GetByIdAsync(id, ct);
        if (scope is null)
            return Result.Failure<PomWorkScope>(Error.NotFoundOf(nameof(PomWorkScope), id));

        var carrierId = Text(context.CarrierId) ?? scope.CarrierId;
        if (carrierId?.Length > 100)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(context.CarrierId), "Carrier ID is too long."));
        if (scope.ScopeType == PomWorkScopeType.Carrier
            && carrierId is not null
            && !string.Equals(carrierId, scope.TargetId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(context.CarrierId), "Carrier ID must match the carrier scope target."));
        var resultCode = Text(context.ResultCode);
        if (resultCode?.Length > 50)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(context.ResultCode), "Result code is too long."));
        var resultMetadata = Text(context.ResultMetadataJson);
        if (resultMetadata?.Length > 4000)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(context.ResultMetadataJson), "Result metadata cannot exceed 4000 characters."));
        if (resultMetadata is not null)
        {
            try { using var _ = JsonDocument.Parse(resultMetadata); }
            catch (JsonException) { return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(context.ResultMetadataJson), "Result metadata must be valid JSON.")); }
        }

        var key = context.IdempotencyKey.Trim();
        var prior = await _repository.GetExecutionByIdempotencyKeyAsync(key, ct);
        if (prior is not null)
        {
            return SameRequest(prior, scope.Id, context, actor, channel, deviceId, remark, carrierId, resultCode, resultMetadata)
                ? Result.Success(scope)
                : Result.Failure<PomWorkScope>(Error.Conflict(
                    $"Idempotency key '{key}' was already used for a different work-scope operation."));
        }
        if (scope.VersionNo != context.ExpectedVersion)
            return Result.Failure<PomWorkScope>(Error.Conflict(
                $"Work scope was changed concurrently. Current version: {scope.VersionNo}."));

        var parentGuard = await ValidateParentExecutionAsync(scope, context.Action, ct);
        if (parentGuard.IsFailure)
            return Result.Failure<PomWorkScope>(parentGuard.Error);

        var transition = Apply(scope, context, actor);
        if (transition.Result.IsFailure)
            return Result.Failure<PomWorkScope>(transition.Result.Error);

        var execution = new PomWorkScopeExecution(
            Guid.NewGuid().ToString("N"), scope.Id, key, context.Action,
            transition.From, scope.Status, context.GoodQty, context.DefectQty,
            actor, scope.EquipmentId, channel, deviceId, DateTime.UtcNow, remark,
            context.ExpectedVersion, context.ExpectedVersion + 1, carrierId, resultCode, resultMetadata);
        var write = await _repository.UpdateWithExecutionAsync(scope, execution, ct);
        if (write.Kind == WorkScopeWriteKind.Applied)
            return Result.Success(scope);

        if (write.Kind == WorkScopeWriteKind.ProjectionOwned)
        {
            return Result.Failure<PomWorkScope>(Error.Conflict(
                "POM.WorkScope.ProjectionOwned",
                $"Work scope '{scope.Id}' is owned by an equipment projection and cannot be changed through ordinary commands."));
        }

        var winner = await _repository.GetExecutionByIdempotencyKeyAsync(key, ct);
        if (winner is not null && SameRequest(winner, scope.Id, context, actor, channel, deviceId, remark, carrierId, resultCode, resultMetadata))
        {
            var current = await _repository.GetByIdAsync(scope.Id, ct);
            if (current is not null) return Result.Success(current);
        }
        return Result.Failure<PomWorkScope>(Error.Conflict(
            "Work scope was changed concurrently. Reload and retry."));
    }

    private async Task<Result> ValidateParentAsync(WorkScopeCreateInput input, CancellationToken ct)
    {
        var parentId = Text(input.ParentScopeId);
        if (input.ScopeType == PomWorkScopeType.Campaign && parentId is not null)
            return Result.Failure(Error.Validation(
                nameof(input.ParentScopeId), "A campaign cannot be nested under another scope."));
        if (parentId is null)
            return Result.Success();
        if (string.Equals(parentId, input.WorkScopeId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Validation(
                nameof(input.ParentScopeId), "A work scope cannot be its own parent."));

        var parent = await _repository.GetByIdAsync(parentId, ct);
        if (parent is null)
            return Result.Failure(Error.NotFoundOf(nameof(PomWorkScope), parentId));
        if (!string.Equals(parent.PlantId, input.PlantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Conflict(
                "A parent work scope must belong to the same plant."));
        if (parent.Status is PomWorkScopeStatus.Started
            or PomWorkScopeStatus.Completed
            or PomWorkScopeStatus.Cancelled)
            return Result.Failure(Error.Conflict(
                "A child scope cannot be added after its parent has started or reached a terminal state."));
        if (parent.IsHold)
            return Result.Failure(Error.Conflict(
                "A child scope cannot be added while its parent is held."));

        var allowed = parent.ScopeType switch
        {
            PomWorkScopeType.Campaign => input.ScopeType is PomWorkScopeType.Batch
                or PomWorkScopeType.Carrier or PomWorkScopeType.Lot or PomWorkScopeType.Other,
            PomWorkScopeType.Batch => input.ScopeType is PomWorkScopeType.Carrier
                or PomWorkScopeType.Lot or PomWorkScopeType.Other,
            _ => false
        };
        return allowed
            ? Result.Success()
            : Result.Failure(Error.Validation(
                nameof(input.ParentScopeId),
                $"A {input.ScopeType} scope cannot be nested under a {parent.ScopeType} scope."));
    }

    private static (PomWorkScopeStatus From, Result Result) Apply(
        PomWorkScope scope,
        WorkScopeOperationContext context,
        string actor)
    {
        var from = scope.Status;
        var result = context.Action switch
        {
            PomWorkScopeAction.Release => scope.Release(actor),
            PomWorkScopeAction.Start => scope.Start(DateTime.UtcNow, actor),
            PomWorkScopeAction.Report => scope.Report(context.GoodQty ?? -1m, context.DefectQty ?? -1m, actor),
            PomWorkScopeAction.Hold => scope.Hold(actor),
            PomWorkScopeAction.ReleaseHold => scope.ReleaseHold(actor),
            PomWorkScopeAction.Complete => scope.Complete(
                context.GoodQty ?? -1m, context.DefectQty ?? -1m, DateTime.UtcNow, actor),
            PomWorkScopeAction.Cancel => scope.Cancel(actor),
            _ => Result.Failure(Error.Validation(nameof(context.Action), "Work scope action is invalid."))
        };
        return (from, result);
    }

    private static bool SameRequest(
        PomWorkScopeExecution execution,
        string workScopeId,
        WorkScopeOperationContext context,
        string actor,
        string channel,
        string? deviceId,
        string? remark,
        string? carrierId,
        string? resultCode,
        string? resultMetadata)
        => string.Equals(execution.WorkScopeId, workScopeId, StringComparison.OrdinalIgnoreCase)
           && execution.Action == context.Action
           && execution.GoodQty == context.GoodQty
           && execution.DefectQty == context.DefectQty
           && string.Equals(execution.UserId, actor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(execution.ClientChannel, channel, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Text(execution.DeviceId), deviceId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Text(execution.Remark), remark, StringComparison.Ordinal)
           && string.Equals(Text(execution.CarrierId), carrierId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Text(execution.ResultCode), resultCode, StringComparison.Ordinal)
           && string.Equals(Text(execution.ResultMetadataJson), resultMetadata, StringComparison.Ordinal)
           && execution.ExpectedVersion == context.ExpectedVersion
           && execution.ResultVersion == context.ExpectedVersion + 1;

    private static Result<PomWorkScopeType?> ParseScopeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Result.Success<PomWorkScopeType?>(null);
        return Enum.TryParse<PomWorkScopeType>(value.Trim(), true, out var parsed) && Enum.IsDefined(parsed)
            ? Result.Success<PomWorkScopeType?>(parsed)
            : Result.Failure<PomWorkScopeType?>(Error.Validation(nameof(value), "Scope type must be Batch, Campaign, Carrier, Lot, Equipment, or Other."));
    }

    private static Result<PomWorkScopeStatus?> ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Result.Success<PomWorkScopeStatus?>(null);
        return Enum.TryParse<PomWorkScopeStatus>(value.Trim(), true, out var parsed) && Enum.IsDefined(parsed)
            ? Result.Success<PomWorkScopeStatus?>(parsed)
            : Result.Failure<PomWorkScopeStatus?>(Error.Validation(nameof(value), "Status is invalid."));
    }

    private static string User(string? value) => string.IsNullOrWhiteSpace(value) ? "SYSTEM" : value.Trim();
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<Result> ValidateParentExecutionAsync(
        PomWorkScope scope,
        PomWorkScopeAction action,
        CancellationToken ct)
    {
        if (scope.ParentScopeId is null) return Result.Success();
        var parent = await _repository.GetByIdAsync(scope.ParentScopeId, ct);
        if (parent is null)
            return Result.Failure(Error.NotFoundOf(nameof(PomWorkScope), scope.ParentScopeId));
        if (parent.Status is PomWorkScopeStatus.Completed or PomWorkScopeStatus.Cancelled)
            return Result.Failure(Error.Conflict("A child work scope cannot execute under a terminal parent."));
        if (parent.IsHold && action is PomWorkScopeAction.Start or PomWorkScopeAction.Report or PomWorkScopeAction.Complete)
            return Result.Failure(Error.Conflict("A child work scope cannot execute while its parent is held."));
        return Result.Success();
    }
}
