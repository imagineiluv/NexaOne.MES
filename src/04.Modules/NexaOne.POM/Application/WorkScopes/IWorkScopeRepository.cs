using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.WorkScopes;

/// <summary>
/// 생산 W/O와 분리된 작업 대상 및 append-only 실행 이력의 영속 계약입니다.
/// 상태 행과 실행 이력은 UpdateWithExecutionAsync에서 같은 트랜잭션으로 기록합니다.
/// </summary>
public interface IWorkScopeRepository
{
    Task<PomWorkScope?> GetByIdAsync(string workScopeId, CancellationToken ct = default);

    Task<PomWorkScope?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task<IReadOnlyList<PomWorkScope>> ListAsync(
        string? plantId,
        PomWorkScopeType? scopeType,
        string? targetId,
        string? parentScopeId,
        PomWorkScopeStatus? status,
        CancellationToken ct = default);

    Task<IReadOnlyList<PomWorkScopeMember>> ListMembersAsync(
        string workScopeId,
        CancellationToken ct = default);

    Task<IReadOnlyList<PomWorkScopeExecution>> ListExecutionsAsync(
        string workScopeId,
        CancellationToken ct = default);

    Task<PomWorkScopeExecution?> GetExecutionByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task AddAsync(PomWorkScope scope, CancellationToken ct = default);

    Task<bool> UpdateWithExecutionAsync(
        PomWorkScope scope,
        PomWorkScopeExecution execution,
        CancellationToken ct = default);
}
