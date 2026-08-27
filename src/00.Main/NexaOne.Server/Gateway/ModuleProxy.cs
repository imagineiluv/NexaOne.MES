namespace NexaOne.Server.Gateway;

/// <summary>형제 Spring 컨텍스트 프록시의 계약 불일치 진단을 일관되게 만듭니다.</summary>
internal static class ModuleProxy
{
    public static InvalidOperationException TypeMismatch<TContract>(
        string module,
        string beanName,
        object bean)
        where TContract : class
        => new(
            $"Module bridge bean '{module}/{beanName}' is '{bean.GetType().FullName}', "
            + $"not '{typeof(TContract).FullName}'.");
}
