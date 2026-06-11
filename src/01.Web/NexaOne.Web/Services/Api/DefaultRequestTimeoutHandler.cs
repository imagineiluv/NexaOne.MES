namespace NexaOne.Web.Services.Api;

/// <summary>
/// §20.11 — HttpClient.Timeout은 클라이언트 전역이라 요청별로 늘릴 수 없다.
/// 전역 한도는 배포 업로드 상한(10분, Program.cs)으로 두고,
/// 본 핸들러가 일반 요청에 기본 100초를 강제해 행 걸린 호출이 UI를 10분간 막는 일을 방지한다.
/// </summary>
public sealed class DefaultRequestTimeoutHandler : DelegatingHandler
{
    /// <summary>일반 API 호출 상한 — HttpClient 기본값과 동일한 100초.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    /// <summary>대용량 업로드 등 장시간 요청 표시 — true면 전역 Timeout만 적용한다.</summary>
    public static readonly HttpRequestOptionsKey<bool> LongRunning = new("NexaOne.LongRunning");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(LongRunning, out var longRunning) && longRunning)
            return await base.SendAsync(request, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultTimeout);
        // 타임아웃 시 HttpClient.Timeout과 동일하게 TaskCanceledException — 호출부 처리 경로가 달라지지 않는다
        return await base.SendAsync(request, cts.Token);
    }
}
