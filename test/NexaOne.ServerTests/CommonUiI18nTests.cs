using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>공통 화면 문구는 현재 언어를 바꿔도 한국어 폴백이 섞이지 않아야 한다.</summary>
public sealed class CommonUiI18nTests
{
    [Fact]
    public void Shell_synchronizes_the_html_language_on_load_and_language_toggle()
    {
        var repository = RepositorySource.Root;
        var host = File.ReadAllText(Path.Combine(repository, "src", "00.Main", "NexaOne.Server", "Components", "HostApp.razor"));
        var shell = File.ReadAllText(Path.Combine(repository, "src", "00.Main", "NexaOne.Server", "Components", "MesShellLayout.razor"));

        host.Should().Contain("window.nxSetLanguage");
        host.Should().Contain("document.documentElement.lang = language === 'EnUs' ? 'en' : 'ko'");
        shell.Should().Contain("!string.Equals(_documentLanguageApplied, _language",
            "an asynchronously loaded persisted language must be applied after the first render as well");
        Regex.Matches(shell, "JS\\.InvokeVoidAsync\\(\"nxSetLanguage\"")
            .Should().HaveCount(2, "the initial user language and every successful toggle must update the document language");
    }

    [Fact]
    public void Every_literal_shared_ui_key_has_an_English_resource()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            var used = LiteralUiKeys();
            var seeded = new HashSet<string>(StringComparer.Ordinal);
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT RESOURCE_KEY FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE LANGUAGE='EnUs'";
            using var reader = command.ExecuteReader();
            while (reader.Read()) seeded.Add(reader.GetString(0));

            used.Where(key => !seeded.Contains(key)).Should().BeEmpty(
                "the active language must cover shared visible text and accessibility labels");
        }
        finally { TryDelete(cs); }
    }

    [Fact]
    public void Existing_development_database_backfills_missing_keys_without_overwriting_custom_translation()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            using (var connection = new SqliteConnection(cs))
            {
                connection.Open();
                Exec(connection, "DELETE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='common.gridTools' AND LANGUAGE='EnUs'");
                Exec(connection, "DELETE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='shell.resizeNavigation' AND LANGUAGE='EnUs'");
                Exec(connection, "DELETE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='shell.resizeNavigationHint' AND LANGUAGE='EnUs'");
                Exec(connection, "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE='My Cards' WHERE RESOURCE_KEY='grid.viewCard' AND LANGUAGE='EnUs'");
            }

            NexaOneDevelopmentDatabaseInitializer.SeedDevCommonUiResourcesIfMissing(cs);
            NexaOneDevelopmentDatabaseInitializer.SeedDevCommonUiResourcesIfMissing(cs);

            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='common.gridTools' AND LANGUAGE='EnUs'")
                .Should().Be("List tools");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='shell.resizeNavigation' AND LANGUAGE='EnUs'")
                .Should().Be("Resize navigation");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='shell.resizeNavigationHint' AND LANGUAGE='EnUs'")
                .Should().Be("Drag or use the left and right arrow keys to resize");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='grid.viewCard' AND LANGUAGE='EnUs'")
                .Should().Be("My Cards", "an administrator's translation must win over the default seed");
            Scalar(cs, "SELECT COUNT(*) FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='common.gridTools' AND LANGUAGE='EnUs'")
                .Should().Be("1");
        }
        finally { TryDelete(cs); }
    }

    private static HashSet<string> LiteralUiKeys()
    {
        var repository = RepositorySource.Root;
        var roots = new[]
        {
            Path.Combine(repository, "src", "00.Main", "NexaOne.Server"),
            Path.Combine(repository, "src", "01.Web", "NexaOne.Web.Components"),
        };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var pattern = new Regex("(?:Ui\\.)?T\\(\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        foreach (var root in roots)
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                                 || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                     .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            keys.Add(match.Groups[1].Value);
        return keys;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(string cs, string sql)
    {
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static string NewDb()
        => $"Data Source={Path.Combine(Path.GetTempPath(), $"nexa-common-ui-{Guid.NewGuid():N}.db")};Foreign Keys=False";

    private static void TryDelete(string cs)
    {
        var file = cs.Replace("Data Source=", string.Empty, StringComparison.Ordinal).Split(';')[0];
        try { File.Delete(file); } catch { /* 임시 테스트 DB 정리 실패는 결과를 가리지 않는다. */ }
    }
}
