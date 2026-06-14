using NexaOne.Common;

namespace NexaOne.POM.Domain;

public enum ProductionOrderStatus
{
    Issued,
    InProgress,
    Completed,
    Cancelled
}

public sealed class ProductionOrder : AuditableEntity<string>
{
    private ProductionOrder(string orderId) : base(orderId) { }

    public string PlanId { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string ProductId { get; private set; } = string.Empty;
    public decimal OrderQty { get; private set; }
    public decimal? ActualQty { get; private set; }
    public DateTime ScheduledStart { get; private set; }
    public DateTime ScheduledEnd { get; private set; }
    public DateTime? ActualStart { get; private set; }
    public DateTime? ActualEnd { get; private set; }
    public ProductionOrderStatus Status { get; private set; }

    public static Result<ProductionOrder> Create(
        string orderId,
        string planId,
        string equipmentId,
        string productId,
        decimal orderQty,
        DateTime scheduledStart,
        DateTime scheduledEnd)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(orderId), "Order ID is required."));
        if (string.IsNullOrWhiteSpace(planId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(planId), "Plan ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(productId), "Product ID is required."));
        if (orderQty <= 0)
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(orderQty), "Order quantity must be positive."));
        if (scheduledStart > scheduledEnd)
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(scheduledStart), "Scheduled start must be on or before scheduled end."));

        var order = new ProductionOrder(orderId)
        {
            PlanId = planId,
            EquipmentId = equipmentId,
            ProductId = productId,
            OrderQty = orderQty,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledEnd,
            Status = ProductionOrderStatus.Issued
        };
        return order;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// 기존 ToDomain은 Create 후 Start/Complete/Cancel을 재생(replay)했는데, 전이가 도메인 이벤트를 발행하면
    /// 모든 읽기마다 유령 이벤트(phantom)가 outbox에 기록되는 읽기경로 손상이 생긴다. Restore는 new(...) 직접
    /// 경로로 Status·EquipmentId·ActualStart/End·ActualQty 등 영속 필드를 그대로 복원해 재생·이벤트발행을 없앤다.</summary>
    public static ProductionOrder Restore(
        string orderId, string planId, string equipmentId, string productId, decimal orderQty,
        decimal? actualQty, DateTime scheduledStart, DateTime scheduledEnd,
        DateTime? actualStart, DateTime? actualEnd, ProductionOrderStatus status)
        => new(orderId)
        {
            PlanId = planId,
            EquipmentId = equipmentId,
            ProductId = productId,
            OrderQty = orderQty,
            ActualQty = actualQty,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledEnd,
            ActualStart = actualStart,
            ActualEnd = actualEnd,
            Status = status
        };

    public Result Start(DateTime actualStart)
    {
        if (Status != ProductionOrderStatus.Issued)
            return Result.Failure(Error.Conflict("Production order can only be started from Issued status."));

        Status = ProductionOrderStatus.InProgress;
        ActualStart = actualStart;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 착수를 도메인 이벤트로 발행한다. 리포가 착수(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        RaiseDomainEvent(new ProductionOrderStartedDomainEvent(Id, EquipmentId, ProductId, actualStart));
        return Result.Success();
    }

    public Result Complete(decimal actualQty, DateTime actualEnd)
    {
        if (Status != ProductionOrderStatus.InProgress)
            return Result.Failure(Error.Conflict("Production order can only be completed from InProgress status."));
        if (actualQty < 0)
            return Result.Failure(Error.Validation(nameof(actualQty), "Actual quantity must be non-negative."));

        Status = ProductionOrderStatus.Completed;
        ActualQty = actualQty;
        ActualEnd = actualEnd;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 완료를 도메인 이벤트로 발행한다. 리포가 완료(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new ProductionOrderCompletedDomainEvent(Id, actualQty, actualEnd));
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == ProductionOrderStatus.Completed || Status == ProductionOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Production order cannot be cancelled in its current status."));

        Status = ProductionOrderStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 취소를 도메인 이벤트로 발행한다. 리포가 취소(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new ProductionOrderCancelledDomainEvent(Id));
        return Result.Success();
    }
}
