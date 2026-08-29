using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>POM 생산 LOT directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class ProductionLotDirectoryProxy : IProductionLotDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public ProductionLotDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<ProductionLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default)
        => Resolve().GetLotAsync(lotId, ct);

    private IProductionLotDirectory Resolve() =>
        _resolver.Resolve<IProductionLotDirectory>("Pom", "productionLotDirectory");
}
