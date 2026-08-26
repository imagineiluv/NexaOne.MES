using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NexaOne.Server;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class ServerSpringConfigResolverTests
{
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("sqlite")]
    [InlineData("SQLITE")]
    public void Uses_sqlite_parent_context_when_gateway_provider_is_sqlite(string provider)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider,
        });

        ServerSpringConfigResolver.Resolve(configuration)
            .Should().Be(ServerSpringConfigResolver.SqliteConfigPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MsSql")]
    public void Defaults_to_mssql_parent_context_for_non_sqlite_provider(string? provider)
    {
        var values = new Dictionary<string, string?>();
        if (provider is not null)
            values["Database:Provider"] = provider;

        ServerSpringConfigResolver.Resolve(BuildConfiguration(values))
            .Should().Be(ServerSpringConfigResolver.MsSqlConfigPath);
    }

    [Fact]
    public void Explicit_spring_config_overrides_database_provider()
    {
        const string customPath = "config/host/server.custom.xml";
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Server:SpringConfig"] = customPath,
        });

        ServerSpringConfigResolver.Resolve(configuration).Should().Be(customPath);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
