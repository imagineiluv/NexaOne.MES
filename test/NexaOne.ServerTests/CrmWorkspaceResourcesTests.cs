using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.Server;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class CrmWorkspaceResourcesTests
{
    [Fact]
    public Task Fresh_startup_seeds_the_CRM_menu_and_every_CRM_page_resource()
        => WithDatabase(async (connectionString, connection) =>
        {
            await Initialize(connectionString);
            await Initialize(connectionString);

            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_CRM_WORKSPACE'")
                .Should().Be("1");
            Scalar(connection, "SELECT PARENT_MENU_ID FROM SYS_MENU WHERE UI_ID='NX_CRM_WORKSPACE'")
                .Should().Be("FACTORY_SLS");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='menu.NX_CRM_WORKSPACE' AND LANGUAGE='EnUs'")
                .Should().Be("CRM deals & projects");

            var page = File.ReadAllText(RepositorySource.GetFile(
                "src/00.Main/NexaOne.Server/Components/Pages/HostCrmWorkspace.razor"));
            var calls = Regex.Matches(page,
                """\bT\(\s*"(?<key>crm\.[^"\\]+)"\s*,\s*"(?:\\.|[^"\\])*"\s*,\s*"(?<en>(?:\\.|[^"\\])*)"\s*\)""");
            calls.Count.Should().BeGreaterThan(20);
            foreach (Match call in calls)
            {
                var key = call.Groups["key"].Value;
                var english = JsonSerializer.Deserialize<string>("\"" + call.Groups["en"].Value + "\"");
                Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                    "WHERE RESOURCE_KEY=@key AND LANGUAGE='EnUs'", key).Should().Be(english, key);
            }
        });

    [Fact]
    public Task Existing_startup_preserves_custom_CRM_menu_and_translation_values()
        => WithDatabase(async (connectionString, connection) =>
        {
            await Initialize(connectionString);
            Exec(connection, """
                UPDATE SYS_MENU
                   SET MENU_NAME='Our CRM', DISPLAY_SEQUENCE=91
                 WHERE MENU_ID='NX_CRM_WORKSPACE';
                UPDATE SYS_MULTI_LANGUAGE_RESOURCE
                   SET MENU_ID='CUSTOM', VALUE='Our CRM workspace'
                 WHERE RESOURCE_KEY='crm.title' AND LANGUAGE='EnUs';
                """);

            await Initialize(connectionString);
            await Initialize(connectionString);

            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_CRM_WORKSPACE'")
                .Should().Be("1");
            Scalar(connection, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='NX_CRM_WORKSPACE'")
                .Should().Be("Our CRM");
            Scalar(connection, "SELECT DISPLAY_SEQUENCE FROM SYS_MENU WHERE MENU_ID='NX_CRM_WORKSPACE'")
                .Should().Be("91");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='crm.title' AND LANGUAGE='EnUs' AND MENU_ID='CUSTOM'")
                .Should().Be("Our CRM workspace");
        });

    private static async Task Initialize(string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:NexaOne"] = connectionString,
        });
        await using var app = builder.Build();
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);
    }

    private static async Task WithDatabase(Func<string, SqliteConnection, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-crm-resources-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False;Pooling=False";
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            await action(connectionString, connection);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql, string? key = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (key is not null)
            command.Parameters.AddWithValue("@key", key);
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
