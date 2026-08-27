using NexaFramework;

namespace NexaOne.Server.Gateway;

/// <summary>
/// Spring 형제 컨텍스트의 공개 Bean을 공용 계약으로 해석하는 조립 경계입니다.
/// 프록시는 프로세스 전역 singleton을 직접 찾지 않고 XML 조립 루트가 주입한 resolver만 사용합니다.
/// </summary>
public sealed class ModuleBeanResolver
{
    private readonly ApplicationServer _server;

    public ModuleBeanResolver(ApplicationServer server)
        => _server = server ?? throw new ArgumentNullException(nameof(server));

    public TContract Resolve<TContract>(string module, string beanName)
        where TContract : class
    {
        var bean = _server.GetBean(module, beanName);
        return bean as TContract
            ?? throw new InvalidOperationException(
                $"Module bridge bean '{module}/{beanName}' is '{bean.GetType().FullName}', "
                + $"not '{typeof(TContract).FullName}'.");
    }
}
