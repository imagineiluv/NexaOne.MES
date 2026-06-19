using NexaOne.Common;

namespace NexaOne.MDM.Domain;

public sealed class Product : AuditableEntity<string>
{
    private Product(string productId) : base(productId) { }

    public string ProductName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string ProductType { get; private set; } = string.Empty;
    public string Unit { get; private set; } = string.Empty;
    public string ValidState { get; private set; } = "Valid";

    public static Result<Product> Create(
        string productId,
        string productName,
        string productType,
        string unit)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<Product>(Error.Validation(nameof(productId), "Product ID is required."));
        if (string.IsNullOrWhiteSpace(productName))
            return Result.Failure<Product>(Error.Validation(nameof(productName), "Product name is required."));

        var product = new Product(productId)
        {
            ProductName = productName,
            ProductType = productType,
            Unit = unit,
            ValidState = "Valid"
        };
        return product;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 Description을 받지 않아 유실되고 ValidState를 무조건 "Valid"로 재설정해, 비활성("Invalid") 제품이
    /// 읽기경로에서 유효로 오인되고 설명이 비는 상태손실을 막는다.</summary>
    public static Product Restore(
        string productId,
        string productName,
        string description,
        string productType,
        string unit,
        string validState,
        string? createdBy = null,
        DateTime? createdAt = null,
        string? updatedBy = null,
        DateTime? updatedAt = null)
    {
        var product = new Product(productId)
        {
            ProductName = productName,
            Description = description,
            ProductType = productType,
            Unit = unit,
            ValidState = validState
        };
        product.RestoreAudit(createdBy ?? product.CreatedBy, createdAt ?? product.CreatedAt, updatedBy, updatedAt);
        return product;
    }

    public void Update(string productName, string description, string productType, string unit)
    {
        ProductName = productName;
        Description = description;
        ProductType = productType;
        Unit = unit;
    }
}
