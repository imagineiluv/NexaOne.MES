using NexaFramework;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>MDM MRP master snapshot을 POM 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class MrpMasterDirectoryProxy : IMrpMasterDirectory
{
    public Task<MrpMasterSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        => Resolve().GetSnapshotAsync(ct);

    private static IMrpMasterDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Mdm", "mrpMasterDirectory");
        return bean as IMrpMasterDirectory
            ?? throw ModuleProxy.TypeMismatch<IMrpMasterDirectory>("Mdm", "mrpMasterDirectory", bean);
    }
}
