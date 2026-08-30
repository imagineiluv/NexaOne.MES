using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NexaOne.Server.Gateway;
using NexaOne.Server.Security;
using NexaOne.ServiceContracts.Fdc;
using FluentAssertions;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class RunAdmissionControllerTests
{
    [Fact]
    public async Task Missing_feature_flag_is_truthful_service_unavailable_before_transport_or_credentials()
    {
        var controller = CreateController(new Dictionary<string, string?>());

        var result = await controller.Acquire(
            new RunAdmissionAcquireDto("EQ-1", "cleaner-a", "request-1"),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Explicit_false_feature_flag_is_truthful_service_unavailable()
    {
        var controller = CreateController(new Dictionary<string, string?>
        {
            ["RunAdmission:Enabled"] = "false",
            ["RunAdmission:RequireHttps"] = "false",
        });

        var result = await controller.Acquire(
            new RunAdmissionAcquireDto("EQ-1", "cleaner-a", "request-1"),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Enabled_transport_policy_rejects_plain_http_before_reading_credentials()
    {
        var controller = CreateController(new Dictionary<string, string?>
        {
            ["RunAdmission:Enabled"] = "true",
        });

        var result = await controller.Acquire(
            new RunAdmissionAcquireDto("EQ-1", "cleaner-a", "request-1"),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
    }

    [Fact]
    public async Task Missing_client_configuration_is_truthful_service_unavailable()
    {
        var controller = CreateController(new Dictionary<string, string?>
        {
            ["RunAdmission:Enabled"] = "true",
            ["RunAdmission:RequireHttps"] = "false",
        });

        var result = await controller.Acquire(
            new RunAdmissionAcquireDto("EQ-1", "cleaner-a", "request-1"),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Valid_installation_secret_reaches_service_but_wrong_secret_does_not()
    {
        const string secret = "installation-secret-with-adequate-entropy";
        var service = new StubService();
        var controller = CreateController(new Dictionary<string, string?>
        {
            ["RunAdmission:Enabled"] = "true",
            ["RunAdmission:RequireHttps"] = "false",
            ["RunAdmission:Clients:cleaner-a:SecretSha256"] = Hash(secret),
            ["RunAdmission:Clients:cleaner-a:ClientId"] = "cleaner-a",
            ["RunAdmission:Clients:cleaner-a:EquipmentIds:0"] = "EQ-1",
        }, service);
        controller.Request.Headers[RunAdmissionController.ClientSecretHeader] = "wrong";
        var request = new RunAdmissionAcquireDto("EQ-1", "cleaner-a", "request-1");

        (await controller.Acquire(request, CancellationToken.None))
            .Should().BeOfType<UnauthorizedResult>();
        service.AcquireCount.Should().Be(0);

        controller.Request.Headers[RunAdmissionController.ClientSecretHeader] = secret;
        var accepted = await controller.Acquire(request, CancellationToken.None);

        accepted.Should().BeOfType<OkObjectResult>();
        service.AcquireCount.Should().Be(1);
    }

    [Fact]
    public async Task Installation_credential_is_bound_to_canonical_client_and_equipment()
    {
        const string secret = "installation-secret-with-adequate-entropy";
        var service = new StubService();
        var controller = CreateController(new Dictionary<string, string?>
        {
            ["RunAdmission:Enabled"] = "true",
            ["RunAdmission:RequireHttps"] = "false",
            ["RunAdmission:Clients:cleaner-a:SecretSha256"] = Hash(secret),
            ["RunAdmission:Clients:cleaner-a:ClientId"] = "cleaner-a",
            ["RunAdmission:Clients:cleaner-a:EquipmentIds:0"] = "EQ-1",
        }, service);
        controller.Request.Headers[RunAdmissionController.ClientSecretHeader] = secret;

        (await controller.Acquire(
                new RunAdmissionAcquireDto("EQ-2", "cleaner-a", "request-1"),
                CancellationToken.None))
            .Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await controller.Acquire(
                new RunAdmissionAcquireDto("EQ-1", "CLEANER-A", "request-1"),
                CancellationToken.None))
            .Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.AcquireCount.Should().Be(0);
    }

    [Fact]
    public async Task Authenticated_malformed_request_returns_bad_request_instead_of_500()
    {
        const string secret = "installation-secret-with-adequate-entropy";
        var controller = CreateController(new Dictionary<string, string?>
        {
            ["RunAdmission:Enabled"] = "true",
            ["RunAdmission:RequireHttps"] = "false",
            ["RunAdmission:Clients:cleaner-a:SecretSha256"] = Hash(secret),
            ["RunAdmission:Clients:cleaner-a:ClientId"] = "cleaner-a",
            ["RunAdmission:Clients:cleaner-a:EquipmentIds:0"] = "EQ-1",
        }, new ThrowingStubService());
        controller.Request.Headers[RunAdmissionController.ClientSecretHeader] = secret;

        var result = await controller.Acquire(
            new RunAdmissionAcquireDto("EQ-1", "cleaner-a", ""),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static RunAdmissionController CreateController(
        IReadOnlyDictionary<string, string?> values,
        StubService? service = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var controller = new RunAdmissionController(
            service ?? new StubService(),
            configuration,
            new ConfigurationEquipmentClientAuthenticator(configuration))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return controller;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private class StubService : IRunAdmissionService
    {
        public int AcquireCount { get; private set; }

        public virtual Task<RunAdmissionDecisionDto> AcquireAsync(
            RunAdmissionAcquireDto request,
            CancellationToken ct = default)
        {
            AcquireCount++;
            return Task.FromResult(new RunAdmissionDecisionDto(
                false, "TEST", "test", null));
        }

        public Task<RunAdmissionStatusDto> KeepAliveAsync(
            RunAdmissionLeaseProofDto request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RunAdmissionReleaseDto> ReleaseAsync(
            RunAdmissionLeaseProofDto request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingStubService : StubService
    {
        public override Task<RunAdmissionDecisionDto> AcquireAsync(
            RunAdmissionAcquireDto request,
            CancellationToken ct = default) =>
            throw new ArgumentException("RequestId is required.", nameof(request));
    }
}
