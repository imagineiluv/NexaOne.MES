using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Fdc;

/// <summary>
/// FDC raw TRACE 보존정리가 아직 durable ingestion을 끝내지 않은 소비자 표본을 지우지 않도록
/// 소비 모듈이 제공하는 축소 보호 계약이다. 반환값은 모든 활성 소비 범위의 전역 low-watermark이며,
/// 활성 범위가 없으면 <see langword="null"/>을 반환한다.
/// </summary>
public interface IFdcTraceRetentionGuard : INexaModuleBridge
{
    Task<DateTime?> GetLowWatermarkAsync(CancellationToken ct = default);
}
