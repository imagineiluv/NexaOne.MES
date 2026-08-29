using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server;
using NexaOne.Web.Services.Meta;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>사용자에게 보이는 MES 메뉴명은 내부 모듈 코드와 분리되고 현재 언어 리소스로 표현돼야 한다.</summary>
public sealed class MesMenuTerminologyTests
{
    private static readonly IReadOnlyDictionary<string, string> KoreanRoots =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FACTORY_PPM"] = "생산계획",
            ["FACTORY_WPM"] = "생산실행",
            ["FACTORY_DLV"] = "출하관리",
            ["FACTORY_PRC"] = "구매관리",
            ["FACTORY_QCA"] = "품질검사",
            ["FACTORY_EMS"] = "설비보전",
            ["EES_EPT"] = "설비지표",
            ["EES_FDC"] = "설비데이터 수집",
            ["QMS"] = "품질관리",
            ["FACTORY_STD_SINGLE"] = "레시피 기준정보",
            ["FACTORY_MDM"] = "기준정보",
            ["FACTORY_COM"] = "공통관리",
            ["MI_SYSTEM_2_0"] = "시스템관리",
        };

    private static readonly IReadOnlyDictionary<string, string> EnglishRoots =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FACTORY_PPM"] = "Production Planning",
            ["FACTORY_WPM"] = "Production Execution",
            ["FACTORY_QCA"] = "Quality Inspection",
            ["FACTORY_EMS"] = "Equipment Maintenance",
            ["EES_EPT"] = "Equipment Metrics",
            ["EES_FDC"] = "Equipment Data Collection",
            ["QMS"] = "Quality Management",
            ["FACTORY_MDM"] = "Master Data",
            ["FACTORY_COM"] = "Common Administration",
            ["MI_SYSTEM_2_0"] = "System Administration",
        };

    private static readonly IReadOnlyDictionary<string, string> KoreanSalesScreens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FACTORY_SLS_SALES_ORDER"] = "수주 관리",
            ["FACTORY_SLS_SALES_REQUEST"] = "판매 요청",
            ["FACTORY_SLS_REPORT_DELIVERY"] = "출하 현황",
        };

    private static readonly IReadOnlyDictionary<string, string> EnglishSalesResources =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["menu.FACTORY_SLS_SALES_ORDER"] = "Sales Order Management",
            ["screen.FACTORY_SLS_SALES_ORDER.title"] = "Sales Order Management",
            ["menu.FACTORY_SLS_SALES_REQUEST"] = "Sales Request",
            ["screen.FACTORY_SLS_SALES_REQUEST.title"] = "Sales Request",
            ["menu.FACTORY_SLS_REPORT_DELIVERY"] = "Shipping Status",
            ["screen.FACTORY_SLS_REPORT_DELIVERY.title"] = "Shipping Status",
        };

    [Fact]
    public void Menu_seed_uses_concise_Korean_MES_terms_without_internal_module_suffixes()
    {
        var json = File.ReadAllText(RepositorySource.GetFile(
            "src/00.Main/NexaOne.Server/config/seed/nexaone-menu.json"));
        var rows = JsonSerializer.Deserialize<List<MenuSeedRow>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        rows.Should().NotBeNull();
        var roots = rows!.Where(row => row.ParentMenuId is null)
            .ToDictionary(row => row.MenuId, row => row.MenuName, StringComparer.Ordinal);
        foreach (var expected in KoreanRoots)
            roots[expected.Key].Should().Be(expected.Value);

        roots.Values.Should().OnlyContain(name =>
            !new[] { "(QCA)", "(QMS)", "(PPM)", "(WPM)", "(EMS)", "(EPT)", "(FDC)", "(MDM)", "(COM)" }
                .Any(code => name.Contains(code, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Sales_menu_seed_and_code_screen_titles_share_one_canonical_term()
    {
        var json = File.ReadAllText(RepositorySource.GetFile(
            "src/00.Main/NexaOne.Server/config/seed/nexaone-menu.json"));
        var rows = JsonSerializer.Deserialize<List<MenuSeedRow>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var menus = rows!.ToDictionary(row => row.MenuId, row => row.MenuName, StringComparer.Ordinal);
        var screens = new InMemoryScreenDefinitionProvider();

        foreach (var expected in KoreanSalesScreens)
        {
            menus[expected.Key].Should().Be(expected.Value);
            screens.Get(expected.Key)!.Title.Should().Be(expected.Value);
        }
    }

    [Fact]
    public void Fresh_schema_seeds_descriptive_English_resources_for_current_language_mode()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            using var connection = new SqliteConnection(cs);
            connection.Open();
            foreach (var expected in EnglishRoots)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                    "WHERE RESOURCE_KEY = @key AND LANGUAGE = 'EnUs'";
                command.Parameters.AddWithValue("@key", $"menu.{expected.Key}");
                Convert.ToString(command.ExecuteScalar()).Should().Be(expected.Value);
            }
            foreach (var expected in EnglishSalesResources)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                    "WHERE RESOURCE_KEY = @key AND LANGUAGE = 'EnUs'";
                command.Parameters.AddWithValue("@key", expected.Key);
                Convert.ToString(command.ExecuteScalar()).Should().Be(expected.Value);
            }
        }
        finally { TryDelete(cs); }
    }

    [Fact]
    public void Existing_development_database_updates_only_untouched_legacy_labels()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            using (var connection = new SqliteConnection(cs))
            {
                connection.Open();
                Exec(connection,
                    "INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,DISPLAY_SEQUENCE,MENU_TYPE,PROGRAM_ID,UI_ID,VALID_STATE) " +
                    "VALUES ('FACTORY_QCA','품질 검사(QCA)',1,'Folder','','','Valid')");
                Exec(connection,
                    "INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,DISPLAY_SEQUENCE,MENU_TYPE,PROGRAM_ID,UI_ID,VALID_STATE) " +
                    "VALUES ('FACTORY_MDM','회사 기준정보',2,'Folder','','','Valid')");
                Exec(connection,
                    "INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,DISPLAY_SEQUENCE,MENU_TYPE,PROGRAM_ID,UI_ID,VALID_STATE) " +
                    "VALUES ('FACTORY_SLS_SALES_ORDER','수주관리',3,'Screen','','FACTORY_SLS_SALES_ORDER','Valid')");
                Exec(connection,
                    "INSERT INTO SYS_MENU (MENU_ID,MENU_NAME,DISPLAY_SEQUENCE,MENU_TYPE,PROGRAM_ID,UI_ID,VALID_STATE) " +
                    "VALUES ('FACTORY_SLS_SALES_REQUEST','판매 주문 접수',4,'Screen','','FACTORY_SLS_SALES_REQUEST','Valid')");
                Exec(connection,
                    "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE='QCA' " +
                    "WHERE RESOURCE_KEY='menu.FACTORY_QCA' AND LANGUAGE='EnUs'");
                Exec(connection,
                    "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE='Corporate Master Data' " +
                    "WHERE RESOURCE_KEY='menu.FACTORY_MDM' AND LANGUAGE='EnUs'");
                Exec(connection,
                    "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE='Sales Order Receipt' " +
                    "WHERE RESOURCE_KEY='menu.FACTORY_SLS_SALES_REQUEST' AND LANGUAGE='EnUs'");
                Exec(connection,
                    "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE='Delivery Status' " +
                    "WHERE RESOURCE_KEY='screen.FACTORY_SLS_REPORT_DELIVERY.title' AND LANGUAGE='EnUs'");

                var legacyOrderJson = JsonSerializer.Serialize(new
                {
                    uiId = "FACTORY_SLS_SALES_ORDER",
                    title = "판매 오더 관리",
                    fields = new[] { new { label = "판매오더 번호" }, new { label = "판매오더명" } },
                    columns = new[] { new { caption = "판매오더 ID" }, new { caption = "판매오더명" } },
                });
                var legacyRequestJson = JsonSerializer.Serialize(new
                {
                    uiId = "FACTORY_SLS_SALES_REQUEST",
                    title = "판매 요청",
                    columns = new[] { new { caption = "판매오더" } },
                });
                var customDeliveryJson = JsonSerializer.Serialize(new
                    { uiId = "FACTORY_SLS_REPORT_DELIVERY", title = "출하 사용자 분석" });
                Exec(connection,
                    "INSERT INTO SYS_SCREEN_DEFINITION (UI_ID,TITLE,DEFINITION_JSON,CREATED_BY,UPDATED_BY) VALUES " +
                    $"('FACTORY_SLS_SALES_ORDER','판매 오더 관리','{legacyOrderJson}','SYSTEM','SYSTEM')");
                Exec(connection,
                    "INSERT INTO SYS_SCREEN_DEFINITION (UI_ID,TITLE,DEFINITION_JSON,CREATED_BY,UPDATED_BY) VALUES " +
                    $"('FACTORY_SLS_SALES_REQUEST','판매 요청','{legacyRequestJson}','SYSTEM','SYSTEM')");
                Exec(connection,
                    "INSERT INTO SYS_SCREEN_DEFINITION (UI_ID,TITLE,DEFINITION_JSON,CREATED_BY,UPDATED_BY) VALUES " +
                    $"('FACTORY_SLS_REPORT_DELIVERY','출하 사용자 분석','{customDeliveryJson}','SYSTEM','SYSTEM')");
            }

            NexaOneDevelopmentDatabaseInitializer.NormalizeDevMenuTerminology(cs);

            Scalar(cs, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='FACTORY_QCA'")
                .Should().Be("품질검사");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='menu.FACTORY_QCA' AND LANGUAGE='EnUs'")
                .Should().Be("Quality Inspection");
            Scalar(cs, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='FACTORY_MDM'")
                .Should().Be("회사 기준정보", "관리 화면에서 사용자가 바꾼 이름은 보존해야 한다");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='menu.FACTORY_MDM' AND LANGUAGE='EnUs'")
                .Should().Be("Corporate Master Data", "사용자 번역도 레거시 기본값이 아니면 보존해야 한다");
            Scalar(cs, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='FACTORY_SLS_SALES_ORDER'")
                .Should().Be("수주 관리");
            Scalar(cs, "SELECT MENU_NAME FROM SYS_MENU WHERE MENU_ID='FACTORY_SLS_SALES_REQUEST'")
                .Should().Be("판매 요청");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='menu.FACTORY_SLS_SALES_REQUEST' AND LANGUAGE='EnUs'")
                .Should().Be("Sales Request");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE WHERE RESOURCE_KEY='screen.FACTORY_SLS_REPORT_DELIVERY.title' AND LANGUAGE='EnUs'")
                .Should().Be("Shipping Status");

            Scalar(cs, "SELECT TITLE FROM SYS_SCREEN_DEFINITION WHERE UI_ID='FACTORY_SLS_SALES_ORDER'")
                .Should().Be("수주 관리");
            var normalizedOrderJson = Scalar(cs,
                "SELECT DEFINITION_JSON FROM SYS_SCREEN_DEFINITION WHERE UI_ID='FACTORY_SLS_SALES_ORDER'");
            normalizedOrderJson.Should().Contain(JsonSerializer.Serialize("수주 관리"));
            normalizedOrderJson.Should().NotContain(JsonSerializer.Serialize("판매 오더 관리"));
            normalizedOrderJson.Should().Contain(JsonSerializer.Serialize("수주 번호"));
            normalizedOrderJson.Should().Contain(JsonSerializer.Serialize("수주명"));
            normalizedOrderJson.Should().Contain(JsonSerializer.Serialize("수주 번호"));
            normalizedOrderJson.Should().NotContain(JsonSerializer.Serialize("판매오더 번호"));
            normalizedOrderJson.Should().NotContain(JsonSerializer.Serialize("판매오더명"));
            normalizedOrderJson.Should().NotContain(JsonSerializer.Serialize("판매오더 ID"));
            Scalar(cs, "SELECT DEFINITION_JSON FROM SYS_SCREEN_DEFINITION WHERE UI_ID='FACTORY_SLS_SALES_REQUEST'")
                .Should().Contain(JsonSerializer.Serialize("수주 번호"));
            Scalar(cs, "SELECT TITLE FROM SYS_SCREEN_DEFINITION WHERE UI_ID='FACTORY_SLS_REPORT_DELIVERY'")
                .Should().Be("출하 사용자 분석", "Designer에서 바꾼 화면 제목은 보존해야 한다");
            Scalar(cs, "SELECT DEFINITION_JSON FROM SYS_SCREEN_DEFINITION WHERE UI_ID='FACTORY_SLS_REPORT_DELIVERY'")
                .Should().Contain(JsonSerializer.Serialize("출하 사용자 분석"));
        }
        finally { TryDelete(cs); }
    }

    [Fact]
    public void Incremental_schema_normalizes_legacy_work_order_labels_without_touching_custom_text()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            using (var connection = new SqliteConnection(cs))
            {
                connection.Open();
                Exec(connection,
                    "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE='W/O Management' " +
                    "WHERE RESOURCE_KEY='menu.FACTORY_PPM_WORK_ORDER' AND LANGUAGE='EnUs'");
                Exec(connection,
                    "INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY,MENU_ID,LANGUAGE,VALUE) " +
                    "VALUES ('screen.FACTORY_PPM_WORK_ORDER.title','COMMON','KoKr','W/O 관리')");
                Exec(connection,
                    "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE='W/O 관리' " +
                    "WHERE RESOURCE_KEY='screen.FACTORY_PPM_WORK_ORDER.title' AND LANGUAGE='KoKr'");
                Exec(connection,
                    "INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY,MENU_ID,LANGUAGE,VALUE) " +
                    "VALUES ('custom.work-order-label','COMMON','EnUs','My custom label')");
            }

            SqliteSchemaInitializer.EnsureSchema(cs);

            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='menu.FACTORY_PPM_WORK_ORDER' AND LANGUAGE='EnUs'")
                .Should().Be("Work Management");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='screen.FACTORY_PPM_WORK_ORDER.title' AND LANGUAGE='KoKr'")
                .Should().Be("작업 관리");
            Scalar(cs, "SELECT VALUE FROM SYS_MULTI_LANGUAGE_RESOURCE " +
                "WHERE RESOURCE_KEY='custom.work-order-label' AND LANGUAGE='EnUs'")
                .Should().Be("My custom label");
        }
        finally { TryDelete(cs); }
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
        => $"Data Source={Path.Combine(Path.GetTempPath(), $"nexa-menu-terms-{Guid.NewGuid():N}.db")};Foreign Keys=False";

    private static void TryDelete(string cs)
    {
        var file = cs.Replace("Data Source=", string.Empty, StringComparison.Ordinal).Split(';')[0];
        try { File.Delete(file); } catch { /* 임시 테스트 DB 정리 실패는 결과를 가리지 않는다. */ }
    }

    private sealed record MenuSeedRow(string MenuId, string MenuName, string? ParentMenuId);
}
