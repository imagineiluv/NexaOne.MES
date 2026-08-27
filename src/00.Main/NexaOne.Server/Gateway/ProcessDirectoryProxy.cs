using NexaFramework;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>MDM 공정 directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class ProcessDirectoryProxy : IProcessDirectory
{
    public Task<bool> ProcessExistsAsync(string processId, CancellationToken ct = default)
        => Resolve().ProcessExistsAsync(processId, ct);

    private static IProcessDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Mdm", "processDirectory");
        return bean as IProcessDirectory
            ?? throw ModuleProxy.TypeMismatch<IProcessDirectory>("Mdm", "processDirectory", bean);
    }
}
