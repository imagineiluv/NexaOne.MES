using NexaFramework;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.Server.Gateway;

/// <summary>
/// FDC와 IVT 형제 Spring 컨텍스트 사이의 부모 프록시다. 조회 SQL과 페이징 정책은 FDC에
/// 남기고, 호스트는 Default ALC의 Common 계약으로만 실제 FDC 빈을 찾아 위임한다.
/// </summary>
public sealed class FdcTraceSourceProxy : IFdcTraceSource
{
    public Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
        IReadOnlyCollection<FdcTraceReadScope> scopes,
        int maxCount,
        CancellationToken ct = default)
    {
        var bean = ApplicationServer.GetInstance().GetBean("Fdc", "fdcTraceSource");
        if (bean is not IFdcTraceSource source)
        {
            throw new InvalidOperationException(
                $"Module bridge bean 'Fdc/fdcTraceSource' is '{bean.GetType().FullName}', "
                + $"not '{typeof(IFdcTraceSource).FullName}'.");
        }

        return source.ReadAsync(scopes, maxCount, ct);
    }
}
