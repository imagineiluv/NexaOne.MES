using NexaOne.Common;

namespace NexaOne.EMS.Domain;

public sealed class SparePart : AuditableEntity<string>
{
    private SparePart(string partId) : base(partId) { }

    public string PartName { get; private set; } = string.Empty;
    public string PartNumber { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string UnitOfMeasure { get; private set; } = string.Empty;
    public decimal CurrentStock { get; private set; }
    public decimal MinStock { get; private set; }
    public decimal MaxStock { get; private set; }
    public string Location { get; private set; } = string.Empty;
    public string? EquipmentClassId { get; private set; }
    public bool IsLowStock => CurrentStock <= MinStock;

    public static Result<SparePart> Create(
        string partId,
        string partName,
        string partNumber,
        string description,
        string unitOfMeasure,
        decimal currentStock,
        decimal minStock,
        decimal maxStock,
        string location,
        string? equipmentClassId = null)
    {
        if (string.IsNullOrWhiteSpace(partId))
            return Result.Failure<SparePart>(Error.Validation(nameof(partId), "Part ID is required."));
        if (string.IsNullOrWhiteSpace(partName))
            return Result.Failure<SparePart>(Error.Validation(nameof(partName), "Part name is required."));
        if (string.IsNullOrWhiteSpace(partNumber))
            return Result.Failure<SparePart>(Error.Validation(nameof(partNumber), "Part number is required."));
        if (string.IsNullOrWhiteSpace(unitOfMeasure))
            return Result.Failure<SparePart>(Error.Validation(nameof(unitOfMeasure), "Unit of measure is required."));
        if (currentStock < 0)
            return Result.Failure<SparePart>(Error.Validation(nameof(currentStock), "Current stock must be non-negative."));
        if (minStock < 0)
            return Result.Failure<SparePart>(Error.Validation(nameof(minStock), "Min stock must be non-negative."));
        if (maxStock <= minStock)
            return Result.Failure<SparePart>(Error.Validation(nameof(maxStock), "Max stock must be greater than min stock."));
        if (string.IsNullOrWhiteSpace(location))
            return Result.Failure<SparePart>(Error.Validation(nameof(location), "Location is required."));

        var part = new SparePart(partId)
        {
            PartName = partName,
            PartNumber = partNumber,
            Description = description,
            UnitOfMeasure = unitOfMeasure,
            CurrentStock = currentStock,
            MinStock = minStock,
            MaxStock = maxStock,
            Location = location,
            EquipmentClassId = equipmentClassId
        };
        return part;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// 기존 ToDomain은 Create를 거쳐 재검증했는데, MaxStock&lt;=MinStock 같은 옛 데이터(또는 검증 우회 기록)는
    /// Create가 실패를 반환해 .Value가 null이 되고, 그 부품이 GetById에서 사라지거나 GetAll/GetLowStock의
    /// OfType 필터에 걸려 읽기마다 통째로 유실된다. 또 Create는 감사필드(CreatedBy/CreatedAt/UpdatedBy/UpdatedAt)를
    /// 복원하지 않아 영속 감사정보가 매 읽기마다 초기화된다. Restore는 new(...) 직접 경로로 영속 필드를 그대로 복원한다.</summary>
    public static SparePart Restore(
        string partId, string partName, string partNumber, string description, string unitOfMeasure,
        decimal currentStock, decimal minStock, decimal maxStock, string location, string? equipmentClassId,
        string createdBy, DateTime createdAt, string? updatedBy, DateTime? updatedAt)
        => new(partId)
        {
            PartName = partName,
            PartNumber = partNumber,
            Description = description,
            UnitOfMeasure = unitOfMeasure,
            CurrentStock = currentStock,
            MinStock = minStock,
            MaxStock = maxStock,
            Location = location,
            EquipmentClassId = equipmentClassId,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            UpdatedBy = updatedBy,
            UpdatedAt = updatedAt
        };

    public Result AdjustStock(decimal delta)
    {
        var newStock = CurrentStock + delta;
        if (newStock < 0)
            return Result.Failure(Error.Validation(nameof(delta), $"Insufficient stock. Current: {CurrentStock}, Requested adjustment: {delta}."));

        CurrentStock = newStock;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
