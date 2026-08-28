using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 프로젝트별 PLC/STO action adapter가 아직 설치되지 않은 호스트의 명시적 fail-closed 기본값이다.
/// FDC worker가 비활성화된 개발/CI 부팅은 허용하지만, worker를 켜면 readiness 검증에서 Plant 시작을 차단한다.
/// 실제 설비 프로젝트는 server.xml의 bean 구현만 교체해야 하며 이 타입을 운영 adapter로 사용하면 안 된다.
/// </summary>
public sealed class UnconfiguredFdcInterlockActionAdapter : IFdcInterlockActionPort
{
    private const string Reason =
        "No project PLC/STO interlock action adapter is configured. Install and HIL-verify an adapter before enabling FDC collection.";

    public Task<FdcInterlockActionReadiness> CheckReadyAsync(
        IReadOnlyCollection<string> requiredActions,
        CancellationToken ct = default) =>
        Task.FromResult(FdcInterlockActionReadiness.Unavailable(Reason));

    public Task<FdcInterlockActionResult> ApplyAsync(
        FdcInterlockActionRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new FdcInterlockActionResult(
            Acknowledged: false,
            ReadbackConfirmed: false,
            AcknowledgementId: null,
            Detail: Reason));

    public Task<FdcInterlockActionResult> ReconcileAsync(
        FdcInterlockActionRequest request,
        CancellationToken ct = default) => ApplyAsync(request, ct);

    public Task<FdcInterlockReleaseResult> ReleaseAsync(
        FdcInterlockReleaseRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new FdcInterlockReleaseResult(false, false, true, null, Reason));
}
