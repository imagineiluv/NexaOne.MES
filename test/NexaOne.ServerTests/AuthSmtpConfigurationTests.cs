using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class AuthSmtpConfigurationTests
{
    [Theory]
    [InlineData(null, "noreply@example.com", "587", "Email:Smtp:Host")]
    [InlineData("   ", "noreply@example.com", "587", "Email:Smtp:Host")]
    [InlineData("smtp host", "noreply@example.com", "587", "Email:Smtp:Host")]
    [InlineData("smtp.example.com", null, "587", "Email:Smtp:Sender")]
    [InlineData("smtp.example.com", "@@invalid", "587", "Email:Smtp:Sender")]
    [InlineData("smtp.example.com", "noreply", "587", "Email:Smtp:Sender")]
    [InlineData("smtp.example.com", "noreply@example.com", null, "Email:Smtp:Port")]
    [InlineData("smtp.example.com", "noreply@example.com", "0", "Email:Smtp:Port")]
    [InlineData("smtp.example.com", "noreply@example.com", "65536", "Email:Smtp:Port")]
    [InlineData("smtp.example.com", "noreply@example.com", "not-a-port", "Email:Smtp:Port")]
    public void Enabled_smtp_rejects_incomplete_or_invalid_required_configuration(
        string? host,
        string? sender,
        string? port,
        string expectedKey)
    {
        var configuration = BuildConfiguration(enabled: true, host, sender, port);
        var services = new ServiceCollection();

        var act = () => services.AddNexaOneAuth(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{expectedKey}'*")
            .WithMessage("*SMTP is enabled*");
    }

    [Fact]
    public void Enabled_smtp_with_complete_configuration_registers_real_sender()
    {
        var configuration = BuildConfiguration(
            enabled: true,
            host: "smtp.example.com",
            sender: "noreply@example.com",
            port: "587");
        var services = new ServiceCollection();

        services.AddNexaOneAuth(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEmailSender>().Should().BeOfType<SmtpEmailSender>();
    }

    [Fact]
    public void Disabled_smtp_keeps_null_sender_even_when_smtp_fields_are_absent()
    {
        var configuration = BuildConfiguration(
            enabled: false,
            host: null,
            sender: null,
            port: null);
        var services = new ServiceCollection();

        services.AddNexaOneAuth(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEmailSender>().Should().BeOfType<NullEmailSender>();
    }

    private static IConfiguration BuildConfiguration(
        bool enabled,
        string? host,
        string? sender,
        string? port)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Email:Smtp:Enabled"] = enabled.ToString(),
                ["Email:Smtp:Host"] = host,
                ["Email:Smtp:Sender"] = sender,
                ["Email:Smtp:Port"] = port,
            })
            .Build();
}
