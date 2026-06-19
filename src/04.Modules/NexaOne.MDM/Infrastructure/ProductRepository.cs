using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Infrastructure;

public sealed class ProductRepository : QueryRepository, IProductRepository
{
    private readonly ServiceObjectProcessor _processor;

    public ProductRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<Product?> GetByIdAsync(string productId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_PRODUCT WHERE PRODUCT_ID = @productId";
        var row = await QueryFirstOrDefaultAsync<ProductRow>(sql, new { productId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_PRODUCT WHERE VALID_STATE = 'Valid' ORDER BY PRODUCT_NAME";
        var rows = await QueryAsync<ProductRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Product>().ToList();
    }

    public async Task AddAsync(Product product, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO MDM_PRODUCT
            (PRODUCT_ID, PRODUCT_NAME, DESCRIPTION, PRODUCT_TYPE, UNIT, VALID_STATE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@ProductId, @ProductName, @Description, @ProductType, @Unit, @ValidState,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, ProductRow.FromDomain(product), ct);
    }

    public async Task UpdateAsync(Product product, CancellationToken ct = default)
    {
        const string sql = @"UPDATE MDM_PRODUCT SET
            PRODUCT_NAME = @ProductName, DESCRIPTION = @Description,
            PRODUCT_TYPE = @ProductType, UNIT = @Unit,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PRODUCT_ID = @ProductId";
        await _processor.UpdateAsync(sql, ProductRow.FromDomain(product), ct);
    }

    private sealed class ProductRow
    {
        public string ProductId   { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string Description { get; set; } = "";
        public string ProductType { get; set; } = "";
        public string Unit        { get; set; } = "";
        public string ValidState  { get; set; } = "Valid";
        public string CreatedBy   { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy  { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public Product ToDomain() =>
            Product.Restore(ProductId, ProductName, Description, ProductType, Unit, ValidState,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static ProductRow FromDomain(Product p) => new()
        {
            ProductId = p.Id, ProductName = p.ProductName, Description = p.Description,
            ProductType = p.ProductType, Unit = p.Unit, ValidState = p.ValidState
        };
    }
}
