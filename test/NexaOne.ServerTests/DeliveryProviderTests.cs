using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NexaFramework.Service.Collaboration;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Collaboration;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class DeliveryProviderTests
{
    [Fact]
    public void Credential_reference_resolves_live_host_configuration_without_exposing_secret_in_failures()
    {
        var values = ValidCredential();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var resolver = new ConfigurationEmailCredentialResolver(configuration);

        var first = resolver.Resolve("mail-primary");
        configuration["Delivery:Email:Credentials:Primary:Password"] = "rotated-secret";
        var second = resolver.Resolve("mail-primary");

        first.Password.Should().Be("initial-secret");
        second.Password.Should().Be("rotated-secret");
        var missing = () => resolver.Resolve("missing-secret-reference");
        missing.Should().Throw<DeliveryProviderException>()
            .Where(error => error.Error == DeliveryProviderError.CredentialUnavailable)
            .Which.Message.Should().NotContain("missing-secret-reference");
    }

    [Fact]
    public async Task Email_component_adapter_normalizes_invalid_recipient_before_network_io()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidCredential()).Build();
        var provider = new EmailComponentDeliveryProvider(
            new ConfigurationEmailCredentialResolver(configuration));
        var payload = new DeliveryPayload(
            DeliveryChannel.Email, "not an address", "Subject", "Body", "smtp", "mail-primary");

        var action = () => provider.SendAsync(payload);

        await action.Should().ThrowAsync<DeliveryProviderException>()
            .Where(error => error.Error == DeliveryProviderError.InvalidRecipient);
    }

    [Fact]
    public void Registry_uses_exact_provider_keys_and_normalizes_unknown_keys()
    {
        var provider = new FakeProvider();
        var registry = new DeliveryProviderRegistry(provider);

        registry.GetRequired("smtp").Should().BeSameAs(provider);
        var action = () => registry.GetRequired("SMTP");
        action.Should().Throw<DeliveryProviderException>()
            .Where(error => error.Error == DeliveryProviderError.UnsupportedProvider);
    }

    private static Dictionary<string, string?> ValidCredential() => new()
    {
        ["Delivery:Email:Credentials:Primary:Reference"] = "mail-primary",
        ["Delivery:Email:Credentials:Primary:Host"] = "smtp.example.com",
        ["Delivery:Email:Credentials:Primary:Port"] = "587",
        ["Delivery:Email:Credentials:Primary:UserName"] = "mailer",
        ["Delivery:Email:Credentials:Primary:Password"] = "initial-secret",
        ["Delivery:Email:Credentials:Primary:FromAddress"] = "noreply@example.com",
        ["Delivery:Email:Credentials:Primary:FromDisplayName"] = "NexaOne",
        ["Delivery:Email:Credentials:Primary:UseStartTls"] = "true",
        ["Delivery:Email:Credentials:Primary:TimeoutSeconds"] = "30",
    };

    private sealed class FakeProvider : IDeliveryProvider
    {
        public string Key => "smtp";
        public Task<string> SendAsync(DeliveryPayload payload, CancellationToken ct = default)
            => Task.FromResult("smtp:test");
    }
}
