using NexaOne.Common;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// 설비의 local-first 실행 snapshot을 POM durable inbox에 접수하는 interface입니다.
/// 접수는 WorkScope 업무 상태를 변경하지 않으며, mapper/plugin이 별도 단계에서 해석합니다.
/// </summary>
public interface IWorkScopeProjectionBridge : INexaModuleBridge
{
    Task<Result<WorkScopeProjectionReceiptDto>> IngestAsync(
        string sourceClientId,
        WorkScopeProjectionCommand command,
        CancellationToken ct = default);
}

public enum WorkScopeProjectionStatus
{
    Running,
    Completed,
    Abandoned,
    RecoveryRequired,
}

public sealed record WorkScopeProjectionCarrierDto(
    string Lane,
    string CarrierId,
    string CleaningRunId);

/// <summary>Cleaner WorkScopeExecutionSnapshot의 transport-neutral 수신 계약입니다.</summary>
/// <remarks>
/// Completed/Abandoned + TerminalCleanupCompleted=false도 유효한 설비 증거입니다. Recovery는
/// 같은 Revision에서 별도 event를 만들 수 있고 cleanup 확인은 Revision+1에서 올 수 있습니다.
/// 향후 business mapper만 cleanup=true일 때 terminal WorkScope 전이를 수행합니다.
/// </remarks>
public sealed record WorkScopeProjectionCommand(
    string ClientId,
    string EventId,
    string WorkScopeId,
    string EquipmentId,
    string OperationKey,
    string PairRunId,
    string SequenceRunId,
    WorkScopeProjectionStatus Status,
    bool TerminalCleanupCompleted,
    string RecipeId,
    string RecipeSnapshotHash,
    string ProgramHash,
    IReadOnlyList<WorkScopeProjectionCarrierDto> Carriers,
    DateTimeOffset OccurredAt,
    long Revision,
    string ResultCode,
    string? ResultMetadataJson = null);

public sealed record WorkScopeProjectionReceiptDto(
    string SourceClientId,
    string EventId,
    string WorkScopeId,
    bool Replay,
    bool IsCurrent,
    long CurrentRevision,
    DateTime AcceptedAt);
