using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>
/// 현장 라우팅의 선후행 통제 수준이다. 어떤 모드에서도 Hold, 종료 상태, 설비·Recipe·품질 같은
/// 절대 불변식은 완화하지 않으며, 공정 순서 이탈을 처리하는 방법만 달라진다.
/// </summary>
public enum RoutingControlMode
{
    Strict,
    Flexible,
    NoControl
}

/// <summary>정상 순서를 벗어나 LOT의 현재 공정을 변경하는 업무 유형이다.</summary>
public enum RouteDeviationType
{
    Normal,
    Bypass,
    Alternative,
    SequenceChange,
    Rework,
    Return
}

/// <summary>라우팅 정책 평가 결과를 UI와 API가 구조적으로 해석할 수 있게 하는 판정 종류다.</summary>
public enum RoutingDecisionKind
{
    Allow,
    AllowWithWarning,
    ApprovalRequired,
    Block
}

/// <summary>
/// 라우팅 정책 판정 결과다. 화면은 문자열 메시지에 의존하지 않고 <see cref="Kind"/>와
/// <see cref="Code"/>로 차단·경고·승인 요청 UX를 선택한다.
/// </summary>
public sealed record RoutingPolicyDecision(
    RoutingDecisionKind Kind,
    string Code,
    string Message,
    RoutingControlMode ControlMode,
    RouteDeviationType DeviationType,
    int FromStepIndex,
    int ToStepIndex,
    bool RequiresReason,
    string? ExceptionId = null)
{
    public bool IsAllowed => Kind is RoutingDecisionKind.Allow or RoutingDecisionKind.AllowWithWarning;
}

/// <summary>유연 라우팅 예외 요청의 상태다. Approved 요청은 적용과 동시에 Applied로 한 번만 소비된다.</summary>
public enum RouteExceptionStatus
{
    Requested,
    Approved,
    Rejected,
    Applied,
    Expired
}

/// <summary>
/// 공정 순서 이탈에 대한 승인 원장이다. 요청 당시 LOT 버전과 출발·도착 공정에 바인딩되어
/// LOT이 변경되거나 다른 공정으로 재사용되는 것을 방지한다.
/// </summary>
public sealed class RouteExceptionRequest
{
    public const int MaxIdLength = PomStorageBoundary.IdentifierLength;
    public const int MaxReasonLength = PomStorageBoundary.ReasonLength;
    public const int MaxDeviceIdLength = PomStorageBoundary.DeviceIdLength;
    public const int MaxActorLength = PomStorageBoundary.ActorLength;

    private RouteExceptionRequest(string exceptionId) => Id = exceptionId;

    public string Id { get; }
    public string LotId { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;
    public RouteDeviationType DeviationType { get; private set; }
    public int FromStepIndex { get; private set; }
    public int ToStepIndex { get; private set; }
    public string FromProcessId { get; private set; } = string.Empty;
    public string ToProcessId { get; private set; } = string.Empty;
    public int BoundLotVersion { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public RouteExceptionStatus Status { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public string? ReviewedBy { get; private set; }
    public DateTime? ReviewedAt { get; private set; }
    public string? ReviewReason { get; private set; }
    public string? ReviewClientChannel { get; private set; }
    public string? ReviewDeviceId { get; private set; }
    public string? AppliedBy { get; private set; }
    public DateTime? AppliedAt { get; private set; }
    public string? AppliedExecutionId { get; private set; }
    public string ClientChannel { get; private set; } = "MES";
    public string? DeviceId { get; private set; }

    /// <summary>필수 사유와 유효기간을 검증해 LOT 버전에 묶인 승인 요청을 만든다.</summary>
    public static Result<RouteExceptionRequest> Request(
        string exceptionId,
        string lotId,
        string plantId,
        RouteDeviationType deviationType,
        int fromStepIndex,
        int toStepIndex,
        string fromProcessId,
        string toProcessId,
        int boundLotVersion,
        string reason,
        string requestedBy,
        DateTime requestedAt,
        DateTime expiresAt,
        string clientChannel = "MES",
        string? deviceId = null)
    {
        if (string.IsNullOrWhiteSpace(exceptionId))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(exceptionId), "Exception ID is required."));
        if (exceptionId.Trim().Length > MaxIdLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(exceptionId), $"Exception ID cannot exceed {MaxIdLength} characters."));
        if (string.IsNullOrWhiteSpace(lotId))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(lotId), "Lot ID is required."));
        if (lotId.Trim().Length > MaxIdLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(lotId), $"Lot ID cannot exceed {MaxIdLength} characters."));
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(plantId), "Plant ID is required."));
        if (plantId.Trim().Length > MaxIdLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(plantId), $"Plant ID cannot exceed {MaxIdLength} characters."));
        if (deviationType is RouteDeviationType.Normal or RouteDeviationType.Return)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(deviationType), "Normal routing and automatic rework Return cannot be approved as exceptions."));
        if (fromStepIndex < 0 || toStepIndex < 0)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(toStepIndex), "Route step index cannot be negative."));
        if (string.IsNullOrWhiteSpace(fromProcessId) || string.IsNullOrWhiteSpace(toProcessId))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(toProcessId), "From and target process IDs are required."));
        if (fromProcessId.Trim().Length > Lot.MaxProcessIdLength ||
            toProcessId.Trim().Length > Lot.MaxProcessIdLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(toProcessId), $"Process ID cannot exceed {Lot.MaxProcessIdLength} characters."));
        if (boundLotVersion < 1)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(boundLotVersion), "Bound lot version must be at least 1."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(reason), "A route exception reason is required."));
        if (reason.Trim().Length > MaxReasonLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(reason), $"Route exception reason cannot exceed {MaxReasonLength} characters."));
        if (string.IsNullOrWhiteSpace(requestedBy))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(requestedBy), "Requester is required."));
        if (requestedBy.Trim().Length > MaxActorLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(requestedBy), $"Requester cannot exceed {MaxActorLength} characters."));
        if (expiresAt <= requestedAt)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(nameof(expiresAt), "Exception expiry must be after the request time."));

        var channel = NormalizeChannel(clientChannel);
        if (channel is null)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        if (Trimmed(deviceId)?.Length > MaxDeviceIdLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(deviceId), $"Device ID cannot exceed {MaxDeviceIdLength} characters."));

        return Result.Success(new RouteExceptionRequest(exceptionId.Trim())
        {
            LotId = lotId.Trim(),
            PlantId = plantId.Trim(),
            DeviationType = deviationType,
            FromStepIndex = fromStepIndex,
            ToStepIndex = toStepIndex,
            FromProcessId = fromProcessId.Trim(),
            ToProcessId = toProcessId.Trim(),
            BoundLotVersion = boundLotVersion,
            Reason = reason.Trim(),
            Status = RouteExceptionStatus.Requested,
            RequestedBy = requestedBy.Trim(),
            RequestedAt = requestedAt,
            ExpiresAt = expiresAt,
            ClientChannel = channel,
            DeviceId = Trimmed(deviceId)
        });
    }

    /// <summary>DB 행을 상태 전이 검증 없이 복원한다.</summary>
    public static RouteExceptionRequest Restore(
        string exceptionId,
        string lotId,
        string plantId,
        RouteDeviationType deviationType,
        int fromStepIndex,
        int toStepIndex,
        string fromProcessId,
        string toProcessId,
        int boundLotVersion,
        string reason,
        RouteExceptionStatus status,
        string requestedBy,
        DateTime requestedAt,
        DateTime expiresAt,
        string? reviewedBy,
        DateTime? reviewedAt,
        string? reviewReason,
        string? reviewClientChannel,
        string? reviewDeviceId,
        string? appliedBy,
        DateTime? appliedAt,
        string? appliedExecutionId,
        string clientChannel,
        string? deviceId) => new(exceptionId)
    {
        LotId = lotId,
        PlantId = plantId,
        DeviationType = deviationType,
        FromStepIndex = fromStepIndex,
        ToStepIndex = toStepIndex,
        FromProcessId = fromProcessId,
        ToProcessId = toProcessId,
        BoundLotVersion = boundLotVersion,
        Reason = reason,
        Status = status,
        RequestedBy = requestedBy,
        RequestedAt = requestedAt,
        ExpiresAt = expiresAt,
        ReviewedBy = reviewedBy,
        ReviewedAt = reviewedAt,
        ReviewReason = reviewReason,
        ReviewClientChannel = reviewClientChannel,
        ReviewDeviceId = reviewDeviceId,
        AppliedBy = appliedBy,
        AppliedAt = appliedAt,
        AppliedExecutionId = appliedExecutionId,
        ClientChannel = clientChannel,
        DeviceId = deviceId
    };

    /// <summary>요청자와 다른 승인자가 유효기간 안의 요청을 승인한다.</summary>
    public Result Approve(
        string approver, string? reviewReason, DateTime reviewedAt,
        string clientChannel = "MES", string? deviceId = null)
    {
        if (string.IsNullOrWhiteSpace(approver))
            return Result.Failure(Error.Validation(nameof(approver), "Approver is required."));
        if (approver.Trim().Length > MaxActorLength)
            return Result.Failure(Error.Validation(
                nameof(approver), $"Approver cannot exceed {MaxActorLength} characters."));
        if (string.Equals(RequestedBy, approver.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Conflict("The requester cannot approve their own route exception."));
        if (Status != RouteExceptionStatus.Requested)
            return Result.Failure(Error.Conflict($"Only a requested route exception can be approved. Current status: {Status}."));
        if (reviewedAt >= ExpiresAt)
        {
            MarkExpired(reviewedAt);
            return Result.Failure(Error.Conflict("The route exception has expired."));
        }
        if (Trimmed(reviewReason)?.Length > MaxReasonLength)
            return Result.Failure(Error.Validation(
                nameof(reviewReason), $"Review reason cannot exceed {MaxReasonLength} characters."));
        var reviewChannel = NormalizeChannel(clientChannel);
        if (reviewChannel is null)
            return Result.Failure(Error.Validation(
                nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        if (Trimmed(deviceId)?.Length > MaxDeviceIdLength)
            return Result.Failure(Error.Validation(
                nameof(deviceId), $"Device ID cannot exceed {MaxDeviceIdLength} characters."));

        Status = RouteExceptionStatus.Approved;
        ReviewedBy = approver.Trim();
        ReviewedAt = reviewedAt;
        ReviewReason = Trimmed(reviewReason);
        ReviewClientChannel = reviewChannel;
        ReviewDeviceId = Trimmed(deviceId);
        return Result.Success();
    }

    /// <summary>대기 중인 요청을 반려하고 반려 사유를 기록한다.</summary>
    public Result Reject(
        string reviewer, string reason, DateTime reviewedAt,
        string clientChannel = "MES", string? deviceId = null)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
            return Result.Failure(Error.Validation(nameof(reviewer), "Reviewer is required."));
        if (reviewer.Trim().Length > MaxActorLength)
            return Result.Failure(Error.Validation(
                nameof(reviewer), $"Reviewer cannot exceed {MaxActorLength} characters."));
        if (string.Equals(RequestedBy, reviewer.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Conflict("The requester cannot review their own route exception."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(nameof(reason), "A rejection reason is required."));
        if (Status != RouteExceptionStatus.Requested)
            return Result.Failure(Error.Conflict($"Only a requested route exception can be rejected. Current status: {Status}."));
        if (reviewedAt >= ExpiresAt)
        {
            MarkExpired(reviewedAt);
            return Result.Failure(Error.Conflict("The route exception has expired."));
        }
        if (reason.Trim().Length > MaxReasonLength)
            return Result.Failure(Error.Validation(
                nameof(reason), $"Review reason cannot exceed {MaxReasonLength} characters."));
        var reviewChannel = NormalizeChannel(clientChannel);
        if (reviewChannel is null)
            return Result.Failure(Error.Validation(
                nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        if (Trimmed(deviceId)?.Length > MaxDeviceIdLength)
            return Result.Failure(Error.Validation(
                nameof(deviceId), $"Device ID cannot exceed {MaxDeviceIdLength} characters."));

        Status = RouteExceptionStatus.Rejected;
        ReviewedBy = reviewer.Trim();
        ReviewedAt = reviewedAt;
        ReviewReason = reason.Trim();
        ReviewClientChannel = reviewChannel;
        ReviewDeviceId = Trimmed(deviceId);
        return Result.Success();
    }

    /// <summary>LOT·버전·출발/도착·예외 유형이 승인 내용과 정확히 일치하는지 확인한다.</summary>
    public Result ValidateForApplication(
        Lot lot,
        RouteDeviationType deviationType,
        int fromStepIndex,
        int toStepIndex,
        DateTime now)
    {
        if (Status != RouteExceptionStatus.Approved)
            return Result.Failure(Error.Conflict($"Route exception is not approved. Current status: {Status}."));
        if (now >= ExpiresAt)
            return Result.Failure(Error.Conflict("The route exception has expired."));
        if (!string.Equals(LotId, lot.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(PlantId, lot.PlantId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Conflict("The route exception belongs to a different lot or plant."));
        if (BoundLotVersion != lot.VersionNo)
            return Result.Failure(Error.Conflict(
                $"The route exception is stale. Bound version: {BoundLotVersion}, current version: {lot.VersionNo}."));
        if (DeviationType != deviationType || FromStepIndex != fromStepIndex || ToStepIndex != toStepIndex)
            return Result.Failure(Error.Conflict("The route exception does not match the requested route transition."));
        if (!string.Equals(FromProcessId, lot.CurrentProcessId, StringComparison.OrdinalIgnoreCase) ||
            toStepIndex < 0 || toStepIndex >= lot.RouteSteps.Count ||
            !string.Equals(ToProcessId, lot.RouteSteps[toStepIndex], StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Conflict("The route process snapshot no longer matches the lot route."));
        return Result.Success();
    }

    /// <summary>승인 요청을 실행 ID와 함께 일회성으로 소비한다.</summary>
    public Result MarkApplied(string appliedBy, string executionId, DateTime appliedAt)
    {
        if (Status != RouteExceptionStatus.Approved)
            return Result.Failure(Error.Conflict("Only an approved route exception can be applied."));
        if (appliedAt >= ExpiresAt)
        {
            MarkExpired(appliedAt);
            return Result.Failure(Error.Conflict("The route exception has expired."));
        }
        if (string.IsNullOrWhiteSpace(appliedBy) || string.IsNullOrWhiteSpace(executionId))
            return Result.Failure(Error.Validation(nameof(appliedBy), "Applied user and execution ID are required."));
        if (appliedBy.Trim().Length > MaxActorLength)
            return Result.Failure(Error.Validation(
                nameof(appliedBy), $"Applied user cannot exceed {MaxActorLength} characters."));
        if (executionId.Trim().Length > MaxIdLength)
            return Result.Failure(Error.Validation(
                nameof(executionId), $"Execution ID cannot exceed {MaxIdLength} characters."));

        Status = RouteExceptionStatus.Applied;
        AppliedBy = appliedBy.Trim();
        AppliedAt = appliedAt;
        AppliedExecutionId = executionId.Trim();
        return Result.Success();
    }

    public bool IsExpired(DateTime now) =>
        (Status is RouteExceptionStatus.Requested or RouteExceptionStatus.Approved) && now >= ExpiresAt;

    internal void MarkExpired(DateTime at)
    {
        Status = RouteExceptionStatus.Expired;
        ReviewedAt ??= at;
    }

    private static string? NormalizeChannel(string? channel)
    {
        var normalized = channel?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized is "MES" or "MOBILE" or "POP" ? normalized : null;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 공정 순서 통제 정책의 단일 판정기다. LOT 자체의 절대 불변식과 예외 형식 검증을 먼저 수행한 뒤
/// Strict/Flexible/NoControl별 허용 방식을 결정한다.
/// </summary>
public interface IRoutingPolicyEvaluator
{
    RoutingPolicyDecision Evaluate(
        Lot lot,
        RouteDeviationType deviationType,
        int targetStepIndex,
        string? reason,
        RouteExceptionRequest? exception,
        DateTime now);
}

/// <summary>
/// 표준 MES 라우팅 정책 구현이다. <see cref="IRoutingPolicyEvaluator"/> 포트로 노출되어
/// 업종별 정책 드라이버가 동일한 구조화 판정 계약을 유지한 채 교체할 수 있다.
/// </summary>
public sealed class RoutingPolicyEvaluator : IRoutingPolicyEvaluator
{
    public RoutingPolicyDecision Evaluate(
        Lot lot,
        RouteDeviationType deviationType,
        int targetStepIndex,
        string? reason,
        RouteExceptionRequest? exception,
        DateTime now)
    {
        var structural = lot.ValidateRouteDeviation(deviationType, targetStepIndex);
        if (structural.IsFailure)
            return Decision(RoutingDecisionKind.Block, "ROUTE_HARD_INVARIANT", structural.Error.Description,
                lot, deviationType, targetStepIndex, requiresReason: true);

        if (deviationType == RouteDeviationType.Normal)
            return Decision(RoutingDecisionKind.Allow, "ROUTE_NORMAL_ALLOWED",
                "현재 라우팅 공정을 정상 실행할 수 있습니다.",
                lot, deviationType, targetStepIndex, requiresReason: false);

        if (lot.ControlMode == RoutingControlMode.Strict)
            return Decision(RoutingDecisionKind.Block, "ROUTE_STRICT_BLOCKED",
                "Strict 모드에서는 현재 라우팅 순서를 변경할 수 없습니다.", lot, deviationType, targetStepIndex, true);

        if (string.IsNullOrWhiteSpace(reason))
            return Decision(RoutingDecisionKind.Block, "ROUTE_REASON_REQUIRED",
                "라우팅 예외 사유를 입력해야 합니다.", lot, deviationType, targetStepIndex, true);

        // NoControl removes the approval requirement, not one-time exception binding rules. When
        // an exception ID is supplied it must still match this exact LOT version and transition.
        if (lot.ControlMode == RoutingControlMode.NoControl && exception is not null)
        {
            var noControlBinding = exception.ValidateForApplication(
                lot, deviationType, lot.CurrentStepIndex, targetStepIndex, now);
            if (noControlBinding.IsFailure)
                return Decision(RoutingDecisionKind.Block, "ROUTE_EXCEPTION_INVALID",
                    noControlBinding.Error.Description, lot, deviationType, targetStepIndex,
                    true, exception.Id);
        }

        if (lot.ControlMode == RoutingControlMode.NoControl)
            return Decision(RoutingDecisionKind.AllowWithWarning, "ROUTE_NO_CONTROL_WARNING",
                "공정 순서 이탈을 허용하지만 사유와 실행 이력이 감사 원장에 기록됩니다.",
                lot, deviationType, targetStepIndex, true);

        if (exception is null)
            return Decision(RoutingDecisionKind.ApprovalRequired, "ROUTE_APPROVAL_REQUIRED",
                "Flexible 모드의 공정 순서 변경에는 승인된 예외 요청이 필요합니다.",
                lot, deviationType, targetStepIndex, true);

        var valid = exception.ValidateForApplication(
            lot, deviationType, lot.CurrentStepIndex, targetStepIndex, now);
        return valid.IsSuccess
            ? Decision(RoutingDecisionKind.Allow, "ROUTE_APPROVED_EXCEPTION",
                "승인된 일회성 예외로 공정 순서 변경을 허용합니다.",
                lot, deviationType, targetStepIndex, true, exception.Id)
            : Decision(RoutingDecisionKind.Block, "ROUTE_EXCEPTION_INVALID", valid.Error.Description,
                lot, deviationType, targetStepIndex, true, exception.Id);
    }

    private static RoutingPolicyDecision Decision(
        RoutingDecisionKind kind,
        string code,
        string message,
        Lot lot,
        RouteDeviationType deviationType,
        int toStep,
        bool requiresReason,
        string? exceptionId = null) => new(
            kind, code, message, lot.ControlMode, deviationType,
            lot.CurrentStepIndex, toStep, requiresReason, exceptionId);
}
