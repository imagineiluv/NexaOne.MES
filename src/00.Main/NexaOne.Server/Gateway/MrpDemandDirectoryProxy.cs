using NexaOne.ServiceContracts.Sls;

namespace NexaOne.Server.Gateway;

/// <summary>SLS 소유 MRP 수요 디렉터리를 POM 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class MrpDemandDirectoryProxy : IMrpDemandDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public MrpDemandDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<IReadOnlyList<MrpDemand>> GetOpenDemandsAsync(CancellationToken ct = default)
        => Resolve().GetOpenDemandsAsync(ct);

    private IMrpDemandDirectory Resolve() =>
        _resolver.Resolve<IMrpDemandDirectory>("Sls", "mrpDemandDirectory");
}
