using System.Data.Common;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

/// <summary>IVT 재고 출고 청구 정보를 ERP 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class StockBillingDirectoryProxy : IStockBillingDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public StockBillingDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<StockBillingOccurrence?> GetMovementInTransactionAsync(
        DbTransaction transaction,
        Guid tenantId,
        Guid organizationId,
        Guid movementId,
        CancellationToken ct = default)
        => Resolve().GetMovementInTransactionAsync(
            transaction, tenantId, organizationId, movementId, ct);

    public Task<IReadOnlyList<StockBillingOccurrence>> FindMovementsInTransactionAsync(
        DbTransaction transaction,
        Guid tenantId,
        Guid organizationId,
        IReadOnlyList<Guid> movementIds,
        CancellationToken ct = default)
        => Resolve().FindMovementsInTransactionAsync(
            transaction, tenantId, organizationId, movementIds, ct);

    private IStockBillingDirectory Resolve() =>
        _resolver.Resolve<IStockBillingDirectory>("Ivt", "stockBillingDirectory");
}
