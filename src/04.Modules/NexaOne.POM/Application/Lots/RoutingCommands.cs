using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.Lots;

/// <summary>LOT에 적용할 라우팅 통제 수준 변경 명령이다.</summary>
public sealed record ChangeRoutingControlModeCommand(
    string PlantId,
    string LotId,
    RoutingControlMode ControlMode,
    string Reason,
    string User,
    int ExpectedVersion,
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null);

/// <summary>공정 이탈을 실제 반영하지 않고 정책 결과만 확인하는 명령이다.</summary>
public sealed record EvaluateRoutingCommand(
    string PlantId,
    string LotId,
    RouteDeviationType DeviationType,
    int TargetStepIndex,
    string? Reason,
    string? ExceptionId = null);

/// <summary>승인 또는 NoControl 사유에 따라 공정 이탈을 LOT에 일회 적용하는 명령이다.</summary>
public sealed record ApplyRouteDeviationCommand(
    string PlantId,
    string LotId,
    RouteDeviationType DeviationType,
    int TargetStepIndex,
    string Reason,
    string User,
    int ExpectedVersion,
    string IdempotencyKey,
    string? ExceptionId = null,
    string ClientChannel = "MES",
    string? DeviceId = null);

/// <summary>Flexible 모드 공정 이탈을 승인받기 위한 버전 바인딩 요청이다.</summary>
public sealed record RequestRouteExceptionCommand(
    string ExceptionId,
    string PlantId,
    string LotId,
    RouteDeviationType DeviationType,
    int TargetStepIndex,
    string Reason,
    string User,
    int ExpectedVersion,
    DateTime ExpiresAt,
    string ClientChannel = "MES",
    string? DeviceId = null);

/// <summary>예외 요청 승인 또는 반려에 사용하는 검토 명령이다.</summary>
public sealed record ReviewRouteExceptionCommand(
    string ExceptionId,
    string Reviewer,
    string? Reason = null,
    string ClientChannel = "MES",
    string? DeviceId = null);

/// <summary>LOT의 현재·다음·복귀 위치와 예외 승인 원장을 함께 보여 주는 조회 모델이다.</summary>
public sealed record LotRoutingContext(
    Lot Lot,
    int? ReturnStepIndex,
    string? ReturnProcessId,
    IReadOnlyList<RouteExceptionRequest> Exceptions);

/// <summary>POM_LOT_EXECUTION에 라우팅 의사결정 문맥을 함께 기록하는 감사 메타데이터다.</summary>
public sealed record RoutingTransitionAudit(
    int? FromStepIndex,
    int? ToStepIndex,
    string? FromProcessId,
    string? ToProcessId,
    RoutingControlMode ControlMode,
    string? RouteExceptionId,
    string ClientChannel,
    string? DeviceId,
    string Reason);
