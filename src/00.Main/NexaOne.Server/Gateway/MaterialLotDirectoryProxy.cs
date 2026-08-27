using NexaFramework;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

/// <summary>IVT 자재 LOT directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class MaterialLotDirectoryProxy : IMaterialLotDirectory
{
    public Task<MaterialLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default)
        => Resolve().GetLotAsync(lotId, ct);

    private static IMaterialLotDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Ivt", "materialLotDirectory");
        return bean as IMaterialLotDirectory
            ?? throw ModuleProxy.TypeMismatch<IMaterialLotDirectory>(
                "Ivt", "materialLotDirectory", bean);
    }
}
