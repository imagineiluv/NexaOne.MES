using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NexaOne.Server;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class ServerApplicationManifestResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_manifest_uses_the_cleaner_product_default(string? configuredPath)
    {
        var values = new Dictionary<string, string?>();
        if (configuredPath is not null)
            values["Server:ApplicationManifest"] = configuredPath;

        ServerApplicationManifestResolver.Resolve(BuildConfiguration(values))
            .Should().Be("config/app.xml");
    }

    [Fact]
    public void Explicit_manifest_is_trimmed_and_selected_for_the_deployment()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Server:ApplicationManifest"] = "  config/projects/customer-a.app.xml  ",
        });

        ServerApplicationManifestResolver.Resolve(configuration)
            .Should().Be("config/projects/customer-a.app.xml");
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
