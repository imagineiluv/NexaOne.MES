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

    public void Update(string productName, string description, string productType, string unit)
    {
        ProductName = productName;
        Description = description;
        ProductType = productType;
        Unit = unit;
    }
}
