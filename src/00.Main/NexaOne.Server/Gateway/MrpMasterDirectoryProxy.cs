using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>MDM MRP master snapshot을 POM 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class MrpMasterDirectoryProxy : IMrpMasterDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public MrpMasterDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<MrpMasterSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        => Resolve().GetSnapshotAsync(ct);

    private IMrpMasterDirectory Resolve() =>
        _resolver.Resolve<IMrpMasterDirectory>("Mdm", "mrpMasterDirectory");
}
