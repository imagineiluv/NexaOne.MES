using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>MDM 공정 directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class ProcessDirectoryProxy : IProcessDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public ProcessDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<bool> ProcessExistsAsync(string processId, CancellationToken ct = default)
        => Resolve().ProcessExistsAsync(processId, ct);

    private IProcessDirectory Resolve() =>
        _resolver.Resolve<IProcessDirectory>("Mdm", "processDirectory");
}
