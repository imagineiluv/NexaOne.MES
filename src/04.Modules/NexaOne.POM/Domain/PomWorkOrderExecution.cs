namespace NexaOne.POM.Domain;

/// <summary>작업지시 실행 이력에 기록할 상태 변경 동작 종류다.</summary>
public enum PomWorkOrderAction
{
    /// <summary>작업지시를 현장 실행 가능 상태로 발행한다.</summary>
    Release,

    /// <summary>작업지시 실행을 시작한다.</summary>
    Start,

    /// <summary>양품·불량 절대 누계를 보고한다.</summary>
    Report,

    /// <summary>작업지시 실행을 보류한다.</summary>
    Hold,

    /// <summary>작업지시 보류를 해제한다.</summary>
    ReleaseHold,

    /// <summary>최종 실적을 확정하고 작업지시를 마감한다.</summary>
    Complete,

    /// <summary>시작 전 작업지시를 취소한다.</summary>
    Cancel
}

/// <summary>
/// 작업지시 상태 전이와 같은 트랜잭션에 추가되는 append-only 실행 감사 이력이다.
/// 멱등 키, 전이 전후 상태와 호출 단말 정보를 남겨 재시도 판정과 추적성을 제공한다.
/// </summary>
public sealed record PomWorkOrderExecution(
    string ExecutionId,
    string WorkOrderId,
    string IdempotencyKey,
    PomWorkOrderAction Action,
    PomWorkOrderStatus FromStatus,
    PomWorkOrderStatus ToStatus,
    decimal? GoodQty,
    decimal? DefectQty,
    string UserId,
    string? EquipmentId,
    string ClientChannel,
    string? DeviceId,
    DateTime OccurredAt,
    string? Remark = null,
    int? ExpectedVersion = null,
    int? ResultVersion = null);
