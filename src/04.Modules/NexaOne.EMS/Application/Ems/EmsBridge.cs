using NexaOne.Common;
using NexaOne.EMS.Domain;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.Ems;

/// <summary>ADR-008 얇은 브리지 어댑터 — EmsService(작업지시)·MaintenancePlanService(계획/예비품)에 위임하고
/// 도메인 엔티티를 계약 DTO로 매핑(Status enum→string). plugin ALC에서 생성되며 호스트(Default ALC)가
/// IEmsBridge로 캐스트해 DI에 등록한다. 상태전이/팩토리 검증의 Result는 그대로 통과시켜 컨트롤러가
/// 409/400/404로 매핑한다.</summary>
public sealed class EmsBridge : IEmsBridge
{
    private readonly EmsService _woService;
    private readonly MaintenancePlanService _planService;

    public EmsBridge(EmsService woService, MaintenancePlanService planService)
    {
        _woService = woService;
        _planService = planService;
    }

    // ── 작업지시 ──

    public async Task<Result<WorkOrderDto>> CreateWorkOrderAsync(
        string woId, string equipmentId, string woType, string description, string assigneeId,
        string? maintenancePlanId, EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        if (context.IsFailure) return Result.Failure<WorkOrderDto>(context.Error);
        var r = await _woService.CreateWorkOrderAsync(
            woId, equipmentId, woType, description, assigneeId,
            maintenancePlanId, context.Value, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<WorkOrderDto>(r.Error);
    }

    public Task<Result> StartWorkOrderAsync(
        string woId, EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        return context.IsFailure
            ? Task.FromResult(Result.Failure(context.Error))
            : _woService.StartWorkOrderAsync(woId, context.Value, ct);
    }

    public Task<Result> CompleteWorkOrderAsync(
        string woId, string remark, EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        return context.IsFailure
            ? Task.FromResult(Result.Failure(context.Error))
            : _woService.CompleteWorkOrderAsync(woId, remark, context.Value, ct);
    }

    public Task<Result> CancelWorkOrderAsync(
        string woId, EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        return context.IsFailure
            ? Task.FromResult(Result.Failure(context.Error))
            : _woService.CancelWorkOrderAsync(woId, context.Value, ct);
    }

    // ── 보전계획 ──

    public async Task<Result<MaintenancePlanDto>> CreatePlanAsync(
        string planId, string planName, string equipmentId, string planType, string cycleType,
        DateTime scheduledDate, decimal estimatedHours, string assigneeId,
        EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        if (context.IsFailure) return Result.Failure<MaintenancePlanDto>(context.Error);
        var r = await _planService.CreatePlanAsync(
            planId, planName, equipmentId, planType, cycleType, scheduledDate, estimatedHours,
            assigneeId, context.Value, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<MaintenancePlanDto>(r.Error);
    }

    public Task<Result> StartPlanAsync(
        string planId, EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        return context.IsFailure
            ? Task.FromResult(Result.Failure(context.Error))
            : _planService.StartPlanAsync(planId, context.Value, ct);
    }

    public Task<Result> CompletePlanAsync(
        string planId, EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        return context.IsFailure
            ? Task.FromResult(Result.Failure(context.Error))
            : _planService.CompletePlanAsync(planId, context.Value, ct);
    }

    public Task<Result> CancelPlanAsync(
        string planId, EmsCommandContextDto command, CancellationToken ct = default)
    {
        var context = ToDomain(command);
        return context.IsFailure
            ? Task.FromResult(Result.Failure(context.Error))
            : _planService.CancelPlanAsync(planId, context.Value, ct);
    }

    // ── 예비품 ──

    public async Task<Result<SparePartDto>> CreatePartAsync(
        string partId, string partName, string partNumber, string description, string unitOfMeasure,
        decimal currentStock, decimal minStock, decimal maxStock, string location,
        string? equipmentClassId, string actorId, CancellationToken ct = default)
    {
        var r = await _planService.CreatePartAsync(
            partId, partName, partNumber, description, unitOfMeasure,
            currentStock, minStock, maxStock, location, equipmentClassId, actorId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<SparePartDto>(r.Error);
    }

    public Task<Result> AdjustStockAsync(
        string partId, SparePartAdjustmentDto adjustment, CancellationToken ct = default)
    {
        var command = ToDomain(adjustment.Command);
        if (command.IsFailure) return Task.FromResult(Result.Failure(command.Error));
        var context = new SparePartAdjustmentContext(
            command.Value, adjustment.TransactionType, adjustment.WorkOrderId,
            adjustment.EquipmentId, adjustment.FromLocation, adjustment.ToLocation,
            adjustment.Remark, adjustment.BomItemId);
        return _planService.AdjustStockAsync(partId, adjustment.Delta, context, ct);
    }

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

    private static Result<MaintenanceCommandContext> ToDomain(EmsCommandContextDto command) =>
        MaintenanceCommandContext.Create(
            command.ActorId, command.IdempotencyKey, command.ClientChannel,
            command.DeviceId, command.CorrelationId);
}
