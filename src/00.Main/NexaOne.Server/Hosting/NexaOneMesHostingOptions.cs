namespace NexaOne.Server;

/// <summary>
/// MES 호스트가 노출하는 공용 HTTP 경로를 정의한다.
/// 기본값은 통합 서버와 동일하므로 다른 호스트도 라우트 구성을 복사하지 않고 재사용할 수 있다.
/// </summary>
public sealed class NexaOneMesHostingOptions
{
    // Blazor/Portal에 컴파일된 경로는 변경 시 클라이언트 라우트와 어긋나므로 읽기 전용으로 유지한다.
    /// <summary>인증되지 않은 사용자가 이동할 MES 로그인 경로다.</summary>
    public string LoginPath => "/login";

    /// <summary>외부 상태 점검에서 사용할 헬스 체크 경로다.</summary>
    public string HealthPath { get; set; } = "/health";

    /// <summary>인증된 운영자가 모듈과 워커 상태를 확인할 진단 경로다.</summary>
    public string DiagnosticsPath { get; set; } = "/diag";

    /// <summary>MES 실시간 이벤트를 전달하는 SignalR 허브 경로다.</summary>
    public string RealtimeHubPath { get; set; } = "/hubs/smartees";

    /// <summary>Portal SPA의 정적 진입 파일 경로다.</summary>
    public string PortalIndexFile => "/spa/index.html";

    /// <summary>Designer의 클라이언트 라우팅을 처리할 비파일 폴백 패턴이다.</summary>
    public string DesignerFallbackPattern => "/Designer/{*path:nonfile}";

    /// <summary>Portal SPA의 클라이언트 라우팅을 처리할 비파일 폴백 패턴이다.</summary>
    public string PortalFallbackPattern => "/spa/{*path:nonfile}";
}

/// <summary>AddNexaOneMes 중복 호출을 조기에 감지하기 위한 DI 등록 표식이다.</summary>
internal sealed class NexaOneMesRegistration { }
