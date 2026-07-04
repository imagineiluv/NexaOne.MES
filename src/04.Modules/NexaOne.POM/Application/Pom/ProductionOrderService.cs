using NexaOne.Common;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.Pom;

public sealed class ProductionOrderService
{
    private readonly IProductionOrderRepository _orderRepository;

    public ProductionOrderService(IProductionOrderRepository orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<Result<IReadOnlyList<ProductionOrder>>> GetByPlanAsync(
        string planId, CancellationToken ct = default)
    {
        var list = await _orderRepository.GetByPlanAsync(planId, ct);
        return Result.Success(list);
    }

    public async Task<Result<ProductionOrder>> CreateOrderAsync(
        string orderId,
        string planId,
        string equipmentId,
        string productId,
        decimal orderQty,
        DateTime scheduledStart,
        DateTime scheduledEnd,
        CancellationToken ct = default)
    {
        var result = ProductionOrder.Create(orderId, planId, equipmentId, productId, orderQty, scheduledStart, scheduledEnd);
        if (result.IsFailure) return result;

        await _orderRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> StartOrderAsync(string orderId, CancellationToken ct = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, ct);
        if (order is null)
            return Result.Failure(Error.NotFoundOf(nameof(ProductionOrder), orderId));

        var startResult = order.Start(DateTime.UtcNow);
        if (startResult.IsFailure) return startResult;

        await _orderRepository.UpdateAsync(order, ct);
        return Result.Success();
    }

    public async Task<Result> CompleteOrderAsync(string orderId, decimal actualQty, CancellationToken ct = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, ct);
        if (order is null)
            return Result.Failure(Error.NotFoundOf(nameof(ProductionOrder), orderId));

        var completeResult = order.Complete(actualQty, DateTime.UtcNow);
        if (completeResult.IsFailure) return completeResult;

        await _orderRepository.UpdateAsync(order, ct);
        return Result.Success();
    }

    public async Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, ct);
        if (order is null)
            return Result.Failure(Error.NotFoundOf(nameof(ProductionOrder), orderId));

        var cancelResult = order.Cancel();
        if (cancelResult.IsFailure) return cancelResult;

        await _orderRepository.UpdateAsync(order, ct);
        return Result.Success();
    }
}
