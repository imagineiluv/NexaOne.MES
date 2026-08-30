namespace NexaOne.Server;

/// <summary>배포별 Spring service와 project plugin 구성을 선언하는 애플리케이션 매니페스트 경로를 결정한다.</summary>
internal static class ServerApplicationManifestResolver
{
    /// <summary>현재 Cleaner 제품 구성을 유지하는 기본 애플리케이션 매니페스트 경로다.</summary>
    internal const string DefaultManifestPath = "config/app.xml";

    /// <summary>
    /// 신뢰된 로컬 배포 설정의 <c>Server:ApplicationManifest</c>를 사용하고, 누락되거나
    /// 공백뿐이면 현재 제품의 기본 매니페스트를 선택한다.
    /// </summary>
    internal static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredPath = configuration["Server:ApplicationManifest"]?.Trim();
        return string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultManifestPath
            : configuredPath;
    }
}
