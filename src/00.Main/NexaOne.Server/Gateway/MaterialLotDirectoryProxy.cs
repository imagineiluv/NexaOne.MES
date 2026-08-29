using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

/// <summary>IVT 자재 LOT directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class MaterialLotDirectoryProxy : IMaterialLotDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public MaterialLotDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<MaterialLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default)
        => Resolve().GetLotAsync(lotId, ct);

    private IMaterialLotDirectory Resolve() =>
        _resolver.Resolve<IMaterialLotDirectory>("Ivt", "materialLotDirectory");
}
