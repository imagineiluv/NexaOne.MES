using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

/// <summary>IVT MRP inventory snapshot을 POM 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class MrpInventoryDirectoryProxy : IMrpInventoryDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public MrpInventoryDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<IReadOnlyList<MrpInventoryBalance>> GetBalancesAsync(CancellationToken ct = default)
        => Resolve().GetBalancesAsync(ct);

    private IMrpInventoryDirectory Resolve() =>
        _resolver.Resolve<IMrpInventoryDirectory>("Ivt", "mrpInventoryDirectory");
}
