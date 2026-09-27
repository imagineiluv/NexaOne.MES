using System.Data.Common;
using NexaOne.ServiceContracts.Shp;

namespace NexaOne.Server.Gateway;

/// <summary>SLS 형제 컨텍스트의 SHP 출하 writer를 호출자 트랜잭션 그대로 전달하는 부모 proxy입니다.</summary>
public sealed class SalesOrderShipmentIntakeProxy : ISalesOrderShipmentIntake
{
    private readonly ModuleBeanResolver _resolver;

    public SalesOrderShipmentIntakeProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task CreateDraftAsync(
        DbTransaction transaction, SalesOrderShipmentDraft draft, CancellationToken ct = default)
        => _resolver.Resolve<ISalesOrderShipmentIntake>("Shp", "salesOrderShipmentIntake")
            .CreateDraftAsync(transaction, draft, ct);
}
