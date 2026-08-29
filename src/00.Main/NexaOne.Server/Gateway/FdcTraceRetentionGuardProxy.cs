using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.Server.Gateway;

/// <summary>
/// FDC와 IVT 형제 Spring 컨텍스트 사이의 보존 보호 프록시다. IVT가 binding/cursor SQL을
/// 소유하고, FDC는 이 축소 계약으로 계산된 low-watermark만 사용한다.
/// </summary>
public sealed class FdcTraceRetentionGuardProxy : IFdcTraceRetentionGuard
{
    private readonly ModuleBeanResolver _resolver;

    public FdcTraceRetentionGuardProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<DateTime?> GetLowWatermarkAsync(CancellationToken ct = default) =>
        _resolver.Resolve<IFdcTraceRetentionGuard>("Ivt", "fdcTraceRetentionGuard")
            .GetLowWatermarkAsync(ct);
}
