using NexaOne.Common;

namespace NexaOne.SHP.Domain;

public enum DeliveryOrderStatus
{
    Draft,
    Confirmed,
    Shipped,
    Cancelled
}

public sealed class DeliveryOrder : AuditableEntity<string>
{
    private readonly List<DeliveryItem> _items = [];

    private DeliveryOrder(string orderId) : base(orderId) { }

    public string CustomerName { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;
    public DateTime RequestedDate { get; private set; }
    public DateTime? ShippedDate { get; private set; }
    public DeliveryOrderStatus Status { get; private set; }
    public string? Remark { get; private set; }
    public IReadOnlyList<DeliveryItem> Items => _items.AsReadOnly();
    public decimal TotalQty => _items.Sum(i => i.PlannedQty);

    public static Result<DeliveryOrder> Create(
        string orderId,
        string customerName,
        string plantId,
        DateTime requestedDate,
        string? remark = null)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return Result.Failure<DeliveryOrder>(Error.Validation(nameof(orderId), "Order ID is required."));
        if (string.IsNullOrWhiteSpace(customerName))
            return Result.Failure<DeliveryOrder>(Error.Validation(nameof(customerName), "Customer name is required."));
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<DeliveryOrder>(Error.Validation(nameof(plantId), "Plant ID is required."));

        var order = new DeliveryOrder(orderId)
        {
            CustomerName = customerName,
            PlantId = plantId,
            RequestedDate = requestedDate,
            Status = DeliveryOrderStatus.Draft,
            Remark = remark
        };
        return order;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 항상 Status=Draft·ShippedDate=null로 강제하므로 Confirmed/Shipped/Cancelled 주문이 Draft로
    /// 잘못 읽혀 상태머신이 무력화되는 읽기경로 상태손실을 막는다(품목은 별도 로드).</summary>
    public static DeliveryOrder Restore(
        string orderId, string customerName, string plantId, DateTime requestedDate,
        DeliveryOrderStatus status, DateTime? shippedDate, string? remark,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var order = new DeliveryOrder(orderId)
        {
            CustomerName = customerName,
            PlantId = plantId,
            RequestedDate = requestedDate,
            Status = status,
            ShippedDate = shippedDate,
            Remark = remark
        };
        order.RestoreAudit(createdBy ?? order.CreatedBy, createdAt ?? order.CreatedAt, updatedBy, updatedAt);
        return order;
    }

    public void AddItem(DeliveryItem item)
    {
        _items.Add(item);
        UpdatedAt = DateTime.UtcNow;
    }

    public Result Confirm()
    {
        if (Status != DeliveryOrderStatus.Draft)
            return Result.Failure(Error.Conflict("Delivery order can only be confirmed from Draft status."));

        Status = DeliveryOrderStatus.Confirmed;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 확정을 도메인 이벤트로 발행한다. 리포가 확정(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        RaiseDomainEvent(new DeliveryOrderConfirmedDomainEvent(Id));
        return Result.Success();
    }

    public Result Ship(DateTime shippedDate)
    {
        if (Status != DeliveryOrderStatus.Confirmed)
            return Result.Failure(Error.Conflict("Delivery order can only be shipped from Confirmed status."));

        Status = DeliveryOrderStatus.Shipped;
        ShippedDate = shippedDate;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 출하를 도메인 이벤트로 발행한다. 리포가 출하(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new DeliveryOrderShippedDomainEvent(Id, shippedDate));
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == DeliveryOrderStatus.Shipped || Status == DeliveryOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Delivery order cannot be cancelled in its current status."));

        Status = DeliveryOrderStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 취소를 도메인 이벤트로 발행한다. 리포가 취소(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new DeliveryOrderCancelledDomainEvent(Id));
        return Result.Success();
    }
}
