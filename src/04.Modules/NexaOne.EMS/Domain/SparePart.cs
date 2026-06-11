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
