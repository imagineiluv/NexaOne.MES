using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Infrastructure;

/// <summary>IVT 자재 LOT 원장을 품목별 MRP 가용 재고로 축약합니다.</summary>
public sealed class MrpInventoryDirectory : QueryRepository, IMrpInventoryDirectory
{
    public MrpInventoryDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<IReadOnlyList<MrpInventoryBalance>> GetBalancesAsync(CancellationToken ct = default)
    {
        var rows = await QueryAsync<BalanceRow>(
            "SELECT MATERIAL_ID AS MaterialId, SUM(CURRENT_QTY) AS Quantity " +
            "FROM IVT_MATERIAL_LOT GROUP BY MATERIAL_ID",
            null,
            ct);
        return rows.Select(static row => new MrpInventoryBalance(row.MaterialId, row.Quantity)).ToArray();
    }

    private sealed class BalanceRow
    {
        public string MaterialId { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
    }
}
