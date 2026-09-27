using System.Data.Common;
using NexaOne.ServiceContracts.Shp;

namespace NexaOne.Server.Gateway;

/// <summary>SLS 형제 컨텍스트에서 SHP 출하 스냅샷을 호출자 트랜잭션으로 읽습니다.</summary>
public sealed class SalesOrderShipmentEvidenceProxy : ISalesOrderShipmentEvidence
{
    private readonly ModuleBeanResolver _resolver;

    public SalesOrderShipmentEvidenceProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<SalesOrderShipmentSnapshot?> FindAsync(
        DbTransaction transaction, string deliveryOrderId, CancellationToken ct = default)
        => _resolver.Resolve<ISalesOrderShipmentEvidence>("Shp", "salesOrderShipmentEvidence")
            .FindAsync(transaction, deliveryOrderId, ct);
}
