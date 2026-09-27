using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MaterialLotSplitControllerTests
{
    private const string Secret = "material-lot-split-test-secret-32bytes!!";
    private const string Issuer = "material-lot-split-test";

    [Fact]
    public async Task Split_http_route_requires_ivt_manage_and_uses_the_jwt_actor()
    {
        MaterialLotSplitCommand? received = null;
        var bridge = new Mock<IMaterialLotSplitBridge>();
        bridge.Setup(x => x.SplitAsync(It.IsAny<MaterialLotSplitCommand>(), It.IsAny<CancellationToken>()))
            .Callback<MaterialLotSplitCommand, CancellationToken>((command, _) => received = command)
            .ReturnsAsync(Result.Success(new MaterialLotSplitDto(
                "S-1", "P-1", "C-1", 2m, 5m, 3m, 2, "InStock", "InStock", false)));
        using var factory = new SplitFactory(bridge.Object);
        const string route = "/api/v1/ivt/material-lots/splits";
        var body = Command() with { ActorId = "spoofed-actor" };

        (await factory.CreateClient().PostAsJsonAsync(route, body))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuthedClient(factory, "reader", Permissions.IvtRead).PostAsJsonAsync(route, body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AuthedClient(factory, "operator-7", Permissions.IvtManage).PostAsJsonAsync(route, body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        received!.ActorId.Should().Be("operator-7");
        bridge.Verify(x => x.SplitAsync(It.IsAny<MaterialLotSplitCommand>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Split_replaces_body_actor_with_the_authenticated_identity()
    {
        var command = Command() with { ActorId = "spoofed-actor" };
        var expected = command with { ActorId = "operator-7" };
        var bridge = new Mock<IMaterialLotSplitBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.SplitAsync(expected, CancellationToken.None))
            .ReturnsAsync(Result.Success(new MaterialLotSplitDto(
                "S-1", "P-1", "C-1", 2m, 5m, 3m, 2, "InStock", "InStock", false)));

        var result = await Controller(bridge, "operator-7").Split(command, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Fact]
    public async Task Split_rejects_missing_actor_without_calling_the_bridge()
    {
        var bridge = new Mock<IMaterialLotSplitBridge>(MockBehavior.Strict);

        var result = await Controller(bridge, null).Split(Command(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        bridge.Invocations.Should().BeEmpty();
    }

    private static MaterialLotSplitController Controller(
        Mock<IMaterialLotSplitBridge> bridge, string? actor)
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        if (actor is not null) identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, actor));
        return new MaterialLotSplitController(bridge.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static MaterialLotSplitCommand Command() => new(
        "S-1", "S:1", "MES", "S:1", "P-1", "C-1", 1, 2m,
        new DateTime(2026, 9, 28, 1, 2, 3, DateTimeKind.Utc));

    private static HttpClient AuthedClient(SplitFactory factory, string actor, string permission)
    {
        var client = factory.CreateClient();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, actor),
            new Claim(Permissions.ClaimType, permission),
        };
        var token = new JwtSecurityToken(Issuer, Issuer, claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class SplitFactory(IMaterialLotSplitBridge bridge) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-lot-split-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={_dbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.ConfigureTestServices(services => services.AddSingleton(bridge));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }
    }
}
