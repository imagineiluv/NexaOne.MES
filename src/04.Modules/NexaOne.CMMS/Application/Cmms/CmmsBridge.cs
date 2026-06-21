using NexaOne.Common;
using NexaOne.CMMS.Domain;
using NexaOne.ServiceContracts.Cmms;

namespace NexaOne.CMMS.Application.Cmms;

/// <summary>ADR-008 얇은 브리지 어댑터 — CmmsService(작업지시)·MaintenancePlanService(계획/예비품)에 위임하고
/// 도메인 엔티티를 계약 DTO로 매핑(Status enum→string). plugin ALC에서 생성되며 호스트(Default ALC)가
/// ICmmsBridge로 캐스트해 DI에 등록한다. 상태전이/팩토리 검증의 Result는 그대로 통과시켜 컨트롤러가
/// 409/400/404로 매핑한다.</summary>
public sealed class CmmsBridge : ICmmsBridge
{
    private readonly CmmsService _woService;
    private readonly MaintenancePlanService _planService;

    public CmmsBridge(CmmsService woService, MaintenancePlanService planService)
    {
        _woService = woService;
        _planService = planService;
    }

    // ── 작업지시 ──

    public async Task<Result<WorkOrderDto>> CreateWorkOrderAsync(
        string woId, string equipmentId, string woType, string description, string assigneeId, CancellationToken ct = default)
    {
        var r = await _woService.CreateWorkOrderAsync(woId, equipmentId, woType, description, assigneeId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<WorkOrderDto>(r.Error);
    }

    public Task<Result> StartWorkOrderAsync(string woId, CancellationToken ct = default)
        => _woService.StartWorkOrderAsync(woId, ct);

    public Task<Result> CompleteWorkOrderAsync(string woId, string remark, CancellationToken ct = default)
        => _woService.CompleteWorkOrderAsync(woId, remark, ct);

    public Task<Result> CancelWorkOrderAsync(string woId, CancellationToken ct = default)
        => _woService.CancelWorkOrderAsync(woId, ct);

    // ── 보전계획 ──

    public async Task<Result<MaintenancePlanDto>> CreatePlanAsync(
        string planId, string planName, string equipmentId, string planType, string cycleType,
        DateTime scheduledDate, decimal estimatedHours, string assigneeId, CancellationToken ct = default)
    {
        var r = await _planService.CreatePlanAsync(
            planId, planName, equipmentId, planType, cycleType, scheduledDate, estimatedHours, assigneeId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<MaintenancePlanDto>(r.Error);
    }

    public Task<Result> StartPlanAsync(string planId, CancellationToken ct = default)
        => _planService.StartPlanAsync(planId, ct);

    public Task<Result> CompletePlanAsync(string planId, CancellationToken ct = default)
        => _planService.CompletePlanAsync(planId, ct);

    public Task<Result> CancelPlanAsync(string planId, CancellationToken ct = default)
        => _planService.CancelPlanAsync(planId, ct);

    // ── 예비품 ──

    public async Task<Result<SparePartDto>> CreatePartAsync(
        string partId, string partName, string partNumber, string description, string unitOfMeasure,
        decimal currentStock, decimal minStock, decimal maxStock, string location,
        string? equipmentClassId, CancellationToken ct = default)
    {
        var r = await _planService.CreatePartAsync(
            partId, partName, partNumber, description, unitOfMeasure,
            currentStock, minStock, maxStock, location, equipmentClassId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<SparePartDto>(r.Error);
    }

    public Task<Result> AdjustStockAsync(string partId, decimal delta, CancellationToken ct = default)
        => _planService.AdjustStockAsync(partId, delta, ct);

    // ── 매핑 ──

    private static WorkOrderDto ToDto(WorkOrder w)
        => new(w.Id, w.PlanId, w.EquipmentId, w.WoType, w.Description, w.AssigneeId,
            w.IssuedAt, w.StartedAt, w.CompletedAt, w.Status.ToString(), w.FailureCodeId, w.Remark);

    private static MaintenancePlanDto ToDto(MaintenancePlan p)
        => new(p.Id, p.PlanName, p.EquipmentId, p.PlanType, p.CycleType,
            p.ScheduledDate, p.EstimatedDurationHours, p.AssigneeId, p.Status.ToString());

    private static SparePartDto ToDto(SparePart s)
        => new(s.Id, s.PartName, s.PartNumber, s.Description, s.UnitOfMeasure,
            s.CurrentStock, s.MinStock, s.MaxStock, s.Location, s.EquipmentClassId, s.IsLowStock);
}
