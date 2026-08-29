using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// MRP 계산에 필요한 MDM 원자료를 물리 테이블과 분리해 제공하는 축소 snapshot 계약입니다.
/// </summary>
public interface IMrpMasterDirectory : INexaModuleBridge
{
    Task<MrpMasterSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public sealed record MrpMasterSnapshot(
    IReadOnlyList<MrpBomComponentEntry> Bom,
    IReadOnlyList<MrpItemPlanningEntry> Items,
    IReadOnlyList<MrpVendorPlanningEntry> Vendors);

public sealed record MrpBomComponentEntry(
    string ProductId,
    string ComponentId,
    decimal Quantity,
    decimal ScrapRate);

public sealed record MrpItemPlanningEntry(
    string ItemId,
    decimal SafetyStock,
    int? LeadTimeDays,
    decimal LotSize,
    string? MakeOrBuy);

public sealed record MrpVendorPlanningEntry(
    string ProductId,
    int? LeadTimeDays,
    decimal? MinimumOrderQuantity);
