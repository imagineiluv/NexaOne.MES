using NexaOne.Common;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.WorkOrders;

/// <summary>
/// Applies the strict predecessor invariant to every work-order start path.
/// Explicit W/O Start and LOT TrackIn auto-start both call this guard.
/// </summary>
public static class WorkOrderRoutingPredecessorGuard
{
    /// <summary>
    /// Allows unbound and whole-route work orders. Only a single-operation work order has sibling
    /// predecessors; a SerialRoute work order owns the ordered LOT flow itself.
    /// </summary>
    public static async Task<Result> ValidateAsync(
        IPomWorkOrderRepository repository,
        PomWorkOrder workOrder,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(workOrder);

        if (workOrder.RoutingScope != PomWorkOrderRoutingScope.Operation ||
            string.IsNullOrWhiteSpace(workOrder.RoutingId) ||
            !workOrder.RoutingStepNo.HasValue)
            return Result.Success();

        var siblings = await repository.GetByProductionOrderAsync(workOrder.ProductionOrderId, ct) ?? [];
        var incomplete = siblings
            .Where(candidate => !string.Equals(candidate.Id, workOrder.Id, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.Equals(
                candidate.RoutingId, workOrder.RoutingId, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.RoutingStepNo.HasValue &&
                                candidate.RoutingStepNo.Value < workOrder.RoutingStepNo.Value)
            .Where(candidate => candidate.Status != PomWorkOrderStatus.Completed)
            .OrderBy(candidate => candidate.RoutingStepNo)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return incomplete is null
            ? Result.Success()
            : Result.Failure(Error.Conflict(
                $"ROUTE_PREDECESSOR_INCOMPLETE: Work order '{incomplete.Id}' " +
                $"(routing step {incomplete.RoutingStepNo}) must be completed first."));
    }
}
