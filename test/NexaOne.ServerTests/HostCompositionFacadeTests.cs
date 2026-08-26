using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Application.Auth;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Common.Telemetry;
using NexaOne.Infrastructure.Diagnostics;
using NexaOne.Server;
using NexaOne.Server.Gateway;
using NexaOne.Server.Realtime;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;
using NexaOne.Web.Services.Meta;
using NexusCom.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// MES composition facade contract. Individual gateway/UI tests prove feature behavior; this suite protects
/// the smaller but critical seam that a Program.cs extraction can silently break: registrations, aliases,
/// host-owned workers and the order-sensitive HTTP endpoints.
/// </summary>
public sealed class HostCompositionFacadeTests : IClassFixture<HostCompositionFacadeTests.FacadeFactory>
{
    private const string Secret = "facade-contract-jwt-secret-key-at-least-32-bytes";
    private const string Issuer = "nexaone-facade-contract";
    private readonly FacadeFactory _factory;

    public HostCompositionFacadeTests(FacadeFactory factory) => _factory = factory;

    public sealed class FacadeFactory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } =
            Path.Combine(Path.GetTempPath(), $"nexaone-facade-contract-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best-effort test cleanup */ }
        }
    }

    [Fact]
    public void Facade_preserves_core_registrations_aliases_and_lifetimes()
    {
        _factory.Services.GetRequiredService<IDatabaseProvider>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IRuleDispatcher>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IQueryRegistry>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IJwtService>().Should().NotBeNull();
        _factory.Services.GetRequiredService<GatewayLoginService>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IErrorLocalizer>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IScreenDefinitionProvider>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IEesHubNotifier>().Should().NotBeNull();
        _factory.Services.GetRequiredService<ActiveUserTracker>().Should().NotBeNull();
        _factory.Services.GetRequiredService<ExternalDependencyProbeCatalog>().Descriptors
            .Should().ContainSingle(descriptor => descriptor.Id == "nexaone.fdc.plc");

        var refreshStore = _factory.Services.GetRequiredService<SysRefreshTokenStore>();
        _factory.Services.GetRequiredService<IRefreshTokenStore>().Should().BeSameAs(refreshStore);

        var screenRefresh = _factory.Services.GetRequiredService<ScreenRefreshNotifier>();
        _factory.Services.GetRequiredService<IScreenRefreshNotifier>().Should().BeSameAs(screenRefresh);
        var alertFeed = _factory.Services.GetRequiredService<RealtimeAlertFeed>();
        _factory.Services.GetRequiredService<IRealtimeAlertFeed>().Should().BeSameAs(alertFeed);

        _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>()
            .Should().BeOfType<PermissionPolicyProvider>();
        _factory.Services.GetServices<IAuthorizationHandler>()
            .Should().Contain(handler => handler is PermissionAuthorizationHandler);

        var workers = _factory.Services.GetServices<IHostedService>().ToArray();
        workers.First().Should().BeOfType<NexaOneMesStartupHostedService>(
            "security initialization and runtime ownership must precede module/background workers");
        workers.Should().Contain(worker => worker is BatchProcessWorker);
        workers.Should().Contain(worker => worker is RefreshTokenCleanupWorker);

        using var firstScope = _factory.Services.CreateScope();
        using var secondScope = _factory.Services.CreateScope();
        var firstAuthState = firstScope.ServiceProvider.GetRequiredService<JwtAuthStateProvider>();
        firstScope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>()
            .Should().BeSameAs(firstAuthState);
        secondScope.ServiceProvider.GetRequiredService<JwtAuthStateProvider>()
            .Should().NotBeSameAs(firstAuthState, "Blazor authentication state is circuit-scoped");

        var firstAuthContext = firstScope.ServiceProvider.GetRequiredService<AuthContextService>();
        firstScope.ServiceProvider.GetRequiredService<IAuthContext>().Should().BeSameAs(firstAuthContext);
        secondScope.ServiceProvider.GetRequiredService<AuthContextService>()
            .Should().NotBeSameAs(firstAuthContext, "user context must not leak between circuits");

        firstScope.ServiceProvider.GetRequiredService<IApiClient>().Should().NotBeNull();
    }

    [Fact]
    public void Facade_defaults_keep_the_existing_public_route_contract()
    {
        var options = _factory.Services.GetRequiredService<NexaOneMesHostingOptions>();

        options.Should().BeEquivalentTo(new
        {
            LoginPath = "/login",
            HealthPath = "/health",
            DiagnosticsPath = "/diag",
            RealtimeHubPath = "/hubs/smartees",
            PortalIndexFile = "/spa/index.html",
            DesignerFallbackPattern = "/Designer/{*path:nonfile}",
            PortalFallbackPattern = "/spa/{*path:nonfile}",
        });
    }

    [Fact]
    public async Task Facade_boots_and_exposes_its_runtime_state_through_authenticated_diag()
    {
        using var client = CreateClient(allowRedirect: false);

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/diag")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());
        var response = await client.GetAsync("/diag");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("modulesEnabled").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("services").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("workerCount").GetInt32().Should().Be(0);
        var dependencies = body.RootElement.GetProperty("externalDependencies").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
        dependencies.Keys.Should().BeEquivalentTo(new[]
        {
            "nexaone.database", "nexaone.fdc.plc", "nexaone.messaging",
        });

        dependencies["nexaone.database"].GetProperty("status").GetString().Should().Be("Healthy");

        dependencies["nexaone.fdc.plc"].GetProperty("status").GetString().Should().Be("Disabled");
        dependencies["nexaone.fdc.plc"].GetProperty("details")
            .GetProperty("driverCount").GetString().Should().Be("0");

        dependencies["nexaone.messaging"].GetProperty("status").GetString().Should().Be("Disabled");
        var diagnosticJson = body.RootElement.GetRawText();
        diagnosticJson.Should().NotContain("ConnectionString");
        diagnosticJson.Should().NotContain("Password");
        diagnosticJson.Should().NotContain(_factory.DbPath);
        diagnosticJson.Should().NotContain($"Data Source={_factory.DbPath};Foreign Keys=False");
        diagnosticJson.Should().NotContain(Secret);
    }

    [Fact]
    public async Task Facade_maps_root_controllers_and_realtime_hub_without_fallback_shadowing()
    {
        using var client = CreateClient(allowRedirect: false);

        var root = await client.GetAsync("/");
        root.StatusCode.Should().Be(HttpStatusCode.Redirect);
        root.Headers.Location!.OriginalString.Should().Be("/login");

        var query = await client.PostAsJsonAsync("/api/v1/query/__facade_contract_probe__", new { });
        query.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "401 proves the authorized controller route won before any SPA fallback");

        var negotiate = await client.PostAsync("/hubs/smartees/negotiate?negotiateVersion=1", content: null);
        negotiate.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "401 proves the authorized SignalR endpoint is mapped; a missing mapping would return 404");
    }

    private HttpClient CreateClient(bool allowRedirect) => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowRedirect });

    private static string MintToken()
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            Issuer,
            new[] { new Claim(ClaimTypes.NameIdentifier, "facade-contract-user") },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
