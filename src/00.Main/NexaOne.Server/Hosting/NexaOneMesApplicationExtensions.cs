using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;
using NexaOne.Infrastructure.Diagnostics;
using NexaOne.Server.Components;

namespace NexaOne.Server;

/// <summary>
/// 순서에 민감한 MES 미들웨어와 엔드포인트 구성을 한곳에서 관리한다.
/// </summary>
public static class NexaOneMesApplicationExtensions
{
    /// <summary>
    /// 인증, 감사, 실시간 허브, Blazor 및 Portal 폴백을 올바른 순서로 HTTP 파이프라인에 연결한다.
    /// </summary>
    /// <param name="app">구성을 적용할 웹 애플리케이션이다.</param>
    /// <returns>연속해서 추가 설정을 적용할 수 있도록 입력 애플리케이션을 반환한다.</returns>
    public static WebApplication UseNexaOneMes(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var runtime = app.Services.GetRequiredService<NexaOneMesRuntimeState>();
        var hosting = app.Services.GetRequiredService<NexaOneMesHostingOptions>();
        runtime.MarkPipelineConfigured();

        // 개발용 시드는 화면 요청을 받기 전에 준비되어야 첫 요청부터 일관된 데이터를 제공한다.
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);

        // 신뢰 proxy에서 온 X-Forwarded-Proto를 먼저 반영해야 admission TLS 정책과 IP rate-limit가
        // 실제 client 연결을 기준으로 판단한다. 신뢰 proxy 목록은 등록 단계에서 제한된다.
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/run-admission")
                && app.Configuration.GetValue("RunAdmission:RequireHttps", true)
                && !context.Request.IsHttps)
            {
                await Results.Problem(
                        statusCode: StatusCodes.Status426UpgradeRequired,
                        title: "HTTPS is required for run admission.")
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        app.UseSwagger();
        if (app.Environment.IsDevelopment()) app.UseSwaggerUI();

        // Portal 정적 자산은 공개 자원이므로 인증 및 SPA 폴백보다 먼저 해석한다.
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseMiddleware<NexaOne.Server.Gateway.AuditUserContextMiddleware>();
        if (app.Configuration.GetValue("RateLimiting:Enabled", true)) app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMiddleware<NexaOne.Server.Gateway.PasswordChangeRequiredMiddleware>();
        if (app.Configuration.GetValue("RequestLogging:Enabled", false))
            app.UseMiddleware<NexaOne.Server.Gateway.RequestLogMiddleware>();
        app.UseAntiforgery();

        app.MapControllers();
        app.MapHub<NexaOne.Server.Realtime.NexaOneEESHub>(hosting.RealtimeHubPath);
        app.MapHealthChecks(hosting.HealthPath).AllowAnonymous();
        app.MapGet("/", () => Results.Redirect(hosting.LoginPath, permanent: false)).AllowAnonymous();
        app.MapGet(hosting.DiagnosticsPath, async (
            ExternalDependencyProbeCatalog dependencyCatalog,
            CancellationToken cancellationToken) =>
        {
            var dependencies = await dependencyCatalog
                .CheckAllAsync(cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                modulesEnabled = runtime.ModulesEnabled,
                services = runtime.LoadedServices,
                workerCount = runtime.WorkerCount,
                // 타입 이름만 노출하고 Bean 인스턴스나 연결 옵션은 내보내지 않아 운영 진단과 비밀정보 경계를 함께 지킨다.
                bridges = runtime.LoadedBridges,
                externalDependencies = dependencies.Select(snapshot => new
                {
                    id = snapshot.Descriptor.Id,
                    displayName = snapshot.Descriptor.DisplayName,
                    kind = snapshot.Descriptor.Kind,
                    version = snapshot.Descriptor.Version,
                    requiredAtStartup = snapshot.Descriptor.RequiredAtStartup,
                    capabilities = snapshot.Descriptor.Capabilities,
                    status = snapshot.Health.Status.ToString(),
                    checkedAtUtc = snapshot.Health.CheckedAtUtc,
                    summary = snapshot.Health.Summary,
                    details = snapshot.Health.Details,
                }),
            });
        }).RequireAuthorization();

        app.MapRazorComponents<HostApp>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(NexaOne.Web.Pages.Meta.MetaScreen).Assembly)
            .AllowAnonymous();

        // 폴백은 실제 파일, API, SignalR, Blazor 라우트가 우선권을 갖도록 항상 마지막에 둔다.
        app.MapFallbackToFile(hosting.DesignerFallbackPattern, hosting.PortalIndexFile);
        app.MapFallbackToFile(hosting.PortalFallbackPattern, hosting.PortalIndexFile);

        return app;
    }

    /// <summary>
    /// 통합 MES 실행 파일의 웹 호스트를 시작하고 종료될 때까지 대기한다.
    /// 보안 초기화와 모듈 수명주기는 <see cref="NexaOneMesStartupHostedService"/>가 담당하므로
    /// 이 메서드는 실행 진입점의 간결성을 위한 편의 래퍼다.
    /// </summary>
    /// <param name="app">실행할 웹 애플리케이션이다.</param>
    /// <param name="cancellationToken">호스트 실행을 취소할 토큰이다.</param>
    public static async Task RunNexaOneMesAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        Console.WriteLine("[NexaOne.Server] Ready (web host). Press Ctrl+C to stop.");
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
