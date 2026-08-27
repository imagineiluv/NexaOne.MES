using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Infrastructure;

/// <summary>MDM이 소유한 BOM·계획·조달 마스터를 MRP용 축소 snapshot으로 제공합니다.</summary>
public sealed class MrpMasterDirectory : QueryRepository, IMrpMasterDirectory
{
    public MrpMasterDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<MrpMasterSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var bom = await QueryAsync<BomRow>(
            "SELECT PRODUCT_ID AS ProductId, COMPONENT_ID AS ComponentId, QUANTITY AS Quantity, " +
            "COALESCE(SCRAP_RATE, 0) AS ScrapRate FROM MDM_BOM",
            null,
            ct);
        var items = await QueryAsync<ItemRow>(
            "SELECT ITEM_ID AS ItemId, SAFETY_STOCK AS SafetyStock, LEAD_TIME_DAYS AS LeadTimeDays, " +
            "LOT_SIZE AS LotSize, MAKE_OR_BUY AS MakeOrBuy FROM MDM_ITEM_PLANNING WHERE IS_ACTIVE = 'Y'",
            null,
            ct);
        var vendors = await QueryAsync<VendorRow>(
            "SELECT PRODUCT_ID AS ProductId, MIN(LEAD_TIME_DAYS) AS LeadTimeDays, MIN(MOQ) AS MinimumOrderQuantity " +
            "FROM MDM_VENDOR_ITEM GROUP BY PRODUCT_ID",
            null,
            ct);

        return new MrpMasterSnapshot(
            bom.Select(static row => new MrpBomComponentEntry(
                row.ProductId, row.ComponentId, row.Quantity, row.ScrapRate)).ToArray(),
            items.Select(static row => new MrpItemPlanningEntry(
                row.ItemId, row.SafetyStock, row.LeadTimeDays, row.LotSize, row.MakeOrBuy)).ToArray(),
            vendors.Select(static row => new MrpVendorPlanningEntry(
                row.ProductId, row.LeadTimeDays, row.MinimumOrderQuantity)).ToArray());
    }

    private sealed class BomRow
    {
        public string ProductId { get; set; } = string.Empty;
        public string ComponentId { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal ScrapRate { get; set; }
    }

    private sealed class ItemRow
    {
        public string ItemId { get; set; } = string.Empty;
        public decimal SafetyStock { get; set; }
        public int? LeadTimeDays { get; set; }
        public decimal LotSize { get; set; }
        public string? MakeOrBuy { get; set; }
    }

    private sealed class VendorRow
    {
        public string ProductId { get; set; } = string.Empty;
        public int? LeadTimeDays { get; set; }
        public decimal? MinimumOrderQuantity { get; set; }
    }
}
