namespace NexaOne.POM.Domain;

/// <summary>작업 대상의 상태 전이 종류입니다.</summary>
public enum PomWorkScopeAction
{
    Release,
    Start,
    Report,
    Hold,
    ReleaseHold,
    Complete,
    Cancel
}

/// <summary>작업 대상 상태 전이와 함께 기록하는 append-only 실행 이력입니다.</summary>
public sealed record PomWorkScopeExecution(
    string ExecutionId,
    string WorkScopeId,
    string IdempotencyKey,
    PomWorkScopeAction Action,
    PomWorkScopeStatus FromStatus,
    PomWorkScopeStatus ToStatus,
    decimal? GoodQty,
    decimal? DefectQty,
    string UserId,
    string? EquipmentId,
    string ClientChannel,
    string? DeviceId,
    DateTime OccurredAt,
    string? Remark = null,
    int? ExpectedVersion = null,
    int? ResultVersion = null,
    string? CarrierId = null,
    string? ResultCode = null,
    string? ResultMetadataJson = null);

/// <summary>상위 Campaign/Batch와 하위 실행 범위 사이의 편성 원장입니다.</summary>
public sealed record PomWorkScopeMember(
    string MemberId,
    string WorkScopeId,
    string MemberScopeId,
    PomWorkScopeType MemberType,
    string MemberTargetId,
    int SequenceNo,
    DateTime CreatedAt);
