using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;
using System.Net.Mail;
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
        // 구체 등록(정리 워커가 PurgeExpiredAsync 호출) + 인터페이스 alias(기존 소비자 무변경).
        services.AddSingleton(sp => new SysRefreshTokenStore(
            sp.GetRequiredService<IRuleDispatcher>(), authRegistry, sp.GetRequiredService<IJwtService>(), ttl));
        services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<SysRefreshTokenStore>());

        // 만료 토큰 정리 워커(호스트 레벨). 기본 OFF — Auth:RefreshTokenCleanup:Enabled=true로 켠다.
        var cleanupEnabled = configuration.GetValue("Auth:RefreshTokenCleanup:Enabled", false);
        var cleanupInterval = TimeSpan.FromSeconds(configuration.GetValue("Auth:RefreshTokenCleanup:IntervalSeconds", 86400));
        var cleanupRetention = TimeSpan.FromDays(configuration.GetValue("Auth:RefreshTokenCleanup:RetentionDays", 7));
        services.AddHostedService(sp => new RefreshTokenCleanupWorker(
            sp.GetRequiredService<SysRefreshTokenStore>(), cleanupEnabled, cleanupInterval, cleanupRetention));

        // 메일 발송(forgot-password 토큰 전달) — 기본 무발송. 활성화된 SMTP는 불완전한 설정으로
        // NullEmailSender에 폴백하지 않고 조립 단계에서 실패해 비밀번호 재설정 메일의 무음 유실을 막는다.
        var smtpEnabled = configuration.GetValue("Email:Smtp:Enabled", false);
        if (smtpEnabled)
            services.AddSingleton<IEmailSender>(CreateSmtpEmailSender(configuration));
        else
            services.AddSingleton<IEmailSender>(new NullEmailSender());

        services.AddSingleton(sp => new GatewayLoginService(
            sp.GetRequiredService<IRuleDispatcher>(), authRegistry,
            sp.GetRequiredService<IJwtService>(), sp.GetRequiredService<IRefreshTokenStore>(),
            sp.GetRequiredService<IEmailSender>()));

        return services;
    }

    private static SmtpEmailSender CreateSmtpEmailSender(IConfiguration configuration)
    {
        var host = configuration["Email:Smtp:Host"]?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            throw InvalidSmtpConfiguration("Email:Smtp:Host", "a non-blank SMTP host is required");
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw InvalidSmtpConfiguration("Email:Smtp:Host", "the SMTP host is not a valid DNS name or IP address");

        var rawPort = configuration["Email:Smtp:Port"]?.Trim();
        if (!int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > IPEndPoint.MaxPort)
            throw InvalidSmtpConfiguration("Email:Smtp:Port", "an integer from 1 through 65535 is required");

        var sender = configuration["Email:Smtp:Sender"]?.Trim();
        if (string.IsNullOrWhiteSpace(sender)
            || !MailAddress.TryCreate(sender, out var parsedSender)
            || !string.Equals(parsedSender.Address, sender, StringComparison.OrdinalIgnoreCase)
            || parsedSender.Host.Length == 0
            || Uri.CheckHostName(parsedSender.Host) == UriHostNameType.Unknown)
            throw InvalidSmtpConfiguration("Email:Smtp:Sender", "a valid sender email address is required");

        return new SmtpEmailSender(
            host,
            port,
            sender,
            configuration["Email:Smtp:User"],
            configuration["Email:Smtp:Password"],
            configuration.GetValue("Email:Smtp:UseSsl", true));
    }

    private static InvalidOperationException InvalidSmtpConfiguration(string key, string reason)
        => new($"SMTP is enabled but '{key}' is invalid: {reason}. " +
               "Disable Email:Smtp:Enabled or provide a complete SMTP configuration.");

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
