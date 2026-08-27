using NexaFramework;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>SYS 사용자 directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class UserDirectoryProxy : IUserDirectory
{
    public Task<bool> IsActiveAsync(string userId, CancellationToken ct = default)
        => Resolve().IsActiveAsync(userId, ct);

    private static IUserDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Sys", "userDirectory");
        return bean as IUserDirectory
            ?? throw ModuleProxy.TypeMismatch<IUserDirectory>("Sys", "userDirectory", bean);
    }
}
