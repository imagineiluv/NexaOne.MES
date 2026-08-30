using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NexaOne.Server;
using NexaOne.Server.Security;
using FluentAssertions;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class EquipmentClientAuthenticatorTests
{
    [Fact]
    public void Equipment_rate_limit_partition_is_bound_to_remote_address_even_with_a_valid_jwt()
    {
        var first = RateLimitContext("operator-a", "10.20.30.40");
        var second = RateLimitContext("operator-b", "10.20.30.40");

        NexaOneMesServiceCollectionExtensions.ResolveGlobalRateLimitPartitionKey(
                first, equipmentClient: true)
            .Should().Be("equipment-client:10.20.30.40");
        NexaOneMesServiceCollectionExtensions.ResolveGlobalRateLimitPartitionKey(
                second, equipmentClient: true)
            .Should().Be("equipment-client:10.20.30.40",
                "equipment ingress must not be repartitioned by optional MES bearer identities");
    }

    [Fact]
    public void Normal_api_rate_limit_partition_still_uses_the_authenticated_subject()
    {
        var context = RateLimitContext("operator-a", "10.20.30.40");

        NexaOneMesServiceCollectionExtensions.ResolveGlobalRateLimitPartitionKey(
                context, equipmentClient: false)
            .Should().Be("operator-a");
    }

    [Theory]
    [InlineData("/api/v1/run-admission", true)]
    [InlineData("/api/v1/run-admission/decide", true)]
    [InlineData("/api/v1/pom/work-scope-projections", true)]
    [InlineData("/api/v1/pom/work-scope-projections/event-1", true)]
    [InlineData("/api/v1/pom/work-scope-projections-extra", false)]
    [InlineData("/api/v1/pom/work-scopes", false)]
    public void Equipment_client_endpoint_policy_classifies_the_shared_authentication_boundary(
        string path,
        bool expected)
    {
        EquipmentClientEndpointPolicy.IsEquipmentClientPath(new PathString(path))
            .Should().Be(expected);
    }

    [Fact]
    public void Configured_installation_secret_authenticates_its_canonical_equipment_identity()
    {
        const string secret = "installation-secret-with-adequate-entropy";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RunAdmission:RequireHttps"] = "false",
                ["RunAdmission:Clients:cleaner-a:SecretSha256"] =
                    "8c2489b33db91a76c2f08a4ec69c06163c598efda1453f0acb37df2e6d5026ba",
                ["RunAdmission:Clients:cleaner-a:ClientId"] = "cleaner-a",
                ["RunAdmission:Clients:cleaner-a:EquipmentIds:0"] = "EQ-1",
            })
            .Build();
        IEquipmentClientAuthenticator authenticator =
            new ConfigurationEquipmentClientAuthenticator(configuration);
        var context = new DefaultHttpContext();
        context.Request.Headers[EquipmentClientAuthentication.ClientSecretHeader] = secret;

        var decision = authenticator.Authenticate(context.Request, "cleaner-a", "EQ-1");

        decision.Identity.Should().Be(new EquipmentClientIdentity("cleaner-a", "EQ-1"));
        decision.Rejection.Should().BeNull();
    }

    private static DefaultHttpContext RateLimitContext(string subject, string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject)],
            authenticationType: "test"));
        return context;
    }
}
