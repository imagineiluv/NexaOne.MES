using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.WorkOrders;

/// <summary>
/// 공정 작업지시 애그리거트와 append-only 실행 이력의 영속 계약이다.
/// 생산관리오더 저장소와 분리하며, 상태 변경에는 낙관적 버전 검사와 멱등 실행 기록을 함께 적용한다.
/// </summary>
public interface IPomWorkOrderRepository
{
    /// <summary>작업지시 식별자로 현재 애그리거트를 조회한다.</summary>
    Task<PomWorkOrder?> GetByIdAsync(string workOrderId, CancellationToken ct = default);

    /// <summary>생산관리오더에 속한 공정 작업지시 목록을 조회한다.</summary>
    Task<IReadOnlyList<PomWorkOrder>> GetByProductionOrderAsync(string productionOrderId, CancellationToken ct = default);

    /// <summary>멱등 키가 실행 이력에 이미 기록됐는지 확인한다.</summary>
    Task<bool> ExecutionExistsAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>동일 요청 재시도의 의미를 검증할 기존 실행 이력을 조회한다.</summary>
    Task<PomWorkOrderExecution?> GetExecutionByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>새 공정 작업지시를 추가한다.</summary>
    Task AddAsync(PomWorkOrder workOrder, CancellationToken ct = default);

    /// <summary>현재 버전이 일치할 때만 작업지시를 갱신한다.</summary>
    Task<bool> UpdateAsync(PomWorkOrder workOrder, CancellationToken ct = default);

    /// <summary>버전 조건부 작업지시 갱신과 멱등 실행 이력 추가를 원자적으로 처리한다.</summary>
    Task<bool> UpdateWithExecutionAsync(PomWorkOrder workOrder, PomWorkOrderExecution execution, CancellationToken ct = default);
}
