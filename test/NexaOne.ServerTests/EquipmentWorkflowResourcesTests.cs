using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.Server;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class EquipmentWorkflowResourcesTests
{
    private static readonly IReadOnlyDictionary<string, string> ClientEnglish = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["error.inventoryPath"] = "The inventory read path is invalid.",
        ["error.inventoryResponse"] = "The inventory response could not be read. Please retry.",
        ["error.inventoryWriteRequest"] = "The inventory write request is invalid.",
        ["error.inventoryWriteOutcome"] = "The write outcome could not be confirmed. Check the current state before retrying.",
        ["error.inventoryIdentityChanged"] = "The signed-in user changed or could not be verified. Sign in as the original user before continuing.",
    };

    [Fact]
    public Task Fresh_startup_seeds_exact_page_and_client_English_without_short_circuiting_full_menu_seed()
        => WithDatabase(async (cs, connection) =>
        {
            await Initialize(cs);
            await Initialize(cs);

            AssertResources(connection, ExpectedResources());
            using var seed = JsonDocument.Parse(File.ReadAllText(RepositorySource.GetFile(
                "src/00.Main/NexaOne.Server/config/seed/nexaone-menu.json")));
            foreach (var row in seed.RootElement.EnumerateArray())
                Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE MENU_ID=@key",
                    row.GetProperty("menuId").GetString()).Should().Be("1");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_INVENTORY_WORKSPACE'").Should().Be("1");
        });

    [Fact]
    public Task Existing_startup_fills_missing_resources_once_preserving_custom_values_menus_roles_and_data()
        => WithDatabase(async (cs, connection) =>
        {
            await Initialize(cs);
            var expected = ExpectedResources();
            // Simulate a database predating these resources: EnsureSchema's existing-table path
            // skips migration DML, so this case requires the actual startup reconciliation hook.
            foreach (var key in expected.Keys)
                Exec(connection, "DELETE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY=@key AND LANGUAGE='EnUs'", key);
            Exec(connection, """
                INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY,MENU_ID,LANGUAGE,VALUE)
                VALUES ('inventory.write.heading','CUSTOM','EnUs','Our equipment actions'),
                       ('inventory.write.loadingRecovery','CUSTOM','EnUs',''),
                       ('error.inventoryWriteOutcome','CUSTOM','EnUs','Our recovery advice'),
                       ('inventory.write.heading','CUSTOM','KoKr','우리 장비 작업'),
                       ('custom.equipment.resource','CUSTOM','EnUs','Leave this alone');
                INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,MENU_TYPE,DISPLAY_SEQUENCE,OPTIONS)
                VALUES ('CUSTOM_EQUIPMENT_MENU','우리 장비','Screen',83,'our options');
                INSERT INTO SYS_ROLE (ROLE_ID,ROLE_NAME,PERMISSIONS,CREATED_BY,UPDATED_BY)
                VALUES ('EQUIPMENT_RESOURCE_CUSTOM','Our role','custom:read','test','test');
                INSERT INTO SYS_MENU_ROLE (MENU_ID,ROLE_ID)
                VALUES ('CUSTOM_EQUIPMENT_MENU','EQUIPMENT_RESOURCE_CUSTOM');
                UPDATE IVT_MATERIAL_LOT SET CURRENT_QTY=123.25 WHERE LOT_ID='LOT_IN_001';
                """);
            var menus = Snapshot(connection, "SELECT * FROM SYS_MENU ORDER BY MENU_ID");
            var roles = Snapshot(connection, "SELECT * FROM SYS_ROLE ORDER BY ROLE_ID");
            var mappings = Snapshot(connection, "SELECT * FROM SYS_MENU_ROLE ORDER BY MENU_ID,ROLE_ID");
            var lots = Snapshot(connection, "SELECT * FROM IVT_MATERIAL_LOT ORDER BY LOT_ID");

            await Initialize(cs);
            await Initialize(cs);

            expected["inventory.write.heading"] = "Our equipment actions";
            expected["inventory.write.loadingRecovery"] = "";
            expected["error.inventoryWriteOutcome"] = "Our recovery advice";
            AssertResources(connection, expected);
            foreach (var key in new[] { "inventory.write.heading", "inventory.write.loadingRecovery", "error.inventoryWriteOutcome" })
                Scalar(connection, "SELECT MENU_ID FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY=@key AND LANGUAGE='EnUs'", key)
                    .Should().Be("CUSTOM");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='inventory.write.heading' AND LANGUAGE='KoKr'")
                .Should().Be("우리 장비 작업");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='custom.equipment.resource' AND LANGUAGE='EnUs'")
                .Should().Be("Leave this alone");
            Snapshot(connection, "SELECT * FROM SYS_MENU ORDER BY MENU_ID").Should().Equal(menus);
            Snapshot(connection, "SELECT * FROM SYS_ROLE ORDER BY ROLE_ID").Should().Equal(roles);
            Snapshot(connection, "SELECT * FROM SYS_MENU_ROLE ORDER BY MENU_ID,ROLE_ID").Should().Equal(mappings);
            Snapshot(connection, "SELECT * FROM IVT_MATERIAL_LOT ORDER BY LOT_ID").Should().Equal(lots);
            Scalar(connection, "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID='LOT_IN_001'").Should().Be("123.25");
        });

    [Theory]
    [InlineData("Production", "Sqlite")]
    [InlineData("Development", "SqlServer")]
    public Task Startup_resource_replay_remains_guarded_by_development_and_SQLite(string environment, string provider)
        => WithDatabase(async (cs, connection) =>
        {
            Exec(connection, """
                CREATE TABLE SYS_MULTI_LANGUAGE_RESOURCE
                    (RESOURCE_KEY TEXT NOT NULL, MENU_ID TEXT, LANGUAGE TEXT NOT NULL, VALUE TEXT,
                     PRIMARY KEY (RESOURCE_KEY,LANGUAGE));
                INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE VALUES ('custom.equipment.resource','CUSTOM','EnUs','Keep');
                """);
            await Initialize(cs, environment, provider);
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MULTI_LANGUAGE_RESOURCE").Should().Be("1");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE").Should().Be("Keep");
        });

    internal static Dictionary<string, string> ExpectedResources()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new[] { "EquipmentWorkflowPanel.razor", "EquipmentWorkflowPanel.razor.cs", "StockWorkflowPanel.razor", "StockWorkflowPanel.razor.cs", "StockBalanceReportPanel.razor", "HostInventoryWorkspace.razor" })
        {
            var source = File.ReadAllText(RepositorySource.GetFile("src/00.Main/NexaOne.Server/Components/Pages/" + file));
            var calls = Regex.Matches(source,
                """\bT\(\s*"(?<key>[^"\\]+)"\s*,\s*"(?:\\.|[^"\\])*"\s*,\s*"(?<en>(?:\\.|[^"\\])*)"\s*\)""");
            calls.Count.Should().BeGreaterThan(0, file);
            Regex.Matches(source, """\bT\(\s*"inventory\.[^"\\]+["]""").Count.Should().Be(calls.Count,
                "all inventory calls must have literal fallbacks covered by this contract in {0}", file);
            foreach (Match call in calls)
            {
                var key = call.Groups["key"].Value;
                key.Should().StartWith("inventory.");
                var english = JsonSerializer.Deserialize<string>("\"" + call.Groups["en"].Value + "\"")!;
                if (expected.TryGetValue(key, out var previous))
                    english.Should().Be(previous, "a shared key must have one English fallback: {0} in {1}", key, file);
                expected[key] = english;
            }
        }
        var client = File.ReadAllText(RepositorySource.GetFile("src/01.Web/NexaOne.Web.Components/Services/Api/ApiClient.cs"));
        var clientKeys = Regex.Matches(client, """\b_ui\.T\(\s*"(?<key>error\.inventory[^"\\]+)["]""")
            .Select(match => match.Groups["key"].Value).Distinct(StringComparer.Ordinal).ToArray();
        clientKeys.Should().BeEquivalentTo(ClientEnglish.Keys, "new client errors need an agreed English resource");
        foreach (var resource in ClientEnglish) expected.Add(resource.Key, resource.Value);
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

    private static async Task Initialize(string connectionString, string environment = "Development", string provider = "Sqlite")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider,
            ["ConnectionStrings:NexaOne"] = connectionString,
        });
        await using var app = builder.Build();
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);
    }

    private static async Task WithDatabase(Func<string, SqliteConnection, Task> action)
    {
        // Cleanup targets only this newly allocated file. No pooling or recursive deletion.
        var path = Path.Combine(Path.GetTempPath(), $"nexa-equipment-resources-{Guid.NewGuid():N}.db");
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
