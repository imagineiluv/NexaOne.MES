using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Infrastructure;

/// <summary>POM 생산 LOT를 축소 snapshot으로 제공하는 owner adapter입니다.</summary>
public sealed class ProductionLotDirectory : QueryRepository, IProductionLotDirectory
{
    public ProductionLotDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<ProductionLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT LOT_ID AS LotId, PRODUCT_ID AS ProductId
                             FROM POM_LOT
                             WHERE LOT_ID = @lotId";
        return await QueryFirstOrDefaultAsync<ProductionLotDirectoryEntry>(
            sql,
            new { lotId },
            ct);
    }
}
