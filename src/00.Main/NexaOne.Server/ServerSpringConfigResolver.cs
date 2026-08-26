namespace NexaOne.Server;

/// <summary>ASP.NET Gateway와 Spring 모듈이 같은 데이터베이스 공급자 설정을 사용하도록 호스트 XML 경로를 결정한다.</summary>
internal static class ServerSpringConfigResolver
{
    /// <summary>MSSQL용 기본 Spring 루트 설정 경로다.</summary>
    internal const string MsSqlConfigPath = "config/host/server.xml";

    /// <summary>SQLite 개발·테스트용 기본 Spring 루트 설정 경로다.</summary>
    internal const string SqliteConfigPath = "config/host/server.sqlite.xml";

    /// <summary>
    /// 명시적인 <c>Server:SpringConfig</c>가 있으면 우선 사용하고, 그렇지 않으면 Gateway의
    /// <c>Database:Provider</c>와 동일한 공급자의 Spring 설정을 선택한다.
    /// </summary>
    /// <param name="configuration">호스트 및 데이터베이스 공급자 설정이다.</param>
    /// <returns>애플리케이션 기준 상대 Spring 루트 XML 경로다.</returns>
    internal static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var explicitPath = configuration["Server:SpringConfig"];
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        return string.Equals(
            configuration["Database:Provider"],
            "Sqlite",
            StringComparison.OrdinalIgnoreCase)
            ? SqliteConfigPath
            : MsSqlConfigPath;
    }

}
