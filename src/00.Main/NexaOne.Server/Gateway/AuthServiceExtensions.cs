using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application.Auth;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 인증 DI(무-브리지). IJwtService + DB-backed IRefreshTokenStore + GatewayLoginService를 등록한다.
/// 인증 명명 쿼리는 공개 게이트웨이(db/queries)와 분리된 db/queries-auth 전용 레지스트리로 로드해 노출을 막는다.
/// AddNexaOneGateway(IRuleDispatcher 등록) 이후에 호출해야 한다.</summary>
public static class AuthServiceExtensions
{
    public static IServiceCollection AddNexaOneAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var dialect = string.Equals(configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase)
            ? "sqlite" : "mssql";
        // 격리 인증 레지스트리(공개 IQueryRegistry 싱글톤과 별개). 루트는 Auth:Query:Directory override 또는
        // BaseDirectory 상위탐색으로 db/queries-auth를 찾는다.
        var authRoot = ResolveAuthQueriesRoot(configuration["Auth:Query:Directory"]);
        if (authRoot is null)
            throw new InvalidOperationException(
                "통합 호스트 인증: db/queries-auth 디렉터리를 찾을 수 없습니다(출력 복사/배포 확인). " +
                "공개 db/queries로의 무음 폴백을 막기 위해 기동 시 실패한다.");
        var authRegistry = FileQueryRegistry.Load(dialect, authRoot);

        services.AddSingleton<IJwtService, JwtService>();

        var ttl = TimeSpan.FromDays(configuration.GetValue("Jwt:RefreshTokenExpiryDays", 7));
        services.AddSingleton<IRefreshTokenStore>(sp => new SysRefreshTokenStore(
            sp.GetRequiredService<IRuleDispatcher>(), authRegistry, sp.GetRequiredService<IJwtService>(), ttl));

        services.AddSingleton(sp => new GatewayLoginService(
            sp.GetRequiredService<IRuleDispatcher>(), authRegistry,
            sp.GetRequiredService<IJwtService>(), sp.GetRequiredService<IRefreshTokenStore>()));

        return services;
    }

    // override가 있으면 그 디렉터리를, 없으면 BaseDirectory에서 상위로 db/queries-auth를 찾는다(db/queries 규약과 동형).
    private static string? ResolveAuthQueriesRoot(string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Directory.Exists(overrideDirectory) ? overrideDirectory : null;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var p = Path.Combine(d.FullName, "db", "queries-auth");
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }
}
