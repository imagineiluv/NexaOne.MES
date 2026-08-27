using NexaFramework;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

/// <summary>IVT MRP inventory snapshot을 POM 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class MrpInventoryDirectoryProxy : IMrpInventoryDirectory
{
    public Task<IReadOnlyList<MrpInventoryBalance>> GetBalancesAsync(CancellationToken ct = default)
        => Resolve().GetBalancesAsync(ct);

    private static IMrpInventoryDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Ivt", "mrpInventoryDirectory");
        return bean as IMrpInventoryDirectory
            ?? throw ModuleProxy.TypeMismatch<IMrpInventoryDirectory>("Ivt", "mrpInventoryDirectory", bean);
    }
}
