using NexaOne.Common;
using NexaOne.EMS.Domain;

namespace NexaOne.EMS.Application.Ems;

public sealed class EmsService
{
    private readonly IWorkOrderRepository _workOrderRepository;

    public EmsService(IWorkOrderRepository workOrderRepository)
    {
        _workOrderRepository = workOrderRepository;
    }

    public async Task<Result<IReadOnlyList<WorkOrder>>> GetByEquipmentAsync(
        string equipmentId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var list = await _workOrderRepository.GetByEquipmentAsync(equipmentId, from, to, ct);
        return Result.Success(list);
    }

    public async Task<Result<IReadOnlyList<WorkOrder>>> GetByStatusAsync(
        WorkOrderStatus status, CancellationToken ct = default)
    {
        var list = await _workOrderRepository.GetByStatusAsync(status, ct);
        return Result.Success(list);
    }

    public async Task<Result<WorkOrder>> CreateWorkOrderAsync(
        string woId,
        string equipmentId,
        string woType,
        string desc,
        string assigneeId,
        CancellationToken ct = default)
    {
        var result = WorkOrder.Create(woId, equipmentId, woType, desc, assigneeId, DateTime.UtcNow);
        if (result.IsFailure) return result;

        await _workOrderRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> StartWorkOrderAsync(string woId, CancellationToken ct = default)
    {
        var wo = await _workOrderRepository.GetByIdAsync(woId, ct);
        if (wo is null)
            return Result.Failure(Error.NotFound(nameof(WorkOrder), woId));

        var startResult = wo.Start();
        if (startResult.IsFailure) return startResult;

        await _workOrderRepository.UpdateAsync(wo, ct);
        return Result.Success();
    }

    public async Task<Result> CompleteWorkOrderAsync(string woId, string remark, CancellationToken ct = default)
    {
        var wo = await _workOrderRepository.GetByIdAsync(woId, ct);
        if (wo is null)
            return Result.Failure(Error.NotFound(nameof(WorkOrder), woId));

        var completeResult = wo.Complete(remark);
        if (completeResult.IsFailure) return completeResult;

        await _workOrderRepository.UpdateAsync(wo, ct);
        return Result.Success();
    }

    public async Task<Result> CancelWorkOrderAsync(string woId, CancellationToken ct = default)
    {
        var wo = await _workOrderRepository.GetByIdAsync(woId, ct);
        if (wo is null)
            return Result.Failure(Error.NotFound(nameof(WorkOrder), woId));

        var cancelResult = wo.Cancel();
        if (cancelResult.IsFailure) return cancelResult;

        await _workOrderRepository.UpdateAsync(wo, ct);
        return Result.Success();
    }
}
