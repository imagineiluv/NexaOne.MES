using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.Server;
using NexaOne.Server.Services;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class HrTimeReportResourcesTests
{
    [Fact]
    public Task Startup_seeds_the_time_report_menu_and_all_English_page_resources_once()
        => WithDatabase(async (connectionString, connection) =>
        {
            await Initialize(connectionString);
            await Initialize(connectionString);

            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_HR_TIME_REPORT'")
                .Should().Be("1");
            Scalar(connection, "SELECT PARENT_MENU_ID FROM SYS_MENU WHERE UI_ID='NX_HR_TIME_REPORT'")
                .Should().Be("FACTORY_STD_SINGLE_WORKER");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU_ROLE WHERE MENU_ID='NX_HR_TIME_REPORT'")
                .Should().Be("0");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE " +
                "RESOURCE_KEY='menu.NX_HR_TIME_REPORT' AND LANGUAGE='EnUs'").Should().Be("Time report");
            HostLiteralMetaRoutes.Contains("NX_HR_TIME_REPORT").Should().BeTrue();

            var page = File.ReadAllText(RepositorySource.GetFile(
                "src/00.Main/NexaOne.Server/Components/Pages/HostHrTimeReport.razor"));
            var calls = Regex.Matches(page,
                """\bT\(\s*"(?<key>hrReport\.[^"\\]+)"\s*,\s*"(?:\\.|[^"\\])*"\s*,\s*"(?<en>(?:\\.|[^"\\])*)"\s*\)""");
            calls.Count.Should().BeGreaterThan(30);
            foreach (Match call in calls)
            {
                var key = call.Groups["key"].Value;
                var english = JsonSerializer.Deserialize<string>("\"" + call.Groups["en"].Value + "\"");
                Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                    "WHERE RESOURCE_KEY=@key AND LANGUAGE='EnUs'", key).Should().Be(english, key);
            }
        });

    private static async Task Initialize(string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development", ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["ConnectionStrings:NexaOne"] = connectionString,
        });
        await using var app = builder.Build();
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);
    }

    private static async Task WithDatabase(Func<string, SqliteConnection, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-hr-report-resources-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False;Pooling=False";
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open(); await action(connectionString, connection);
        }
        finally { File.Delete(path); }
    }

    private static string Scalar(SqliteConnection connection, string sql, string? key = null)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        if (key is not null) command.Parameters.AddWithValue("@key", key);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
