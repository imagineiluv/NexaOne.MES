using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>SYS 사용자 directory를 QMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class UserDirectoryProxy : IUserDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public UserDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<bool> IsActiveAsync(string userId, CancellationToken ct = default)
        => Resolve().IsActiveAsync(userId, ct);

    private IUserDirectory Resolve() =>
        _resolver.Resolve<IUserDirectory>("Sys", "userDirectory");
}
