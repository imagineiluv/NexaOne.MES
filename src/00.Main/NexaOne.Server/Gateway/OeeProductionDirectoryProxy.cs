using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>POM OEE 생산 증거 directory를 EST 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class OeeProductionDirectoryProxy : IOeeProductionDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public OeeProductionDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<OeeProductionWindowDto> LoadProductionAsync(
        string plantId,
        string equipmentId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
        => _resolver.Resolve<IOeeProductionDirectory>("Pom", "oeeProductionDirectory")
            .LoadProductionAsync(plantId, equipmentId, fromUtc, toUtc, ct);
}
