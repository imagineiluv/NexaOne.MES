using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.Application.Query;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server;
using NexaOne.Server.Services;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class InventoryWorkspaceMenuTests
{
    private const string MenuId = "NX_INVENTORY_WORKSPACE";

    [Fact]
    public void Seed_registers_a_literal_inventory_route_under_IVT()
    {
        using var seed = ReadSeed();
        var menu = seed.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("menuId").GetString() == MenuId);
        menu.GetProperty("menuName").GetString().Should().Be("재고·장비 공유");
        menu.GetProperty("parentMenuId").GetString().Should().Be("FACTORY_IVT");
        menu.GetProperty("displaySequence").GetInt32().Should().Be(3);
        menu.GetProperty("menuType").GetString().Should().Be("Screen");
        menu.GetProperty("uiId").GetString().Should().Be(MenuId);
        HostLiteralMetaRoutes.Contains(MenuId).Should().BeTrue();
    }

    [Fact]
    public Task Fresh_schema_does_not_short_circuit_full_menu_seed_and_seeds_all_page_English_resources()
        => WithDatabase(async (cs, connection) =>
        {
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU").Should().Be("0");
            await Initialize(cs);
            await Initialize(cs);

            using var seed = ReadSeed();
            foreach (var row in seed.RootElement.EnumerateArray())
                Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE MENU_ID=@id",
                    row.GetProperty("menuId").GetString()).Should().Be("1");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_INVENTORY_WORKSPACE'")
                .Should().Be("1");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='menu.NX_INVENTORY_WORKSPACE' AND LANGUAGE='EnUs'")
                .Should().Be("Inventory & equipment");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='error.inventoryPath' AND LANGUAGE='EnUs'")
                .Should().Be("The inventory read path is invalid.");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='error.inventoryResponse' AND LANGUAGE='EnUs'")
                .Should().Be("The inventory response could not be read. Please retry.");

            // Check the resource contract against the page's actual literal fallbacks, so adding a
            // visible string without a migration resource fails even when old keys are all present.
            var page = File.ReadAllText(RepositorySource.GetFile(
                "src/00.Main/NexaOne.Server/Components/Pages/HostInventoryWorkspace.razor"));
            var calls = Regex.Matches(page,
                """\bT\("(?<key>[^"\\]+)",\s*"(?:\\.|[^"\\])*",\s*"(?<en>(?:\\.|[^"\\])*)"\)""");
            calls.Count.Should().BeGreaterThan(0);
            foreach (Match call in calls)
            {
                var key = "inventory." + call.Groups["key"].Value;
                var english = JsonSerializer.Deserialize<string>("\"" + call.Groups["en"].Value + "\"");
                Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                    "WHERE RESOURCE_KEY=@id AND LANGUAGE='EnUs'", key).Should().Be(english, key);
            }
        });

    [Fact]
    public Task Existing_startup_appends_once_and_preserves_custom_menus_roles_history_and_translations()
        => WithDatabase(async (cs, connection) =>
        {
            Exec(connection, """
                INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,MENU_TYPE,DISPLAY_SEQUENCE)
                VALUES ('FACTORY_IVT','우리 창고','Folder',91);
                INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,PARENT_MENU_ID,DISPLAY_SEQUENCE,UI_ID,OPTIONS)
                VALUES ('CUSTOM_STOCK','우리 재고','FACTORY_IVT',41,'CUSTOM_STOCK','custom options');
                INSERT INTO SYS_MENU_ROLE (MENU_ID,ROLE_ID) VALUES ('CUSTOM_STOCK','ADMIN');
                INSERT INTO SYS_ROLE (ROLE_ID,ROLE_NAME,PERMISSIONS,CREATED_BY,UPDATED_BY)
                VALUES ('INVENTORY_MENU_CUSTOM','Custom role','custom:read','test','test');
                INSERT INTO SYS_FAVORITE_MENU (USER_ID,MENU_ID,DISPLAY_SEQUENCE,CREATED_AT)
                VALUES ('admin','CUSTOM_STOCK',13,'2026-09-20T00:00:00Z');
                INSERT INTO SYS_RECENT_MENU (USER_ID,MENU_ID,LAST_USED_AT)
                VALUES ('admin','CUSTOM_STOCK','2026-09-20T01:00:00Z');
                DELETE FROM SYS_MULTI_LANGUAGE_RESOURCE
                 WHERE RESOURCE_KEY LIKE 'inventory.%'
                    OR RESOURCE_KEY IN ('menu.NX_INVENTORY_WORKSPACE','error.inventoryPath','error.inventoryResponse');
                INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY,MENU_ID,LANGUAGE,VALUE)
                VALUES ('inventory.title','CUSTOM','EnUs','Our inventory'),
                       ('menu.NX_INVENTORY_WORKSPACE','CUSTOM','EnUs','Our workspace'),
                       ('inventory.title','CUSTOM','KoKr','사용자 재고'),
                       ('error.inventoryPath','CUSTOM','EnUs','Our invalid inventory path');
                """);

            await Initialize(cs);
            await Initialize(cs);

            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UI_ID='NX_INVENTORY_WORKSPACE'")
                .Should().Be("1");
            Scalar(connection, "SELECT DISPLAY_SEQUENCE FROM SYS_MENU WHERE MENU_ID='NX_INVENTORY_WORKSPACE'")
                .Should().Be("42");
            Scalar(connection, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='NX_INVENTORY_WORKSPACE'")
                .Should().Be("재고·장비 공유");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE MENU_ID='CUSTOM_STOCK' " +
                "AND MENU_NAME='우리 재고' AND DISPLAY_SEQUENCE=41 AND OPTIONS='custom options'").Should().Be("1");
            Scalar(connection, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='FACTORY_IVT'").Should().Be("우리 창고");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU_ROLE WHERE MENU_ID='NX_INVENTORY_WORKSPACE'")
                .Should().Be("0");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU_ROLE WHERE MENU_ID='CUSTOM_STOCK' AND ROLE_ID='ADMIN'")
                .Should().Be("1");
            Scalar(connection, "SELECT PERMISSIONS FROM SYS_ROLE WHERE ROLE_ID='INVENTORY_MENU_CUSTOM'")
                .Should().Be("custom:read");
            Scalar(connection, "SELECT DISPLAY_SEQUENCE FROM SYS_FAVORITE_MENU WHERE MENU_ID='CUSTOM_STOCK'")
                .Should().Be("13");
            Scalar(connection, "SELECT LAST_USED_AT FROM SYS_RECENT_MENU WHERE MENU_ID='CUSTOM_STOCK'")
                .Should().Be("2026-09-20T01:00:00Z");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='inventory.title' AND LANGUAGE='EnUs'").Should().Be("Our inventory");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='inventory.title' AND LANGUAGE='KoKr'").Should().Be("사용자 재고");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='menu.NX_INVENTORY_WORKSPACE' AND LANGUAGE='EnUs'").Should().Be("Our workspace");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='inventory.loading' AND LANGUAGE='EnUs'").Should().Be("Loading…");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='error.inventoryPath' AND LANGUAGE='EnUs' AND MENU_ID='CUSTOM'")
                .Should().Be("Our invalid inventory path");
            Scalar(connection, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='error.inventoryResponse' AND LANGUAGE='EnUs'")
                .Should().Be("The inventory response could not be read. Please retry.");
        });

    [Theory]
    [InlineData(MenuId, MenuId)]
    [InlineData("CUSTOM_INVENTORY_ROUTE", MenuId)]
    [InlineData("CUSTOM_INVENTORY_ROUTE", "nx_inventory_workspace")]
    [InlineData("CUSTOM_INVENTORY_ROUTE", "Nx_Inventory_Workspace")]
    public Task Existing_menu_identity_or_route_is_preserved_including_invalid_state_and_role(
        string existingId, string existingUiId)
        => WithDatabase(async (cs, connection) =>
        {
            Exec(connection, """
                INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,MENU_TYPE) VALUES ('FACTORY_IVT','창고','Folder');
                INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,PARENT_MENU_ID,DISPLAY_SEQUENCE,UI_ID,VALID_STATE)
                VALUES (@id,'내 공유 화면','CUSTOM_PARENT',73,'NX_INVENTORY_WORKSPACE','Invalid');
                INSERT INTO SYS_MENU_ROLE (MENU_ID,ROLE_ID) VALUES (@id,'ADMIN');
                """, existingId);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE SYS_MENU SET UI_ID=@ui WHERE MENU_ID=@id";
                command.Parameters.AddWithValue("@ui", existingUiId);
                command.Parameters.AddWithValue("@id", existingId);
                command.ExecuteNonQuery();
            }

            await Initialize(cs);
            await Initialize(cs);

            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE UPPER(UI_ID)='NX_INVENTORY_WORKSPACE'")
                .Should().Be("1");
            Scalar(connection, "SELECT UI_ID FROM SYS_MENU WHERE MENU_ID=@id", existingId).Should().Be(existingUiId);
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU WHERE MENU_ID=@id AND MENU_NAME='내 공유 화면' " +
                "AND PARENT_MENU_ID='CUSTOM_PARENT' AND DISPLAY_SEQUENCE=73 AND VALID_STATE='Invalid'", existingId)
                .Should().Be("1");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU_ROLE WHERE MENU_ID=@id AND ROLE_ID='ADMIN'", existingId)
                .Should().Be("1");
        });

    [Fact]
    public Task Maximum_custom_sequence_is_preserved_without_overflow_or_renumbering()
        => WithDatabase(async (cs, connection) =>
        {
            Exec(connection, """
                INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,MENU_TYPE) VALUES ('FACTORY_IVT','창고','Folder');
                INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,PARENT_MENU_ID,DISPLAY_SEQUENCE)
                VALUES ('CUSTOM_LAST','My last menu','FACTORY_IVT',2147483647);
                """);
            await Initialize(cs);
            await Initialize(cs);
            Scalar(connection, "SELECT DISPLAY_SEQUENCE FROM SYS_MENU WHERE MENU_ID='NX_INVENTORY_WORKSPACE'")
                .Should().Be(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Scalar(connection, "SELECT DISPLAY_SEQUENCE FROM SYS_MENU WHERE MENU_ID='CUSTOM_LAST'")
                .Should().Be(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

    [Fact]
    public Task Existing_custom_tree_without_IVT_is_not_replaced_or_given_an_orphan_leaf()
        => WithDatabase(async (cs, connection) =>
        {
            Exec(connection, "INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,MENU_TYPE) VALUES ('CUSTOM_ROOT','My root','Folder')");
            await Initialize(cs);
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU").Should().Be("1");
            Scalar(connection, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='CUSTOM_ROOT'").Should().Be("My root");
        });

    [Fact]
    public Task Unmapped_leaf_is_discoverable_without_inventory_role_grants()
        => WithDatabase(async (cs, connection) =>
        {
            await Initialize(cs);
            Exec(connection, """
                INSERT INTO SYS_ROLE (ROLE_ID,ROLE_NAME,PERMISSIONS,CREATED_BY,UPDATED_BY)
                VALUES ('MENU_ONLY','Menu only','','test','test');
                INSERT INTO SYS_USER (USER_ID,USER_NAME,PASSWORD_HASH,EMAIL,ROLE_ID,CREATED_BY,UPDATED_BY)
                VALUES ('menu-only','Menu only','unused','menu@example.test','MENU_ONLY','test','test');
                """);
            var registry = FileQueryRegistry.Load("sqlite", RepositorySource.GetDirectory(
                "src/00.Main/NexaOne.Server/config/db/queries"));
            registry.TryGet("SYS.MenuTreeForUser", out var query).Should().BeTrue();
            using var command = connection.CreateCommand();
            command.CommandText = query!.Sql;
            command.Parameters.AddWithValue("@currentUser", "menu-only");
            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(reader.GetOrdinal("MENU_ID")));
            ids.Should().Contain(MenuId);
            // This is menu discovery only. Scope membership and business grants are checked by IVT APIs.
        });

    private static JsonDocument ReadSeed() => JsonDocument.Parse(File.ReadAllText(RepositorySource.GetFile(
        "src/00.Main/NexaOne.Server/config/seed/nexaone-menu.json")));

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
        // Only this GUID-named file is deleted; pooling is disabled before the file is opened.
        var path = Path.Combine(Path.GetTempPath(), $"nexa-inventory-menu-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Foreign Keys=False;Pooling=False";
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            using var connection = new SqliteConnection(cs);
            connection.Open();
            await action(cs, connection);
        }
        finally { File.Delete(path); }
    }

    private static void Exec(SqliteConnection connection, string sql, string? id = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql, string? id = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("@id", id);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }
}
