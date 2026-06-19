using NexaOne.Common;

namespace NexaOne.SHP.Domain;

public sealed class DeliveryItem : AuditableEntity<string>
{
    private DeliveryItem(string itemId) : base(itemId) { }

    public string DeliveryOrderId { get; private set; } = string.Empty;
    public string ProductId { get; private set; } = string.Empty;
    public decimal PlannedQty { get; private set; }
    public decimal? ActualQty { get; private set; }
    public string? LotId { get; private set; }

    public static Result<DeliveryItem> Create(
        string itemId,
        string deliveryOrderId,
        string productId,
        decimal plannedQty,
        string? lotId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(itemId), "Item ID is required."));
        if (string.IsNullOrWhiteSpace(deliveryOrderId))
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(deliveryOrderId), "Delivery order ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(productId), "Product ID is required."));
        if (plannedQty <= 0)
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(plannedQty), "Planned quantity must be positive."));

        var item = new DeliveryItem(itemId)
        {
            DeliveryOrderId = deliveryOrderId,
            ProductId = productId,
            PlannedQty = plannedQty,
            LotId = lotId
        };
        return item;
    }

    public Result SetActualQty(decimal qty)
    {
        if (qty < 0)
            return Result.Failure(Error.Validation(nameof(qty), "Actual quantity must be non-negative."));

        ActualQty = qty;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>읽기경로 재구성 전용 팩토리(읽기경로 Restore 패턴). 영속된 모든 필드를 검증 없이
    /// 그대로 복원한다 — Create+뮤테이터로 재구성하면 (1) CreatedAt/By·UpdatedAt/By 감사 메타데이터가
    /// 손실되고, (2) SetActualQty가 UpdatedAt을 UtcNow로 덮어쓰며, (3) 과거에 적재된(현행 검증 기준 미달)
    /// 행이 Create 검증 실패로 드롭되는 문제가 있다. Restore는 이 세 가지를 모두 방지한다.</summary>
    public static DeliveryItem Restore(
        string itemId,
        string deliveryOrderId,
        string productId,
        decimal plannedQty,
        decimal? actualQty,
        string? lotId,
        string? createdBy = null,
        DateTime? createdAt = null,
        string? updatedBy = null,
        DateTime? updatedAt = null)
    {
        var item = new DeliveryItem(itemId)
        {
            DeliveryOrderId = deliveryOrderId,
            ProductId = productId,
            PlannedQty = plannedQty,
            ActualQty = actualQty,
            LotId = lotId
        };
        item.RestoreAudit(createdBy ?? item.CreatedBy, createdAt ?? item.CreatedAt, updatedBy, updatedAt);
        return item;
    }
}
