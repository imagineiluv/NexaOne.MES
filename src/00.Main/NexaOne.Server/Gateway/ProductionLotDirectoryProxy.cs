using NexaFramework;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>POM 생산 LOT directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class ProductionLotDirectoryProxy : IProductionLotDirectory
{
    public Task<ProductionLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default)
        => Resolve().GetLotAsync(lotId, ct);

    private static IProductionLotDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Pom", "productionLotDirectory");
        return bean as IProductionLotDirectory
            ?? throw ModuleProxy.TypeMismatch<IProductionLotDirectory>(
                "Pom", "productionLotDirectory", bean);
    }
}
