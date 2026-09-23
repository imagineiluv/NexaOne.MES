using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.Server;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>The ERP workspaces seed exact English resources on fresh and existing development databases,
/// sharing the inventory keys that earlier migrations already own.</summary>
public sealed class BillingWorkflowResourcesTests
{
    [Fact]
    public Task Fresh_startup_seeds_billing_menu_and_exact_English_resources()
        => WithDatabase(async (cs, connection) =>
        {
            await Initialize(cs);
            await Initialize(cs);

            AssertResources(connection, ExpectedResources());
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_BILLING_WORKSPACE'").Should().Be("1");
            Scalar(connection, "SELECT PARENT_MENU_ID FROM SYS_MENU WHERE UI_ID='NX_BILLING_WORKSPACE'").Should().Be("FACTORY_SLS");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='menu.NX_BILLING_WORKSPACE' AND LANGUAGE='EnUs'")
                .Should().Be("Estimates, invoices & payments");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_RECURRING_WORKSPACE'").Should().Be("1");
            Scalar(connection, "SELECT PARENT_MENU_ID FROM SYS_MENU WHERE UI_ID='NX_RECURRING_WORKSPACE'").Should().Be("FACTORY_SLS");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='menu.NX_RECURRING_WORKSPACE' AND LANGUAGE='EnUs'")
                .Should().Be("Recurring ERP rules");
        });

    [Fact]
    public Task Existing_startup_fills_missing_billing_resources_once_preserving_custom_values()
        => WithDatabase(async (cs, connection) =>
        {
            await Initialize(cs);
            var expected = ExpectedResources();
            foreach (var key in expected.Keys.Where(key => key.StartsWith("billing.", StringComparison.Ordinal)))
                Exec(connection, "DELETE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY=@key AND LANGUAGE='EnUs'", key);
            Exec(connection, """
                INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY,MENU_ID,LANGUAGE,VALUE)
                VALUES ('billing.heading','CUSTOM','EnUs','Our billing actions'),
                       ('billing.newEstimate','CUSTOM','EnUs',''),
                       ('billing.heading','CUSTOM','KoKr','우리 청구 작업');
                """);
            var menus = Snapshot(connection, "SELECT * FROM SYS_MENU ORDER BY MENU_ID");

            await Initialize(cs);
            await Initialize(cs);

            expected["billing.heading"] = "Our billing actions";
            expected["billing.newEstimate"] = "";
            AssertResources(connection, expected);
            Scalar(connection, "SELECT MENU_ID FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='billing.heading' AND LANGUAGE='EnUs'").Should().Be("CUSTOM");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='billing.heading' AND LANGUAGE='KoKr'").Should().Be("우리 청구 작업");
            Snapshot(connection, "SELECT * FROM SYS_MENU ORDER BY MENU_ID").Should().Equal(menus);
        });

    /// <summary>Every literal fallback of the billing and recurring ERP pages.</summary>
    internal static Dictionary<string, string> ExpectedResources()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new[] { "BillingWorkflowPanel.razor", "BillingWorkflowPanel.razor.cs", "HostBillingWorkspace.razor", "RecurringWorkflowPanel.razor", "RecurringWorkflowPanel.razor.cs", "HostRecurringWorkspace.razor" })
        {
            var source = File.ReadAllText(RepositorySource.GetFile("src/00.Main/NexaOne.Server/Components/Pages/" + file));
            var calls = Regex.Matches(source,
                """\bT\(\s*"(?<key>[^"\\]+)"\s*,\s*"(?:\\.|[^"\\])*"\s*,\s*"(?<en>(?:\\.|[^"\\])*)"\s*\)""");
            calls.Count.Should().BeGreaterThan(0, file);
            Regex.Matches(source, """\bT\(\s*"(?:billing|inventory|recurring)\.[^"\\]+["]""").Count.Should().Be(calls.Count,
                "all ERP workspace calls must have literal fallbacks covered by this contract in {0}", file);
            foreach (Match call in calls)
            {
                var key = call.Groups["key"].Value;
                (key.StartsWith("billing.", StringComparison.Ordinal) || key.StartsWith("inventory.", StringComparison.Ordinal) || key.StartsWith("recurring.", StringComparison.Ordinal)).Should().BeTrue(key);
                var english = JsonSerializer.Deserialize<string>("\"" + call.Groups["en"].Value + "\"")!;
                if (expected.TryGetValue(key, out var previous))
                    english.Should().Be(previous, "a shared key must have one English fallback: {0} in {1}", key, file);
                expected[key] = english;
            }
        }
        expected.Keys.Count(key => key.StartsWith("billing.", StringComparison.Ordinal)).Should().BeGreaterThan(50);
        expected.Keys.Count(key => key.StartsWith("recurring.", StringComparison.Ordinal)).Should().BeGreaterThan(50);
        return expected;
    }

    private static void AssertResources(SqliteConnection connection, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var resource in expected)
        {
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY=@key AND LANGUAGE='EnUs'", resource.Key)
                .Should().Be("1", resource.Key);
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY=@key AND LANGUAGE='EnUs'", resource.Key)
                .Should().Be(resource.Value, resource.Key);
        }
    }

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
        var path = Path.Combine(Path.GetTempPath(), $"nexa-billing-resources-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Foreign Keys=False;Pooling=False";
        try
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            await action(cs, connection);
        }
        finally { File.Delete(path); }
    }

    private static string[] Snapshot(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.ToArray();
    }

    private static void Exec(SqliteConnection connection, string sql, string? key = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (key is not null) command.Parameters.AddWithValue("@key", key);
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql, string? key = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (key is not null) command.Parameters.AddWithValue("@key", key);
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
