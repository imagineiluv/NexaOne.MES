using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using NexaOne.API.Extensions;
using NexaOne.API.Hubs;
using NexaOne.Application;
using NexusFramework.Workflow.Nodes;
using NexusFramework.Workflow.Tooling;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// §17.3 — Serilog 구조화 로그. 싱크/레벨은 appsettings의 Serilog 섹션으로 구성하고,
// 요청 공통 필드(CorrelationId/UserId/PlantId)는 RequestLogContextMiddleware가 주입한다.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Services
// 도메인 enum(상태)을 문자열로 직렬화 — Web DTO(string Status/State)와 계약 일치
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "NexaOne API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header
    });
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// JWT Authentication
var jwtSection = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSection["SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey is required");
// §18.7 — 비밀키는 파일에 두지 않는다. 커밋된 플레이스홀더/취약 키로 조용히 기동되면 HMAC 대칭키가
// 노출돼 토큰 위조로 인증이 우회되므로, 부팅 시 플레이스홀더·32바이트 미만 키를 거부한다(환경변수/UserSecrets로 주입).
if (secretKey.StartsWith("CHANGE_ME", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(secretKey) < 32)
    throw new InvalidOperationException(
        "Jwt:SecretKey must be a strong secret (>= 32 bytes) supplied via environment variable or user-secrets; " +
        "the committed placeholder is rejected.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // §18 심층 방어 — 전역 FallbackPolicy: [Authorize]/정책이 명시되지 않은 엔드포인트도 기본 인증을 요구한다.
    // 컨트롤러에 [Authorize]를 빠뜨려도 익명 노출되지 않게 하는 안전망. 익명 진입점(login/refresh/forgot/
    // reset/register/exists/deploy 다운로드·최신/health)은 [AllowAnonymous]로 명시 예외 처리한다.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
// ADR-003 — 권한 기반 PEP: "perm:{permission}" 정책을 동적 생성하고 permission 클레임으로 집행.
// 역할 기반 [Authorize(Roles=...)]는 기본 제공자에 위임되어 그대로 동작(추가형).
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
    NexaOne.API.Security.PermissionPolicyProvider>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    NexaOne.API.Security.PermissionAuthorizationHandler>();
builder.Services.AddSignalR();

// §18.2.3 — Rate Limiting: 기본은 인증 사용자별(없으면 IP별) 100req/min.
// login/forgot-password/register 같은 익명 진입점은 별도 "auth" 정책(IP당 10req/min)으로
// 브루트포스/계정 열거 시도를 차단한다. 거부 응답은 429.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // 설계 표준의 Identity.Name 대신 토큰의 NameIdentifier 클레임 사용 (Name 클레임 부재 적응)
        var userKey = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(userKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

// Health checks — DB 공급자에 따라 조건부 등록. SqlServer 체크는 MSSQL에서만(SQLite 등 비-MSSQL은 제외)
// → Database:Provider 설정만 바꿔 MSSQL↔SQLite 전환 시 /health가 깨지지 않는다.
var healthChecks = builder.Services.AddHealthChecks();
if (string.Equals(builder.Configuration.GetValue<string>("Database:Provider") ?? "MsSql",
        "MsSql", StringComparison.OrdinalIgnoreCase))
{
    healthChecks.AddSqlServer(
        builder.Configuration.GetConnectionString("NexaOne") ?? string.Empty,
        name: "sqlserver",
        tags: ["db", "sql", "ready"]);
}

// Application services
builder.Services.AddNexaOneServices(builder.Configuration);
builder.Services.AddNexaOneEES(builder.Configuration);
// EesHubNotifier는 싱글톤 IHubContext만 래핑하는 무상태 구현 → 싱글톤으로 등록한다.
// (Scoped로 두면 싱글톤 HostedService 생성자 주입이 captive dependency가 되어 스코프 검증 시 기동 실패)
builder.Services.AddSingleton<NexaOne.API.Hubs.IEesHubNotifier, NexaOne.API.Hubs.EesHubNotifier>();
// ADR-002 §2.5 — 실시간 알림 경로 조정. 버스 활성(Events:Outbox:Enabled) 시 구독자가 이벤트 기반 알림을
// 전달하므로 컨트롤러는 이벤트로 뒷받침되는 직접 SignalR 호출을 생략한다(이중 발행 방지). 비활성이면 직접 발행(폴백).
builder.Services.AddSingleton(new NexaOne.API.Services.RealtimeNotificationCoordinator(
    builder.Configuration.GetValue("Events:Outbox:Enabled", false)));

// §17.4 OpenTelemetry — 분산 추적 + Metrics. OTLP 엔드포인트가 설정되면 Collector로,
// 없으면 개발용 Console exporter로 트레이스를 내보낸다(설계: 개발 Console/OTLP, 운영 OTLP Collector).
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "NexaOne.API", serviceVersion: "2.0"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        else
            tracing.AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddRuntimeInstrumentation()
               .AddMeter(NexaOne.Common.Telemetry.NexaMesMetrics.MeterName);   // §17.5 비즈니스 Metrics
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        else
            metrics.AddConsoleExporter();   // 개발: OTLP 미설정 시 Console로 Metrics 노출 (§17.4)
    });

// §17.5 비즈니스 Metrics 트래커 — ObservableGauge(active_users/equipment_channel_status)의 상태 보관처.
// 시각은 TimeProvider로 주입(테스트 용이). 미들웨어/Hub/FDC 호스트가 갱신한다.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NexaOne.Common.Telemetry.ActiveUserTracker>();
builder.Services.AddSingleton<NexaOne.Common.Telemetry.EquipmentChannelStatusRegistry>();

// §8 워크플로우 엔진 — NexusFramework WorkflowManager.
// NodeRegistry(AssemblyInvocation + Try/Catch/Finally) → WorkflowExecutor → WorkflowManager 싱글톤.
builder.Services.AddSingleton<INodeRegistry>(_ =>
{
    var registry = new NodeRegistry();          // Try/Catch/Finally 기본 등록
    registry.RegisterAssemblyInvocationNode();  // [WorkflowCallable] 호출 노드
    return registry;
});
builder.Services.AddSingleton<IWorkflowExecutor>(sp =>
    new WorkflowExecutor(sp.GetRequiredService<INodeRegistry>()));
builder.Services.AddSingleton(sp =>
    new WorkflowManager(sp.GetRequiredService<IWorkflowExecutor>()));

// FDC 실시간 수집 호스트 (§10.4.2/10.4.3) — "Fdc:Collector:Enabled"=true 일 때만 실제 기동
// PlantController는 싱글톤으로 공유: HostedService가 설비를 등록·기동하고, FdcController가 수동 제어한다(§10.4.4)
builder.Services.AddSingleton<NexusFramework.PlantController>();
builder.Services.AddHostedService<NexaOne.API.Services.FdcCollectorHostedService>();

// ADR-002 Event Bus — Transactional Outbox. 저장소는 항상 등록(EnqueueAsync 사용 가능).
builder.Services.AddScoped<NexaOne.Infrastructure.Persistence.IOutboxRepository,
    NexaOne.Infrastructure.Persistence.OutboxRepository>();
// opt-in: Kafka 백본 + Outbox 디스패처 + 구독자(SignalR). 브로커 없는 dev/CI 보호("Kafka:Enabled").
if (builder.Configuration.GetValue("Kafka:Enabled", false))
{
    var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
    var eventTopic = builder.Configuration["Events:Outbox:Topic"] ?? "nexaone.events";

    builder.Services.AddSingleton<NexaOne.Infrastructure.Messaging.IMessageBus>(sp =>
        new NexaOne.Infrastructure.Messaging.KafkaMessageBus(
            kafkaBootstrap,
            sp.GetRequiredService<ILogger<NexaOne.Infrastructure.Messaging.KafkaMessageBus>>(),
            sp.GetRequiredService<ILogger<NexusCom.Messaging.Kafka.KafkaDriver>>()));

    builder.Services.AddHostedService<NexaOne.API.Services.OutboxDispatcherService>();

    // 버스 구독자: 도메인 이벤트 → SignalR 푸시 (UI 갱신 = 버스 소비, ADR-002)
    builder.Services.AddHostedService(sp =>
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        Func<NexaOne.Infrastructure.Messaging.DomainEventMessage, CancellationToken, Task> handler =
            async (msg, ct) =>
            {
                using var scope = scopeFactory.CreateScope();
                var notifier = scope.ServiceProvider.GetRequiredService<NexaOne.API.Hubs.IEesHubNotifier>();
                // 이벤트 유형별 세분 알림 — 인메모리 구독자와 동일 매핑(ADR-002 §2.5).
                await NexaOne.API.Services.RealtimeNotificationDispatch.DispatchAsync(
                    notifier, msg.EventType, msg.AggregateId, msg.Payload, ct);
            };
        return new NexaOne.Infrastructure.Messaging.KafkaConsumerService(
            new NexusCom.Messaging.Kafka.KafkaDriver(
                sp.GetRequiredService<ILogger<NexusCom.Messaging.Kafka.KafkaDriver>>()),
            new NexaOne.Infrastructure.Messaging.KafkaConsumerOptions
            {
                BootstrapServers = kafkaBootstrap,
                Topics = new[] { eventTopic }
            },
            handler,
            sp.GetRequiredService<ILogger<NexaOne.Infrastructure.Messaging.KafkaConsumerService>>());
    });
}
// Kafka 없이 Outbox만 켠 경우(Events:Outbox:Enabled && !Kafka:Enabled) — 브로커리스(인메모리) Event Bus.
// 디스패처가 인메모리 버스로 발행하고 인프로세스 구독자가 SignalR로 푸시한다(dev/단일노드/통합테스트).
else if (builder.Configuration.GetValue("Events:Outbox:Enabled", false))
{
    builder.Services.AddSingleton<NexaOne.Infrastructure.Messaging.InMemoryMessageBus>();
    builder.Services.AddSingleton<NexaOne.Infrastructure.Messaging.IMessageBus>(
        sp => sp.GetRequiredService<NexaOne.Infrastructure.Messaging.InMemoryMessageBus>());
    builder.Services.AddHostedService<NexaOne.API.Services.InMemoryBusSubscriberService>();
    builder.Services.AddHostedService<NexaOne.API.Services.OutboxDispatcherService>();
}

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("NexaOne", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});

var app = builder.Build();

// §17.5 — ObservableGauge(nexames_active_users/nexames_equipment_channel_status)를 트래커 싱글톤에 1회 바인딩
NexaOne.Common.Telemetry.NexaMesMetrics.BindObservableGauges(
    app.Services.GetRequiredService<NexaOne.Common.Telemetry.ActiveUserTracker>(),
    app.Services.GetRequiredService<NexaOne.Common.Telemetry.EquipmentChannelStatusRegistry>());

// ADR-002 — Outbox 적체/데드레터 ObservableGauge는 outbox 활성 시에만 바인딩한다(off면 게이지 미노출).
// 게이지 콜백은 동기이므로 스코프드 IOutboxRepository를 IServiceScopeFactory로 해석해 COUNT를 동기 대기한다.
if (app.Configuration.GetValue("Events:Outbox:Enabled", false))
{
    var outboxScopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    static int CountOutbox(IServiceScopeFactory f, Func<NexaOne.Infrastructure.Persistence.IOutboxRepository, Task<int>> q)
    {
        using var scope = f.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<NexaOne.Infrastructure.Persistence.IOutboxRepository>();
        return q(repo).GetAwaiter().GetResult();
    }
    NexaOne.Common.Telemetry.NexaMesMetrics.BindOutboxGauges(
        () => CountOutbox(outboxScopeFactory, r => r.CountPendingAsync()),
        () => CountOutbox(outboxScopeFactory, r => r.CountDeadLetteredAsync()));
}

// Phase 2(Pro-Code 공존) — OpenAPI 스펙은 상시 노출한다: 외부 React/Vue SPA가 TypeScript 클라이언트를
// 생성하고 계약을 발견하는 채널. 대화형 Swagger UI는 개발 환경에만 노출(운영 정보 노출 최소화).
app.UseSwagger();
if (app.Environment.IsDevelopment())
    app.UseSwaggerUI();

// §17.5 — API 시간/오류/활동사용자 계측. 예외 처리기보다 앞에 두어 500 변환 후의 최종 상태코드와 전체 시간을 관측.
app.UseMiddleware<NexaOne.API.Middleware.NexaMesMetricsMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors("NexaOne");
app.UseAuthentication();
// §17.3 — 인증 직후 요청 공통 로그 필드 주입(UserId 클레임 판정 가능 시점) + 요청 완료 로그
app.UseMiddleware<NexaOne.API.Middleware.RequestLogContextMiddleware>();
app.UseSerilogRequestLogging();
// §18.2.3 — 사용자/IP 파티션 Rate Limiting (인증 후 → 사용자 클레임 기준 파티션 가능 시점).
// RateLimiting:Enabled=false면 미적용 — 외부 게이트웨이/WAF가 이미 제한하거나, 통합테스트가
// 비즈니스 로직 검증 시 레이트리밋(공유 IP·다수 호출)에 막혀 비결정적으로 실패하는 것을 피할 때 사용. 기본 활성.
if (builder.Configuration.GetValue("RateLimiting:Enabled", true))
    app.UseRateLimiter();
// §20.10 — 비밀번호 변경 강제 사용자의 업무 API 차단 (인증 후 → 클레임 판정 가능 시점)
app.UseMiddleware<NexaOne.API.Middleware.PasswordChangeRequiredMiddleware>();
app.UseAuthorization();
app.MapControllers();
// §18.2.3 적응 — SignalR(long-polling 시 분당 요청 수 급증)과 헬스 프로브(공유 모니터링 IP)는
// 전역 한도(100req/min)에 걸려 429가 나면 안 되므로 Rate Limiting에서 제외한다
app.MapHub<NexaOneEESHub>("/hubs/smartees").DisableRateLimiting();
// 헬스 프로브는 전역 FallbackPolicy(인증 요구)에 걸리지 않도록 명시적으로 익명 허용한다(모니터링/k8s liveness).
// liveness/readiness 분리: /health/live는 체크 없이 프로세스 생존만(Predicate=_=>false → 빈 집합 = Healthy),
// /health/ready는 "ready" 태그(SqlServer 등 의존성)만 평가, /health는 호환을 위해 전체 체크 유지.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health").AllowAnonymous().DisableRateLimiting();

await app.RunAsync();

public partial class Program { }
