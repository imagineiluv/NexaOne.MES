using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ems;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — EMS 보전 단일 애그리거트 쓰기(작업지시 생명주기 +
/// 보전계획 생명주기 + 예비품 생성/재고조정). plugin(EMS)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. Result로 상태전이/팩토리 검증 분기(Conflict/Validation/NotFound/Success)를
/// 손실 없이 전달한다. 순수 조회는 게이트웨이(EMS.xml)로, MaintenancePlan→WorkOrder 캐스케이드(다중
/// 애그리거트)는 UnitOfWork 선결로 본 브리지에서 제외한다.</summary>
[NexaModuleBridge("Ems", "emsBridge")]
public interface IEmsBridge : INexaModuleBridge
{
    // ── 작업지시(WorkOrder) 생명주기 ──
    Task<Result<WorkOrderDto>> CreateWorkOrderAsync(
        string woId, string equipmentId, string woType, string description, string assigneeId,
        string? maintenancePlanId, EmsCommandContextDto command, CancellationToken ct = default);
    Task<Result> StartWorkOrderAsync(
        string woId, EmsCommandContextDto command, CancellationToken ct = default);
    Task<Result> CompleteWorkOrderAsync(
        string woId, string remark, EmsCommandContextDto command, CancellationToken ct = default);
    Task<Result> CancelWorkOrderAsync(
        string woId, EmsCommandContextDto command, CancellationToken ct = default);

    // ── 보전계획(MaintenancePlan) 생명주기 ──
    Task<Result<MaintenancePlanDto>> CreatePlanAsync(
        string planId, string planName, string equipmentId, string planType, string cycleType,
        DateTime scheduledDate, decimal estimatedHours, string assigneeId,
        EmsCommandContextDto command, CancellationToken ct = default);
    Task<Result> StartPlanAsync(
        string planId, EmsCommandContextDto command, CancellationToken ct = default);
    Task<Result> CompletePlanAsync(
        string planId, EmsCommandContextDto command, CancellationToken ct = default);
    Task<Result> CancelPlanAsync(
        string planId, EmsCommandContextDto command, CancellationToken ct = default);

    // ── 예비품(SparePart) 생성/재고조정 ──
    Task<Result<SparePartDto>> CreatePartAsync(
        string partId, string partName, string partNumber, string description, string unitOfMeasure,
        decimal currentStock, decimal minStock, decimal maxStock, string location,
        string? equipmentClassId, string actorId, CancellationToken ct = default);
    Task<Result> AdjustStockAsync(
        string partId, SparePartAdjustmentDto adjustment, CancellationToken ct = default);
}
