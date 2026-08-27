using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>MRP가 소비하는 IVT 품목별 가용 재고 snapshot 계약입니다.</summary>
[NexaModuleBridge("Ivt", "mrpInventoryDirectory")]
public interface IMrpInventoryDirectory : INexaModuleBridge
{
    Task<IReadOnlyList<MrpInventoryBalance>> GetBalancesAsync(CancellationToken ct = default);
}

public sealed record MrpInventoryBalance(string MaterialId, decimal Quantity);
