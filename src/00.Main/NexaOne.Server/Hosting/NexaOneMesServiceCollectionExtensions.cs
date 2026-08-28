using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Infrastructure.Diagnostics;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;
using NexaOne.Web.Services.Meta;
using NexaFramework;
using Radzen;

namespace NexaOne.Server;

/// <summary>
/// MES 애플리케이션 서비스의 단일 조립 진입점이다.
/// Gateway, 인증, HTTP, Blazor를 등록하되 Spring 컨텍스트 생성은 빌드 이후 호스트 수명주기에 맡긴다.
/// </summary>
public static class NexaOneMesServiceCollectionExtensions
{
    /// <summary>
    /// MES 모듈 런타임, Gateway, 인증, 실시간 통신 및 UI 서비스를 DI 컨테이너에 등록한다.
    /// 등록 단계에는 Spring 시작이나 데이터베이스 변경 같은 실행 부작용이 없다.
    /// </summary>
    /// <param name="services">서비스를 추가할 DI 컬렉션이다.</param>
    /// <param name="configuration">MES와 프레임워크가 공유할 애플리케이션 설정이다.</param>
    /// <param name="configureHosting">호스트 공개 경로를 선택적으로 조정하는 콜백이다.</param>
    /// <returns>구성이 적용된 서비스 컬렉션을 반환한다.</returns>
    public static IServiceCollection AddNexaOneMes(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NexaOneMesHostingOptions>? configureHosting = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(NexaOneMesRegistration)))
            throw new InvalidOperationException("AddNexaOneMes() can only be called once for a service collection.");

        var hosting = new NexaOneMesHostingOptions();
        configureHosting?.Invoke(hosting);
        ValidateHostingOptions(hosting);
        services.AddSingleton<NexaOneMesRegistration>();
        services.TryAddSingleton<IConfiguration>(configuration);
        services.AddSingleton(hosting);
        services.Configure<HostOptions>(configuration.GetSection("Host"));

        // 등록 단계는 부작용 없이 유지한다. Spring 컨텍스트와 워커는 WebApplication.Build 성공 후
        // 가장 먼저 실행되는 수명주기 서비스가 생성한다.
        AddModuleRuntimeServices(services, configuration);
        // GatewayLoginService와 DB probe가 같은 데이터 소스를 사용하므로 명명 쿼리 경로를 먼저 등록한다.
        services.AddNexaOneGateway(configuration);
        // DB/PLC catalog/message-bus readiness는 resource lifecycle을 소유하지 않는 product probe다.
        // 따라서 NexaFramework DriverHost로 포장하지 않고 이 단일 product catalog 뒤에 둔다.
        services.AddSingleton<FdcPlcProtocolCatalogProbe>();
        services.AddSingleton<IExternalDependencyProbe>(sp =>
            sp.GetRequiredService<FdcPlcProtocolCatalogProbe>());
        services.AddSingleton<NexaOneDatabaseProbe>();
        services.AddSingleton<IExternalDependencyProbe>(sp =>
            sp.GetRequiredService<NexaOneDatabaseProbe>());
        services.AddSingleton<NexaOneMessageBusProbe>();
        services.AddSingleton<IExternalDependencyProbe>(sp =>
            sp.GetRequiredService<NexaOneMessageBusProbe>());
        services.AddSingleton<ExternalDependencyProbeCatalog>();
        InsertHostedServiceFirst<NexaOneMesStartupHostedService>(services);

        services.AddScoped<BatchProcessRunner>();
        services.AddSingleton<IHostedService>(sp => new BatchProcessWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<NexaFramework.Scheduling.IRecurringScheduler>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<BatchProcessWorker>>()));

        services.AddSingleton<NexaOne.Server.Realtime.ScreenRefreshNotifier>();
        services.AddSingleton<IScreenRefreshNotifier>(
            sp => sp.GetRequiredService<NexaOne.Server.Realtime.ScreenRefreshNotifier>());
        services.AddSingleton<NexaOne.Server.Realtime.RealtimeAlertFeed>();
        services.AddSingleton<IRealtimeAlertFeed>(
            sp => sp.GetRequiredService<NexaOne.Server.Realtime.RealtimeAlertFeed>());

        services.AddNexaOneAuth(configuration);
        AddOptionalDatabaseLogging(services, configuration);

        services.AddSingleton<IErrorLocalizer, ErrorLocalizer>();
        services.AddControllers(options => options.Filters.Add<ErrorLocalizationFilter>())
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter()));
        services.AddRadzenComponents();
        services.AddHttpContextAccessor();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddHealthChecks()
            .AddCheck<ExternalDependencyHealthCheck>("required-external-dependencies");

        services.AddSignalR();
        services.AddSingleton<NexaOne.Server.Realtime.IEesHubNotifier, NexaOne.Server.Realtime.EesHubNotifier>();
        services.TryAddSingleton<NexaOne.Common.Telemetry.ActiveUserTracker>();

        AddJwtAuthentication(services, configuration, hosting);
        services.AddAuthorization();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddScoped<JwtAuthStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());
        services.AddScoped<AuthTokenService>();
        services.AddScoped<AuthContextService>();
        services.AddScoped<IAuthContext>(sp => sp.GetRequiredService<AuthContextService>());
        services.AddScoped<ApiNotificationService>();
        services.AddScoped<UiTextService>();
        // 메타 bridge:* 액션은 프로그램별 raw SQL이 아니라 등록된 typed 드라이버를 통해 실행한다.
        // POM 드라이버는 기존 JWT 보호 REST API를 호출하므로 권한/버전/멱등/감사 계약이 한 경계에 유지된다.
        services.AddScoped<IMetaCommandDriver, PomWorkOrderMetaCommandDriver>();
        services.AddScoped<IMetaCommandDriver, PomLotRoutingMetaCommandDriver>();
        services.AddScoped<IMetaCommandDriver, QmsInspectionMetaCommandDriver>();
        services.AddScoped<IMetaCommandDriver, MrpConversionMetaCommandDriver>();
        services.TryAddScoped<IMetaCommandDriverCatalog, MetaCommandDriverCatalog>();
        // 메타 화면의 표시 권한도 QueryGateway/bridge와 같은 카탈로그를 사용한다.
        services.TryAddScoped<MetaPermissionCatalog>();
        services.TryAddScoped<IMetaPermissionCatalog>(sp => sp.GetRequiredService<MetaPermissionCatalog>());
        services.TryAddScoped<IMetaPermissionEvaluator>(sp => sp.GetRequiredService<MetaPermissionCatalog>());
        services.AddScoped<NexaOne.Server.Services.OpenedScreensState>();
        services.AddSingleton<CodeScreenDefinitionCatalog>();
        services.AddSingleton<ICodeScreenDefinitionCatalog>(sp =>
            sp.GetRequiredService<CodeScreenDefinitionCatalog>());
        services.AddSingleton(sp => new GatewayScreenDefinitionProvider(
            sp.GetRequiredService<ICodeScreenDefinitionCatalog>(),
            sp.GetRequiredService<IRuleDispatcher>(),
            sp.GetRequiredService<IQueryRegistry>(),
            sp.GetRequiredService<ILogger<GatewayScreenDefinitionProvider>>()));
        services.AddSingleton<IScreenDefinitionProvider>(sp =>
            sp.GetRequiredService<GatewayScreenDefinitionProvider>());
        // Meta command driver/catalog는 요청별 IApiClient·JWT context를 사용하므로 scoped다.
        // Binding validator와 이를 소비하는 seed service도 같은 수명으로 맞춰 singleton→scoped 포획을 막는다.
        services.AddScoped<IScreenDefinitionBindingValidator, ScreenDefinitionBindingValidator>();
        services.AddScoped<IScreenDefinitionSeedService, ScreenDefinitionSeedService>();

        var apiBase = configuration["ApiBaseUrl"]
            ?? $"http://localhost:{configuration.GetValue("Server:Port", 8080)}/";
        services.AddTransient<DefaultRequestTimeoutHandler>();
        services.AddHttpClient<IApiClient, ApiClient>(client =>
        {
            client.BaseAddress = new Uri(apiBase);
            client.Timeout = TimeSpan.FromMinutes(10);
        }).AddHttpMessageHandler<DefaultRequestTimeoutHandler>();

        AddRateLimiting(services);
        return services;
    }

    /// <summary>
    /// Spring 기반 MES 모듈 상태와 지연 로딩 Bridge 및 스케줄러 어댑터를 등록한다.
    /// </summary>
    private static void AddModuleRuntimeServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // MES 스케줄러의 실제 소유자는 Spring 루트 컨텍스트다. 프레임워크 기본 구현만 제거하고
        // 호스트가 직접 제공한 descriptor는 보존한다. 모듈 사용 시 아래 MES 어댑터가 마지막에 등록되어
        // 단일 IRecurringScheduler의 기본 해석 대상이 되며, 기존 구현은 IEnumerable 조회에 남는다.
        var defaultSchedulers = services.Where(descriptor =>
                descriptor.ServiceType == typeof(NexaFramework.Scheduling.IRecurringScheduler)
                && descriptor.ImplementationType == typeof(NexaFramework.Scheduling.QuartzScheduler))
            .ToList();
        foreach (var descriptor in defaultSchedulers) services.Remove(descriptor);

        var modulesEnabled = configuration.GetValue("Server:Modules:Enabled", true);
        services.TryAddSingleton(ApplicationServer.GetInstance());
        services.AddSingleton(sp => new NexaOneMesRuntimeState(
            sp.GetRequiredService<ApplicationServer>(), configuration, modulesEnabled));

        // 구현 플러그인 DLL은 별도 ALC에서 Spring이 생성한다. 계약↔Bean 연결 메타데이터는
        // 공유 계약 어셈블리를 reflection scan하지 않고 제품 composition root가 명시적으로 소유한다.
        var bridgeCatalog = NexaOneMesBridgeCatalog.Create();
        services.AddSingleton<INexaModuleBridgeCatalog>(bridgeCatalog);

        if (!modulesEnabled) return;

        services.AddSingleton<NexaFramework.Scheduling.IRecurringScheduler, NexaOneMesRecurringScheduler>();
        foreach (var descriptor in bridgeCatalog.Descriptors)
        {
            // 프로젝트별 plugin/adapter가 같은 공용 계약을 먼저 등록하면 그 선택을 보존한다.
            // 기본 구현은 Spring 모듈의 공개 bridge bean을 지연 해석한다.
            services.TryAddSingleton(descriptor.ContractType, serviceProvider =>
                serviceProvider.GetRequiredService<NexaOneMesRuntimeState>().GetBridge(
                    descriptor.ContractType,
                    descriptor.Module,
                    descriptor.BeanName));
        }
    }

    /// <summary>DB 로그가 활성화된 경우 비동기 로그 채널과 저장 워커를 등록한다.</summary>
    private static void AddOptionalDatabaseLogging(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue("AppLogging:Db:Enabled", false)) return;

        // 로그 폭주가 업무 요청 메모리를 잠식하지 않도록 용량을 제한하고 가장 오래된 항목부터 버린다.
        var channel = System.Threading.Channels.Channel.CreateBounded<NexaOne.Server.Logging.AppLogEntry>(
            new System.Threading.Channels.BoundedChannelOptions(1000)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
        services.AddSingleton<ILoggerProvider>(new NexaOne.Server.Logging.DbLoggerProvider(channel.Writer));
        services.AddHostedService(sp => new NexaOne.Server.Logging.AppLogFlushWorker(
            channel.Reader, sp.GetRequiredService<IRuleDispatcher>()));
    }

    /// <summary>
    /// JWT 서명 및 검증 정책을 구성하고 SignalR 연결의 쿼리 토큰을 허브 경로에서만 허용한다.
    /// </summary>
    private static void AddJwtAuthentication(
        IServiceCollection services,
        IConfiguration configuration,
        NexaOneMesHostingOptions hosting)
    {
        var jwt = configuration.GetSection("Jwt");
        var secretKey = jwt["SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey is required");
        if (secretKey.StartsWith("CHANGE_ME", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(secretKey) < 32)
            throw new InvalidOperationException(
                "Jwt:SecretKey must be a strong secret (>= 32 bytes) supplied via environment variable or user-secrets; " +
                "the committed placeholder is rejected.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt["Issuer"],
                    ValidAudience = jwt["Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                    ClockSkew = TimeSpan.Zero,
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // WebSocket 연결은 Authorization 헤더를 쓰기 어려워 허브 요청에 한해 쿼리 토큰을 받는다.
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken)
                            && context.HttpContext.Request.Path.StartsWithSegments(hosting.RealtimeHubPath))
                            context.Token = accessToken;
                        return Task.CompletedTask;
                    },
                };
            });
    }

    /// <summary>일반 API와 인증 API에 각각 사용자/IP 기준 고정 구간 요청 제한을 적용한다.</summary>
    private static void AddRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var key = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });
    }

    /// <summary>잘못된 상대 경로가 런타임 라우팅 오류로 이어지기 전에 호스트 경로 설정을 검증한다.</summary>
    private static void ValidateHostingOptions(NexaOneMesHostingOptions options)
    {
        // 모든 공개 경로에 동일한 절대 애플리케이션 경로 규칙을 적용한다.
        static void RequirePath(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('/'))
                throw new ArgumentException($"{name} must be an absolute application path beginning with '/'.", name);
        }

        RequirePath(options.LoginPath, nameof(options.LoginPath));
        RequirePath(options.HealthPath, nameof(options.HealthPath));
        RequirePath(options.DiagnosticsPath, nameof(options.DiagnosticsPath));
        RequirePath(options.RealtimeHubPath, nameof(options.RealtimeHubPath));
        RequirePath(options.PortalIndexFile, nameof(options.PortalIndexFile));
        RequirePath(options.DesignerFallbackPattern, nameof(options.DesignerFallbackPattern));
        RequirePath(options.PortalFallbackPattern, nameof(options.PortalFallbackPattern));
    }

    /// <summary>
    /// MES 시작 장벽이 다른 HostedService보다 앞서도록 수명주기 서비스를 첫 등록 위치에 삽입한다.
    /// </summary>
    private static void InsertHostedServiceFirst<THostedService>(IServiceCollection services)
        where THostedService : class, IHostedService
    {
        var firstHostedService = services
            .Select((descriptor, index) => (descriptor, index))
            .FirstOrDefault(item => item.descriptor.ServiceType == typeof(IHostedService));
        var insertAt = firstHostedService.descriptor is null ? services.Count : firstHostedService.index;
        services.Insert(insertAt, ServiceDescriptor.Singleton<IHostedService, THostedService>());
    }
}
