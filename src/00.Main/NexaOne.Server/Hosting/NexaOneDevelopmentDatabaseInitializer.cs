using Microsoft.AspNetCore.Builder;

namespace NexaOne.Server;

/// <summary>
/// 개발 환경의 SQLite 화면 확인에 필요한 최소 데이터를 준비한다.
/// 운영 조립 코드와 분리되어 있으며 모든 시드는 재실행해도 중복되지 않도록 구성한다.
/// </summary>
internal static class NexaOneDevelopmentDatabaseInitializer
{
    /// <summary>
    /// 개발 SQLite 환경일 때만 스키마와 메뉴, 기준정보, 생산오더 계층 및 배치 정의를 순서대로 준비한다.
    /// </summary>
    /// <param name="app">환경과 연결 설정을 제공하는 웹 애플리케이션이다.</param>
    public static void Initialize(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Environment.IsDevelopment()
            || !string.Equals(app.Configuration.GetValue<string>("Database:Provider"), "Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        var connectionString = app.Configuration.GetConnectionString("NexaOne");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        // FK 참조 대상인 스키마와 기준정보를 먼저 만들고, 이를 참조하는 생산오더 계층을 나중에 보장한다.
        NexaOne.Infrastructure.Persistence.SqliteSchemaInitializer.EnsureSchema(connectionString);
        SeedDevOperatorScreensIfMissing(connectionString);
        SeedDevMenuIfEmpty(connectionString);
        NormalizeDevMenuTerminology(connectionString);
        SeedDevCommonUiResourcesIfMissing(connectionString);
        EnsureDevQmsSampleLotReferences(connectionString);
        SeedDevMasterDataIfEmpty(connectionString);
        EnsureDevRoutingStepProcessMappings(connectionString);
        EnsureDevPomOrderHierarchy(connectionString);
        SeedDevBatchDefinitionsIfEmpty(connectionString);
    }

    /// <summary>
    /// V106 이전에 생성된 개발 DB의 라우팅 스텝에 공정 매핑을 보강한다.
    /// 사용자 정의 값은 보존하고, 제공 데모 라우팅의 빈 값만 채워 SerialRoute 검증에 사용한다.
    /// </summary>
    static void EnsureDevRoutingStepProcessMappings(string connectionString)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var mapping in new[]
                 {
                     (RoutingId: "RT01", StepNo: 10, ProcessId: "PROC01"),
                     (RoutingId: "RT01", StepNo: 20, ProcessId: "PROC02"),
                     (RoutingId: "RT02", StepNo: 10, ProcessId: "PROC01"),
                 })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE MDM_ROUTING_STEP
                   SET PROCESS_ID = @processId,
                       UPDATED_BY = 'SYSTEM',
                       UPDATED_AT = @updatedAt
                 WHERE ROUTING_ID = @routingId
                   AND STEP_NO = @stepNo
                   AND (PROCESS_ID IS NULL OR TRIM(PROCESS_ID) = '')
                """;
            command.Parameters.AddWithValue("@routingId", mapping.RoutingId);
            command.Parameters.AddWithValue("@stepNo", mapping.StepNo);
            command.Parameters.AddWithValue("@processId", mapping.ProcessId);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Keeps the stable QMS demo inspection IDs traceable on both fresh and existing development databases.
    /// Incoming inspection owns an inventory material lot; process and shipping inspections own production lots.
    /// </summary>
    static void EnsureDevQmsSampleLotReferences(string connectionString)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("o");

        using (var incoming = connection.CreateCommand())
        {
            incoming.Transaction = transaction;
            // INSERT OR IGNORE still fires BEFORE INSERT triggers in SQLite.  The
            // IVT material-lot replacement guard must therefore be bypassed by
            // selecting no row when the stable demo key already exists.
            incoming.CommandText =
                "INSERT INTO IVT_MATERIAL_LOT " +
                "(LOT_ID,MATERIAL_ID,LOT_NO,WAREHOUSE,CURRENT_QTY,UNIT,STATUS,RECEIVED_AT,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                "SELECT 'LOT_IN_001','ITEM03','LOT_IN_001','RAW',100,'KG','InStock',@at,'SYSTEM',@at,'SYSTEM',@at " +
                "WHERE NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_LOT WHERE LOT_ID = 'LOT_IN_001')";
            incoming.Parameters.AddWithValue("@at", now);
            incoming.ExecuteNonQuery();
        }

        foreach (var lot in new[]
                 {
                     (LotId: "LOT_PR_001", ProductId: "ITEM02"),
                     (LotId: "LOT_SH_001", ProductId: "ITEM01"),
                 })
        {
            using var production = connection.CreateCommand();
            production.Transaction = transaction;
            production.CommandText =
                "INSERT OR IGNORE INTO POM_LOT " +
                "(LOT_ID,PLANT_ID,PRODUCT_ID,QTY,DEFECT_QTY,LOT_STATE,PROCESS_STATE,ROUTE_STEPS,CURRENT_STEP,IS_HOLD,CREATED_BY,CREATED_AT) " +
                "VALUES (@lot,'PLANT01',@product,100,0,'Created','Idle','PROC01',0,'N','SYSTEM',@at)";
            production.Parameters.AddWithValue("@lot", lot.LotId);
            production.Parameters.AddWithValue("@product", lot.ProductId);
            production.Parameters.AddWithValue("@at", now);
            production.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Designer에서 관리하는 MES·Mobile·POP 작업 실행 화면을 개발 SQLite DB에 보강한다.
    /// 누락 화면은 공통 작업실행 템플릿으로 추가하고, 빈 정의 또는 수정되지 않은 이전 관리형 revision만 업그레이드한다.
    /// 기존 제목·진입 대상·감사 정보와 Designer에서 수정한 정의는 갱신하거나 교체하지 않는다.
    /// </summary>
    /// <param name="connectionString">개발 SQLite 데이터베이스 연결 문자열이다.</param>
    static void SeedDevOperatorScreensIfMissing(string connectionString)
    {
        var seeds = new[]
        {
            (UiId: "POM_MES_WORK_EXECUTION", Title: "MES 작업 실행", Channel: "MES", EntryPath: "/meta/POM_MES_WORK_EXECUTION"),
            (UiId: "POM_MOBILE_WORK_EXECUTION", Title: "모바일 작업 실행", Channel: "MOBILE", EntryPath: "/Mobile/POM_MOBILE_WORK_EXECUTION"),
            (UiId: "POM_POP_WORK_EXECUTION", Title: "POP 작업 실행", Channel: "POP", EntryPath: "/POP/POM_POP_WORK_EXECUTION"),
        };

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("o");

        foreach (var seed in seeds)
        {
            (string Title, string DefinitionJson)? existingDefinition = null;
            using (var selectDefinition = connection.CreateCommand())
            {
                selectDefinition.Transaction = transaction;
                selectDefinition.CommandText =
                    "SELECT TITLE, DEFINITION_JSON FROM SYS_SCREEN_DEFINITION WHERE UI_ID = @uiId";
                selectDefinition.Parameters.AddWithValue("@uiId", seed.UiId);
                using var reader = selectDefinition.ExecuteReader();
                if (reader.Read())
                    existingDefinition = (reader.GetString(0), reader.GetString(1));
            }

            if (existingDefinition is null)
            {
                // 화면별로 확인하므로 Mobile/POP만 있는 기존 DB에도 빠진 MES 화면만 추가된다.
                using var insertDefinition = connection.CreateCommand();
                insertDefinition.Transaction = transaction;
                insertDefinition.CommandText =
                    "INSERT INTO SYS_SCREEN_DEFINITION " +
                    "(UI_ID, TITLE, DEFINITION_JSON, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT) " +
                    "VALUES (@uiId, @title, @definitionJson, 'SYSTEM', @now, 'SYSTEM', @now)";
                insertDefinition.Parameters.AddWithValue("@uiId", seed.UiId);
                insertDefinition.Parameters.AddWithValue("@title", seed.Title);
                insertDefinition.Parameters.AddWithValue(
                    "@definitionJson",
                    PomWorkExecutionScreenTemplate.Serialize(seed.UiId, seed.Title));
                insertDefinition.Parameters.AddWithValue("@now", now);
                insertDefinition.ExecuteNonQuery();
            }
            else if (PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                         seed.UiId, existingDefinition.Value.DefinitionJson))
            {
                var replaceHistoricalPlaceholderTitle =
                    string.Equals(existingDefinition.Value.Title, seed.UiId, StringComparison.OrdinalIgnoreCase)
                    && PomWorkExecutionScreenTemplate.IsHistoricalRevision1PlaceholderDefinition(
                        seed.UiId, existingDefinition.Value.DefinitionJson);
                var upgradedTitle = replaceHistoricalPlaceholderTitle
                    ? seed.Title
                    : existingDefinition.Value.Title;

                // JSON만 비교 후 교체한다. Designer 제목과 CREATED/UPDATED 감사 정보는 의도적으로 건드리지 않는다.
                using var upgradeDefinition = connection.CreateCommand();
                upgradeDefinition.Transaction = transaction;
                upgradeDefinition.CommandText =
                    "UPDATE SYS_SCREEN_DEFINITION SET TITLE = @title, DEFINITION_JSON = @definitionJson " +
                    "WHERE UI_ID = @uiId AND TITLE = @previousTitle AND DEFINITION_JSON = @previousDefinitionJson";
                upgradeDefinition.Parameters.AddWithValue("@title", upgradedTitle);
                upgradeDefinition.Parameters.AddWithValue("@definitionJson",
                    PomWorkExecutionScreenTemplate.Serialize(seed.UiId, upgradedTitle));
                upgradeDefinition.Parameters.AddWithValue("@uiId", seed.UiId);
                upgradeDefinition.Parameters.AddWithValue("@previousTitle", existingDefinition.Value.Title);
                upgradeDefinition.Parameters.AddWithValue("@previousDefinitionJson", existingDefinition.Value.DefinitionJson);
                upgradeDefinition.ExecuteNonQuery();
            }

            // FK 대상 정의를 먼저 보장한 뒤 target을 추가하며, 기존 사용자 경로와 감사 정보는 그대로 둔다.
            using var targetExists = connection.CreateCommand();
            targetExists.Transaction = transaction;
            targetExists.CommandText = "SELECT COUNT(*) FROM SYS_SCREEN_TARGET WHERE UI_ID = @uiId";
            targetExists.Parameters.AddWithValue("@uiId", seed.UiId);
            if (Convert.ToInt64(targetExists.ExecuteScalar() ?? 0L) > 0) continue;

            using var insertTarget = connection.CreateCommand();
            insertTarget.Transaction = transaction;
            insertTarget.CommandText =
                "INSERT INTO SYS_SCREEN_TARGET " +
                "(UI_ID, TARGET_CHANNEL, ENTRY_PATH, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT) " +
                "VALUES (@uiId, @channel, @entryPath, 'SYSTEM', @now, 'SYSTEM', @now)";
            insertTarget.Parameters.AddWithValue("@uiId", seed.UiId);
            insertTarget.Parameters.AddWithValue("@channel", seed.Channel);
            insertTarget.Parameters.AddWithValue("@entryPath", seed.EntryPath);
            insertTarget.Parameters.AddWithValue("@now", now);
            insertTarget.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // 개발 SQLite 전용 — SYS_MENU가 비어 있을 때만 SmartUX(:9020) 실제 데스크톱 메뉴 트리를 시드한다(idempotent).
    // 임베드된 nexaone-menu.json(SUX 카테고리 331행, 4단계 계층) + 동작하는 데모/관리 화면 폴더를 덧붙인다. 운영(MSSQL)은
    // 본 경로를 타지 않는다(상위 if가 Database:Provider==Sqlite && Development일 때만 호출). 직접 Dapper-free
    // Microsoft.Data.Sqlite 인서트 — 게이트웨이 DI/감사 컨텍스트 없이 부트스트랩 시점에 안전하게 채운다.
    /// <summary>메뉴가 비어 있을 때 임베디드 SmartUX 트리 또는 최소 폴백 메뉴를 시드한다.</summary>
    static void SeedDevMenuIfEmpty(string connectionString)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        conn.Open();

        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM SYS_MENU";
            if (Convert.ToInt64(count.ExecuteScalar() ?? 0L) > 0) return; // 이미 시드됨/사용자 데이터 존재 → 건너뜀
        }

        // 임베드된 SmartUX 메뉴 트리 우선. 리소스 부재 시 최소 폴백(셸이 빈 사이드바가 되지 않게).
        var rows = LoadSmartUxMenuSeed() ?? MinimalFallbackMenu();

        using var tx = conn.BeginTransaction();
        foreach (var r in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO SYS_MENU (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, UI_ID, PROGRAM_ID, VALID_STATE) " +
                "VALUES (@id, @name, @parent, @seq, @type, @uiId, @legacy, 'Valid')";
            cmd.Parameters.AddWithValue("@id", r.MenuId);
            cmd.Parameters.AddWithValue("@name", r.MenuName);
            cmd.Parameters.AddWithValue("@parent", (object?)r.ParentMenuId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@seq", r.DisplaySequence);
            cmd.Parameters.AddWithValue("@type", r.MenuType);
            cmd.Parameters.AddWithValue("@uiId", (object?)r.UiId ?? "");
            cmd.Parameters.AddWithValue("@legacy", (object?)r.LegacyId ?? "");   // 원본 SmartUX ID(정정 항목만, V081)
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Console.WriteLine($"[NexaOne.Server] SYS_MENU seeded ({rows.Count} rows: SmartUX tree + dev-demo).");
    }

    /// <summary>
    /// 기존 개발 SQLite는 증분 스키마 경로에서 데이터 보정 마이그레이션을 재실행하지 않으므로,
    /// 레거시 기본값과 정확히 일치하는 메뉴·화면·영문 리소스만 표준 MES 용어로 바꾼다.
    /// 관리 화면이나 Designer에서 사용자가 고친 이름은 일치 조건을 통과하지 않아 그대로 보존된다.
    /// </summary>
    internal static void NormalizeDevMenuTerminology(string connectionString)
    {
        var terms = new[]
        {
            (Id: "FACTORY_PPM", Legacy: "생산 계획(PPM)", Standard: "생산계획", LegacyEn: "PPM", English: "Production Planning"),
            (Id: "FACTORY_WPM", Legacy: "생산 실행(WPM)", Standard: "생산실행", LegacyEn: "WPM", English: "Production Execution"),
            (Id: "FACTORY_DLV", Legacy: "출하 관리", Standard: "출하관리", LegacyEn: "Shipping Management", English: "Shipping Management"),
            (Id: "FACTORY_PRC", Legacy: "구매 관리", Standard: "구매관리", LegacyEn: "Purchasing Management", English: "Purchasing Management"),
            (Id: "FACTORY_QCA", Legacy: "품질 검사(QCA)", Standard: "품질검사", LegacyEn: "QCA", English: "Quality Inspection"),
            (Id: "FACTORY_EMS", Legacy: "설비 보전(EMS)", Standard: "설비보전", LegacyEn: "EMS", English: "Equipment Maintenance"),
            (Id: "EES_EPT", Legacy: "설비 지표(EPT)", Standard: "설비지표", LegacyEn: "EPT", English: "Equipment Metrics"),
            (Id: "EES_FDC", Legacy: "설비 데이터 수집(FDC)", Standard: "설비데이터 수집", LegacyEn: "FDC", English: "Equipment Data Collection"),
            (Id: "QMS", Legacy: "품질 관리(QMS)", Standard: "품질관리", LegacyEn: "QMS", English: "Quality Management"),
            (Id: "FACTORY_STD_SINGLE", Legacy: "레시피 기준 정보", Standard: "레시피 기준정보", LegacyEn: "Recipe Master Data", English: "Recipe Master Data"),
            (Id: "FACTORY_MDM", Legacy: "기준 정보(MDM)", Standard: "기준정보", LegacyEn: "MDM", English: "Master Data"),
            (Id: "FACTORY_COM", Legacy: "공통 관리(COM)", Standard: "공통관리", LegacyEn: "COM", English: "Common Administration"),
            (Id: "MI_SYSTEM_2_0", Legacy: "시스템 관리", Standard: "시스템관리", LegacyEn: "System Management", English: "System Administration"),
            (Id: "FACTORY_SLS_SALES_ORDER", Legacy: "수주관리", Standard: "수주 관리", LegacyEn: "Sales Order Management", English: "Sales Order Management"),
            (Id: "FACTORY_SLS_SALES_REQUEST", Legacy: "판매 주문 접수", Standard: "판매 요청", LegacyEn: "Sales Order Receipt", English: "Sales Request"),
        };

        var screenResources = new[]
        {
            (Key: "screen.FACTORY_SLS_REPORT_DELIVERY.title", Legacy: "Delivery Status", Standard: "Shipping Status"),
        };

        var screenDefinitions = new[]
        {
            (Id: "FACTORY_SLS_SALES_ORDER", LegacyTitle: "판매 오더 관리", StandardTitle: "수주 관리"),
            (Id: "FACTORY_SLS_REPORT_DELIVERY", LegacyTitle: "납품 현황", StandardTitle: "출하 현황"),
        };

        var screenLabels = new[]
        {
            (Id: "FACTORY_SLS_SALES_ORDER", LegacyLabel: "판매오더 번호", StandardLabel: "수주 번호"),
            (Id: "FACTORY_SLS_SALES_ORDER", LegacyLabel: "판매오더명", StandardLabel: "수주명"),
            (Id: "FACTORY_SLS_SALES_ORDER", LegacyLabel: "판매오더 ID", StandardLabel: "수주 번호"),
            (Id: "FACTORY_SLS_SALES_REQUEST", LegacyLabel: "판매오더", StandardLabel: "수주 번호"),
        };

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var term in terms)
        {
            using (var menu = connection.CreateCommand())
            {
                menu.Transaction = transaction;
                menu.CommandText =
                    "UPDATE SYS_MENU SET MENU_NAME = @standard WHERE MENU_ID = @id AND MENU_NAME = @legacy";
                menu.Parameters.AddWithValue("@standard", term.Standard);
                menu.Parameters.AddWithValue("@id", term.Id);
                menu.Parameters.AddWithValue("@legacy", term.Legacy);
                menu.ExecuteNonQuery();
            }

            if (term.LegacyEn == term.English) continue;
            using var resource = connection.CreateCommand();
            resource.Transaction = transaction;
            resource.CommandText =
                "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE = @english " +
                "WHERE RESOURCE_KEY = @key AND LANGUAGE = 'EnUs' AND VALUE = @legacyEnglish";
            resource.Parameters.AddWithValue("@english", term.English);
            resource.Parameters.AddWithValue("@key", $"menu.{term.Id}");
            resource.Parameters.AddWithValue("@legacyEnglish", term.LegacyEn);
            resource.ExecuteNonQuery();
        }

        foreach (var term in screenResources)
        {
            using var resource = connection.CreateCommand();
            resource.Transaction = transaction;
            resource.CommandText =
                "UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET VALUE = @standard " +
                "WHERE RESOURCE_KEY = @key AND LANGUAGE = 'EnUs' AND VALUE = @legacy";
            resource.Parameters.AddWithValue("@standard", term.Standard);
            resource.Parameters.AddWithValue("@key", term.Key);
            resource.Parameters.AddWithValue("@legacy", term.Legacy);
            resource.ExecuteNonQuery();
        }

        foreach (var term in screenDefinitions)
        {
            // 코드 시드를 Designer로 가져온 JSON은 System.Text.Json의 이스케이프 문자열이고,
            // 수동 저장 JSON은 한글 원문일 수 있어 두 표현을 모두 정확한 제목 값 단위로 치환한다.
            using var definition = connection.CreateCommand();
            definition.Transaction = transaction;
            definition.CommandText =
                "UPDATE SYS_SCREEN_DEFINITION SET TITLE = @standard, " +
                "DEFINITION_JSON = REPLACE(REPLACE(DEFINITION_JSON, @literalLegacy, @literalStandard), @escapedLegacy, @escapedStandard) " +
                "WHERE UI_ID = @id AND TITLE = @legacy";
            definition.Parameters.AddWithValue("@standard", term.StandardTitle);
            definition.Parameters.AddWithValue("@literalLegacy", $"\"{term.LegacyTitle}\"");
            definition.Parameters.AddWithValue("@literalStandard", $"\"{term.StandardTitle}\"");
            definition.Parameters.AddWithValue("@escapedLegacy", System.Text.Json.JsonSerializer.Serialize(term.LegacyTitle));
            definition.Parameters.AddWithValue("@escapedStandard", System.Text.Json.JsonSerializer.Serialize(term.StandardTitle));
            definition.Parameters.AddWithValue("@id", term.Id);
            definition.Parameters.AddWithValue("@legacy", term.LegacyTitle);
            definition.ExecuteNonQuery();
        }

        foreach (var term in screenLabels)
        {
            // UI_ID 범위 안에서 정확히 일치하는 JSON 문자열 값만 바꿔 사용자 지정 라벨은 보존한다.
            using var definition = connection.CreateCommand();
            definition.Transaction = transaction;
            definition.CommandText =
                "UPDATE SYS_SCREEN_DEFINITION SET " +
                "DEFINITION_JSON = REPLACE(REPLACE(DEFINITION_JSON, @literalLegacy, @literalStandard), @escapedLegacy, @escapedStandard) " +
                "WHERE UI_ID = @id";
            definition.Parameters.AddWithValue("@literalLegacy", $"\"{term.LegacyLabel}\"");
            definition.Parameters.AddWithValue("@literalStandard", $"\"{term.StandardLabel}\"");
            definition.Parameters.AddWithValue("@escapedLegacy", System.Text.Json.JsonSerializer.Serialize(term.LegacyLabel));
            definition.Parameters.AddWithValue("@escapedStandard", System.Text.Json.JsonSerializer.Serialize(term.StandardLabel));
            definition.Parameters.AddWithValue("@id", term.Id);
            definition.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// 기존 개발 SQLite에도 공통 UI 영문 리소스 마이그레이션을 멱등 적용한다. SQLite 증분 스키마 보강은
    /// 데이터 마이그레이션을 재실행하지 않으므로, 배포 출력에 함께 복사되는 SQL 원본을 INSERT OR IGNORE로
    /// 실행해 사용자 번역을 덮어쓰지 않으면서 신규 키만 채운다.
    /// </summary>
    internal static void SeedDevCommonUiResourcesIfMissing(string connectionString)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var migrationName in new[]
                 {
                     "V099__SYS_I18N_COMMON_UI.sql",
                     "V108__SYS_I18N_NAV_RESIZE.sql",
                 })
        {
            var migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "db",
                "migrations",
                migrationName);
            if (!File.Exists(migrationPath))
                throw new FileNotFoundException(
                    "Common UI language migration was not copied to the server output.",
                    migrationPath);

            var sql = File.ReadAllText(migrationPath)
                .Replace(
                    "INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE",
                    "INSERT OR IGNORE INTO SYS_MULTI_LANGUAGE_RESOURCE",
                    StringComparison.Ordinal);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // 개발 SQLite 전용 — MDM_PLANT가 비어 있을 때만 점등된 MDM 업무화면(공장/품목/AREA)이 실제 행을 보이도록
    // 최소 마스터 데이터를 시드한다(idempotent). 감사 컬럼은 명시값으로 채운다(SQLite엔 GETUTCDATE 기본값 없음).
    /// <summary>기준정보가 비어 있을 때 화면과 업무 흐름 검증에 필요한 개발용 마스터 데이터를 시드한다.</summary>
    static void SeedDevMasterDataIfEmpty(string connectionString)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        conn.Open();

        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM MDM_PLANT";
            if (Convert.ToInt64(count.ExecuteScalar() ?? 0L) > 0) return; // 이미 데이터 존재 → 건너뜀
        }

        var now = DateTime.UtcNow.ToString("o");
        // 모든 개발 시드를 한 트랜잭션에 묶고 파라미터 바인딩을 일관되게 적용하는 로컬 실행기다.
        void Exec(System.Data.IDbTransaction tx, string sql, params (string, object)[] ps)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
            cmd.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        // 공장 → 구역(FK PLANT_ID) → 품목. 감사 4컬럼(@by/@at)은 모든 표에 공통.
        foreach (var p in new[] {
        ("PLANT01", "서울공장", "수도권 생산거점", "KR", "Asia/Seoul"),
        ("PLANT02", "부산공장", "영남 생산거점", "KR", "Asia/Seoul") })
            Exec(tx, "INSERT INTO MDM_PLANT (PLANT_ID,PLANT_NAME,DESCRIPTION,COUNTRY,TIME_ZONE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@desc,@country,@tz,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", p.Item1), ("@name", p.Item2), ("@desc", p.Item3), ("@country", p.Item4), ("@tz", p.Item5), ("@at", now));

        foreach (var a in new[] {
        ("AREA01", "조립1동", "조립 라인", "PLANT01"),
        ("AREA02", "포장동", "포장 라인", "PLANT01"),
        ("AREA03", "가공동", "가공 라인", "PLANT02") })
            Exec(tx, "INSERT INTO MDM_AREA (AREA_ID,AREA_NAME,DESCRIPTION,PLANT_ID,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@desc,@plant,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", a.Item1), ("@name", a.Item2), ("@desc", a.Item3), ("@plant", a.Item4), ("@at", now));

        foreach (var pr in new[] {
        ("ITEM01", "완제품 A", "출하용 완제품", "FG", "EA"),
        ("ITEM02", "반제품 B", "공정 중간품", "SF", "EA"),
        ("ITEM03", "원자재 C", "투입 원자재", "RM", "KG") })
            Exec(tx, "INSERT INTO MDM_PRODUCT (PRODUCT_ID,PRODUCT_NAME,DESCRIPTION,PRODUCT_TYPE,UNIT,VALID_STATE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@desc,@type,@unit,'Valid','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", pr.Item1), ("@name", pr.Item2), ("@desc", pr.Item3), ("@type", pr.Item4), ("@unit", pr.Item5), ("@at", now));

        // 설비(점등된 설비 관리 화면용). CREATED_BY/UPDATED_BY는 NOT NULL이며 기본값이 없어 명시 필수.
        foreach (var e in new[] {
        ("EQ01", "가공기 1호", "PLANT01", "AREA01", "CNC", "EQC_GENERAL"),
        ("EQ02", "검사기 1호", "PLANT01", "AREA02", "INSPECTION", "EQC_GENERAL"),
        ("EQ03", "조립기 1호", "PLANT02", "AREA03", "ASSEMBLY", "EQC_GENERAL") })
            Exec(tx, "INSERT INTO MDM_EQUIPMENT (EQUIPMENT_ID,EQUIPMENT_NAME,PLANT_ID,AREA_ID,EQUIPMENT_TYPE,EQUIPMENT_CLASS_ID,VALID_STATE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@plant,@area,@type,@cls,'Valid','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", e.Item1), ("@name", e.Item2), ("@plant", e.Item3), ("@area", e.Item4), ("@type", e.Item5), ("@cls", e.Item6), ("@at", now));

        // 코드 클래스 → 코드(사유 코드 그룹/사유 코드 화면용). 코드는 FK(CODE_CLASS_ID)로 클래스 선삽입 필요.
        foreach (var c in new[] {
        ("CC_DEFECT", "결함 사유", "결함 발생 사유 코드"),
        ("CC_DOWNTIME", "비가동 사유", "설비 비가동 사유 코드") })
            Exec(tx, "INSERT INTO MDM_CODE_CLASS (CODE_CLASS_ID,CODE_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));

        foreach (var c in new[] {
        ("RC_SCRATCH", "CC_DEFECT", "흠집", 1),
        ("RC_CRACK", "CC_DEFECT", "균열", 2),
        ("RC_PLAN", "CC_DOWNTIME", "계획 정지", 1),
        ("RC_FAULT", "CC_DOWNTIME", "고장 정지", 2) })
            Exec(tx, "INSERT INTO MDM_CODE (CODE_ID,CODE_CLASS_ID,CODE_NAME,SORT_ORDER,VALID_STATE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@cls,@name,@sort,'Valid','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@cls", c.Item2), ("@name", c.Item3), ("@sort", c.Item4), ("@at", now));

        // QMS 검사 규격(검사 SPEC 관리 화면용). NOMINAL/TOLERANCE는 nullable(계량형만 값).
        const string specSql = "INSERT INTO QMS_INSPECTION_SPEC (SPEC_ID,SPEC_NAME,PROCESS_ID,ITEM_NAME,MEASURE_TYPE,NOMINAL_VALUE,TOLERANCE_PLUS,TOLERANCE_MINUS,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                               "VALUES (@id,@name,@proc,@item,@mt,@nom,@tp,@tm,1,'SYSTEM',@at,'SYSTEM',@at)";
        Exec(tx, specSql, ("@id", "SPEC01"), ("@name", "외관 검사"), ("@proc", "PROC_ASSY"), ("@item", "완제품 A"), ("@mt", "Attribute"),
            ("@nom", DBNull.Value), ("@tp", DBNull.Value), ("@tm", DBNull.Value), ("@at", now));
        Exec(tx, specSql, ("@id", "SPEC02"), ("@name", "치수 검사"), ("@proc", "PROC_MACH"), ("@item", "반제품 B"), ("@mt", "Variable"),
            ("@nom", 10.0m), ("@tp", 0.5m), ("@tm", 0.5m), ("@at", now));

        // QMS SPC 파라미터(SPC 관리도 화면용). EQUIPMENT_ID는 위에서 시드한 설비 참조. USL/LSL은 nullable.
        const string spcSql = "INSERT INTO QMS_SPC_PARAM (PARAM_ID,PARAM_NAME,EQUIPMENT_ID,PROCESS_ID,MEAN,UCL,LCL,USL,LSL,SAMPLE_SIZE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                              "VALUES (@id,@name,@eq,@proc,@mean,@ucl,@lcl,@usl,@lsl,@n,1,'SYSTEM',@at,'SYSTEM',@at)";
        Exec(tx, spcSql, ("@id", "SP01"), ("@name", "치수 X"), ("@eq", "EQ01"), ("@proc", "PROC_MACH"),
            ("@mean", 10.0m), ("@ucl", 11.0m), ("@lcl", 9.0m), ("@usl", 11.5m), ("@lsl", 8.5m), ("@n", 5), ("@at", now));
        Exec(tx, spcSql, ("@id", "SP02"), ("@name", "가동 온도"), ("@eq", "EQ03"), ("@proc", "PROC_ASSY"),
            ("@mean", 200.0m), ("@ucl", 210.0m), ("@lcl", 190.0m), ("@usl", DBNull.Value), ("@lsl", DBNull.Value), ("@n", 5), ("@at", now));

        // ===== V035 신설 마스터 시드(점등 화면이 실제 행을 보이도록). FK 순서: 분류 → 본체 → 라우팅/BOM/Qtime. =====
        // 분류 마스터 5종(공통 형태: id/name/desc). 테이블·컬럼명만 달라 개별 루프.
        foreach (var c in new[] { ("EQC_GENERAL", "일반 설비", "범용 설비 그룹"), ("EQC_PRECISION", "정밀 설비", "정밀 가공 설비 그룹") })
            Exec(tx, "INSERT INTO MDM_EQUIPMENT_CLASS (EQUIPMENT_CLASS_ID,EQUIPMENT_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
        foreach (var c in new[] { ("IC_FG", "완제품", "Finished Goods"), ("IC_SF", "반제품", "Semi-Finished"), ("IC_RM", "원자재", "Raw Material") })
            Exec(tx, "INSERT INTO MDM_ITEM_CLASS (ITEM_CLASS_ID,ITEM_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
        foreach (var c in new[] { ("CRC_PLASTIC", "플라스틱 캐리어", "플라스틱 재질"), ("CRC_METAL", "금속 캐리어", "금속 재질") })
            Exec(tx, "INSERT INTO MDM_CARRIER_CLASS (CARRIER_CLASS_ID,CARRIER_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
        foreach (var c in new[] { ("SGC_ASSY", "조립공정", "조립 라인 공정군"), ("SGC_TEST", "검사공정", "검사/시험 공정군") })
            Exec(tx, "INSERT INTO MDM_SEGMENT_CLASS (SEGMENT_CLASS_ID,SEGMENT_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
        foreach (var c in new[] { ("PRC_AUTO", "자동화공정", "자동 설비 공정"), ("PRC_MANUAL", "수동공정", "작업자 수동 공정") })
            Exec(tx, "INSERT INTO MDM_PROCESS_CLASS (PROCESS_CLASS_ID,PROCESS_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));

        // 본체(그룹 참조).
        foreach (var c in new[] { ("CR01", "PC 캐리어", "CRC_PLASTIC", "PC 트레이"), ("CR02", "금속 트레이", "CRC_METAL", "스테인리스 트레이") })
            Exec(tx, "INSERT INTO MDM_CARRIER (CARRIER_ID,CARRIER_NAME,CARRIER_CLASS_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@cls,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@cls", c.Item3), ("@desc", c.Item4), ("@at", now));
        foreach (var c in new[] { ("SEG01", "SMT 조립", "SGC_ASSY", "표면실장 조립"), ("SEG02", "기능 검사", "SGC_TEST", "기능 시험") })
            Exec(tx, "INSERT INTO MDM_SEGMENT (SEGMENT_ID,SEGMENT_NAME,SEGMENT_CLASS_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@cls,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@cls", c.Item3), ("@desc", c.Item4), ("@at", now));
        foreach (var c in new[] { ("PROC01", "자동 투입", "PRC_AUTO", "자동 자재 투입"), ("PROC02", "수동 검사", "PRC_MANUAL", "작업자 육안 검사") })
            Exec(tx, "INSERT INTO MDM_PROCESS (PROCESS_ID,PROCESS_NAME,PROCESS_CLASS_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@cls,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@cls", c.Item3), ("@desc", c.Item4), ("@at", now));

        // 라우팅/BOM(제품 ITEM01~03 참조 — 위에서 시드).
        foreach (var c in new[] { ("RT01", "완제품 A 라우팅", "ITEM01", "표준 라우팅"), ("RT02", "반제품 B 라우팅", "ITEM02", "중간 라우팅") })
            Exec(tx, "INSERT INTO MDM_ROUTING (ROUTING_ID,ROUTING_NAME,PRODUCT_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@prod,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@prod", c.Item3), ("@desc", c.Item4), ("@at", now));
        const string bomSql = "INSERT INTO MDM_BOM (BOM_ID,PRODUCT_ID,COMPONENT_ID,QUANTITY,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@prod,@comp,@qty,@desc,'SYSTEM',@at,'SYSTEM',@at)";
        Exec(tx, bomSql, ("@id", "BOM01"), ("@prod", "ITEM01"), ("@comp", "ITEM03"), ("@qty", 10.0m), ("@desc", "완제품 A ← 원자재 C"), ("@at", now));
        Exec(tx, bomSql, ("@id", "BOM02"), ("@prod", "ITEM01"), ("@comp", "ITEM02"), ("@qty", 2.0m), ("@desc", "완제품 A ← 반제품 B"), ("@at", now));

        // Qtime/Qtime 액션(공정 SEG01/02 참조).
        const string qtSql = "INSERT INTO MDM_QTIME (QTIME_ID,SEGMENT_ID,STANDARD_TIME,UNIT,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@seg,@t,@unit,@desc,'SYSTEM',@at,'SYSTEM',@at)";
        Exec(tx, qtSql, ("@id", "QT01"), ("@seg", "SEG01"), ("@t", 30.0m), ("@unit", "분"), ("@desc", "SMT 조립 표준시간"), ("@at", now));
        Exec(tx, qtSql, ("@id", "QT02"), ("@seg", "SEG02"), ("@t", 60.0m), ("@unit", "분"), ("@desc", "기능 검사 표준시간"), ("@at", now));
        foreach (var c in new[] { ("QA01", "QT01", "ACT_HOLD", "표준시간 초과 보류"), ("QA02", "QT01", "ACT_RELEASE", "검토 후 해제") })
            Exec(tx, "INSERT INTO MDM_QTIME_ACTION (ACTION_ID,QTIME_ID,ACTION_CODE,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@qt,@code,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@qt", c.Item2), ("@code", c.Item3), ("@desc", c.Item4), ("@at", now));

        // ===== V037~V044 신설 QMS 마스터/트랜잭션 시드(점등 화면이 실제 행을 보이도록). 감사·IS_ACTIVE·STATUS는 DDL DEFAULT로 채움. =====
        // 검사항목/검사정의/수입검사방법(기준정보 V037)
        foreach (var c in new[] { ("INSP_VISUAL", "외관 검사", "Incoming", "Attribute"), ("INSP_DIM", "치수 검사", "Process", "Variable"), ("INSP_FUNC", "기능 검사", "Shipping", "Attribute") })
            Exec(tx, "INSERT INTO QMS_INSPECTION_ITEM (ITEM_ID,ITEM_NAME,INSPECTION_TYPE,MEASURE_TYPE,UNIT) VALUES (@id,@name,@t,@mt,'EA')",
                ("@id", c.Item1), ("@name", c.Item2), ("@t", c.Item3), ("@mt", c.Item4));
        foreach (var c in new[] { ("IDEF_IN", "수입검사 정의", "Incoming"), ("IDEF_PR", "공정검사 정의", "Process") })
            Exec(tx, "INSERT INTO QMS_INSPECTION_DEF (INSP_DEF_ID,INSP_DEF_NAME,PROCESS_ID,PRODUCT_ID,INSPECTION_TYPE) VALUES (@id,@name,'PROC01','ITEM03',@t)",
                ("@id", c.Item1), ("@name", c.Item2), ("@t", c.Item3));
        foreach (var c in new[] { ("IM_AQL10", "AQL 1.0 정상검사", "AQL", "1.0"), ("IM_FULL", "전수검사", "Full", "-") })
            Exec(tx, "INSERT INTO QMS_INCOMING_INSP_METHOD (METHOD_ID,METHOD_NAME,PRODUCT_ID,SAMPLING_TYPE,AQL_LEVEL) VALUES (@id,@name,'ITEM03',@st,@aql)",
                ("@id", c.Item1), ("@name", c.Item2), ("@st", c.Item3), ("@aql", c.Item4));

        // 검사 실행(수입/공정/출하)
        foreach (var c in new[] { ("INS_IN1", "Incoming", "LOT_IN_001", "ITEM03", "EQ02", "Pass", 0), ("INS_PR1", "Process", "LOT_PR_001", "ITEM02", "EQ01", "Pass", 0), ("INS_SH1", "Shipping", "LOT_SH_001", "ITEM01", "EQ02", "Fail", 2) })
            Exec(tx, "INSERT INTO QMS_INSPECTION (INSPECTION_ID,INSPECTION_TYPE,LOT_ID,PRODUCT_ID,EQUIPMENT_ID,SPEC_ID,INSPECTED_AT,INSPECTOR_ID,RESULT,SAMPLE_QTY,DEFECT_QTY,IS_CONFIRMED) " +
                     "VALUES (@id,@t,@lot,@prod,@eq,'SPEC01',@at,'admin',@r,10,@d,1)",
                ("@id", c.Item1), ("@t", c.Item2), ("@lot", c.Item3), ("@prod", c.Item4), ("@eq", c.Item5), ("@at", now), ("@r", c.Item6), ("@d", c.Item7));

        // 장기재고검사(자재/제품)
        foreach (var c in new[] { ("LT_MAT1", "Material", "ITEM03", "Completed"), ("LT_PRD1", "Product", "ITEM01", "Requested") })
            Exec(tx, "INSERT INTO QMS_LONGTERM_INSPECTION (LT_INSP_ID,TARGET_TYPE,PRODUCT_ID,LOT_ID,WAREHOUSE,REQUEST_DATE,REQUESTED_BY,STATUS) " +
                     "VALUES (@id,@t,@prod,'LOT_LT_01','창고A',@at,'admin',@st)",
                ("@id", c.Item1), ("@t", c.Item2), ("@prod", c.Item3), ("@at", now), ("@st", c.Item4));

        // 클레임
        foreach (var c in new[] { ("CLM001", "CL-2026-001", "현대전자", "ITEM01", "Quality", "Critical", "Received"), ("CLM002", "CL-2026-002", "삼성SDI", "ITEM02", "Delivery", "Major", "Completed") })
            Exec(tx, "INSERT INTO QMS_CLAIM (CLAIM_ID,CLAIM_NO,CUSTOMER_NAME,PRODUCT_ID,CLAIM_TYPE,OCCURRED_DATE,SEVERITY,STATUS,ASSIGNEE_ID) " +
                     "VALUES (@id,@no,@cust,@prod,@ct,@at,@sv,@st,'admin')",
                ("@id", c.Item1), ("@no", c.Item2), ("@cust", c.Item3), ("@prod", c.Item4), ("@ct", c.Item5), ("@at", now), ("@sv", c.Item6), ("@st", c.Item7));

        // NCR
        foreach (var c in new[] { ("NCR001", "NCR-2026-001", "Process", "LOT_PR_001", "Open"), ("NCR002", "NCR-2026-002", "Incoming", "LOT_IN_001", "Closed") })
            Exec(tx, "INSERT INTO QMS_NCR (NCR_ID,NCR_NO,SOURCE_TYPE,LOT_ID,PRODUCT_ID,ISSUED_DATE,ISSUED_BY,DISPOSITION,STATUS) " +
                     "VALUES (@id,@no,@src,@lot,'ITEM02',@at,'admin','Rework',@st)",
                ("@id", c.Item1), ("@no", c.Item2), ("@src", c.Item3), ("@lot", c.Item4), ("@at", now), ("@st", c.Item5));

        // Hold/Release · 4M 변경
        Exec(tx, "INSERT INTO QMS_HOLD_RELEASE (HOLD_ID,LOT_ID,PRODUCT_ID,HOLD_TYPE,RISK_RANGE,REASON,REQUESTED_BY,REQUESTED_AT,STATUS) " +
                 "VALUES ('HOLD001','LOT_SH_001','ITEM01','Hold','High','출하검사 부적합 보류','admin',@at,'Hold')", ("@at", now));
        Exec(tx, "INSERT INTO QMS_4M_CHANGE (CHANGE_ID,CHANGE_NO,CHANGE_TYPE,EQUIPMENT_ID,PRODUCT_ID,CHANGE_DATE,DESCRIPTION,REQUESTED_BY,APPROVAL_STATUS) " +
                 "VALUES ('4M001','4M-2026-001','Machine','EQ01','ITEM01',@at,'가공기 1호 공구 교체','admin','Approved')", ("@at", now));

        // 계측기 + 검교정/RNR/수리
        foreach (var c in new[] { ("GA01", "버니어캘리퍼스", "측정", "CD-15CP"), ("GA02", "마이크로미터", "측정", "MDC-25MX") })
            Exec(tx, "INSERT INTO QMS_GAUGE (GAUGE_ID,GAUGE_NAME,GAUGE_TYPE,MODEL,SERIAL_NO,LOCATION,EQUIPMENT_ID,CALIBRATION_CYCLE_DAYS,NEXT_CALIBRATION_AT) " +
                     "VALUES (@id,@name,@t,@model,@id,'검사실','EQ02',365,@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@t", c.Item3), ("@model", c.Item4), ("@at", now));
        Exec(tx, "INSERT INTO QMS_GAUGE_CALIBRATION_PLAN (PLAN_ID,GAUGE_ID,PLAN_NAME,SCHEDULED_DATE,CYCLE_TYPE,ASSIGNEE_ID,STATUS) VALUES ('CP01','GA01','연간 검교정',@at,'Annual','admin','Planned')", ("@at", now));
        Exec(tx, "INSERT INTO QMS_GAUGE_CALIBRATION_RESULT (RESULT_ID,GAUGE_ID,PLAN_ID,CALIBRATED_AT,CALIBRATED_BY,RESULT,CERTIFICATE_NO) VALUES ('CR01','GA01','CP01',@at,'한국인정','Pass','CERT-2026-001')", ("@at", now));
        Exec(tx, "INSERT INTO QMS_GAUGE_RNR_PLAN (RNR_PLAN_ID,GAUGE_ID,PLAN_NAME,SCHEDULED_DATE,OPERATOR_COUNT,TRIAL_COUNT,PART_COUNT,STATUS) VALUES ('RP01','GA01','버니어 R&R',@at,3,2,10,'Planned')", ("@at", now));
        Exec(tx, "INSERT INTO QMS_GAUGE_RNR_RESULT (RNR_RESULT_ID,RNR_PLAN_ID,GAUGE_ID,EVALUATED_AT,EVALUATED_BY,GAGE_RR_PERCENT,NDC,JUDGEMENT) VALUES ('RR01','RP01','GA01',@at,'admin',8.5,12,'Accept')", ("@at", now));
        Exec(tx, "INSERT INTO QMS_GAUGE_REPAIR_RESULT (REPAIR_ID,GAUGE_ID,REPAIRED_AT,REPAIRED_BY,FAILURE_DESC,REPAIR_DESC,COST) VALUES ('RE01','GA02',@at,'외주','영점 불량','영점 조정',50000)", ("@at", now));

        // 협력사 평가(항목→정의→연결→실적→시정조치)
        foreach (var c in new[] { ("SI_Q", "품질", "Quality", 40), ("SI_D", "납기", "Delivery", 30), ("SI_P", "가격", "Price", 30) })
            Exec(tx, "INSERT INTO QMS_SPM_EVAL_ITEM (ITEM_ID,ITEM_NAME,CATEGORY,MAX_SCORE) VALUES (@id,@name,@cat,@max)",
                ("@id", c.Item1), ("@name", c.Item2), ("@cat", c.Item3), ("@max", c.Item4));
        Exec(tx, "INSERT INTO QMS_SPM_EVAL_DEF (DEF_ID,DEF_NAME,EVAL_CYCLE,TARGET_TYPE) VALUES ('SD_ANN','연간 정기평가','Annual','Supplier')");
        foreach (var c in new[] { ("SP_Q", "SI_Q", 40, 1), ("SP_D", "SI_D", 30, 2), ("SP_P", "SI_P", 30, 3) })
            Exec(tx, "INSERT INTO QMS_SPM_EVAL_PARAM (PARAM_ID,DEF_ID,ITEM_ID,WEIGHT,SORT_ORDER) VALUES (@id,'SD_ANN',@item,@w,@o)",
                ("@id", c.Item1), ("@item", c.Item2), ("@w", c.Item3), ("@o", c.Item4));
        foreach (var c in new[] { ("SR01", "SUP_A", "대한정밀", "A", 92.5m), ("SR02", "SUP_B", "한일소재", "B", 78.0m) })
            Exec(tx, "INSERT INTO QMS_SPM_EVAL_RESULT (RESULT_ID,SUPPLIER_ID,SUPPLIER_NAME,DEF_ID,EVAL_PERIOD,TOTAL_SCORE,GRADE,EVALUATED_AT,EVALUATOR_ID) " +
                     "VALUES (@id,@sid,@sname,'SD_ANN','2026',@score,@grade,@at,'admin')",
                ("@id", c.Item1), ("@sid", c.Item2), ("@sname", c.Item3), ("@grade", c.Item4), ("@score", c.Item5), ("@at", now));
        Exec(tx, "INSERT INTO QMS_SPM_ACTION_RESULT (ACTION_ID,RESULT_ID,SUPPLIER_ID,ACTION_DESC,ACTION_DATE,STATUS) VALUES ('AR01','SR02','SUP_B','납기 개선 시정조치',@at,'Open')", ("@at", now));

        // ===== EMS(설비보전) 시드 — 예비품(V027)/그룹·입출고(V045)/작업지시(V008)/보전계획(V027). V008/V027 감사 컬럼은 DEFAULT가 없어 명시 필수. =====
        const string emsPartSql = "INSERT INTO EMS_SPARE_PART (PART_ID,PART_NAME,PART_NUMBER,DESCRIPTION,UNIT_OF_MEASURE,CURRENT_STOCK,MIN_STOCK,MAX_STOCK,LOCATION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@no,@desc,@uom,@cur,@min,@max,@loc,'SYSTEM',@at,'SYSTEM',@at)";
        Exec(tx, emsPartSql, ("@id", "ESP01"), ("@name", "베어링 6204"), ("@no", "BRG-6204"), ("@desc", "회전부 베어링"), ("@uom", "EA"), ("@cur", 50), ("@min", 10), ("@max", 100), ("@loc", "자재창고 A"), ("@at", now));
        Exec(tx, emsPartSql, ("@id", "ESP02"), ("@name", "모터 1.5kW"), ("@no", "MTR-15"), ("@desc", "구동 모터"), ("@uom", "EA"), ("@cur", 8), ("@min", 5), ("@max", 20), ("@loc", "자재창고 B"), ("@at", now));
        Exec(tx, emsPartSql, ("@id", "ESP03"), ("@name", "근접센서"), ("@no", "SNS-PRX"), ("@desc", "감지 센서"), ("@uom", "EA"), ("@cur", 30), ("@min", 10), ("@max", 60), ("@loc", "자재창고 A"), ("@at", now));
        foreach (var c in new[] { ("ESPC_BRG", "베어링류", "회전부 베어링"), ("ESPC_MTR", "모터류", "구동 모터") })
            Exec(tx, "INSERT INTO EMS_SPARE_PART_CLASS (PART_CLASS_ID,PART_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
        foreach (var c in new[] { ("EIO01", "ESP01", "Incoming", 20, "입고처", "자재창고 A"), ("EIO02", "ESP02", "Move", 2, "자재창고 B", "조립1동"), ("EIO03", "ESP03", "Scrap", 5, "자재창고 A", "폐기장") })
            Exec(tx, "INSERT INTO EMS_SPARE_PART_INOUT (INOUT_ID,PART_ID,TRANSACTION_TYPE,QUANTITY,FROM_LOCATION,TO_LOCATION,TRANSACTION_AT,PROCESSED_BY,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@pid,@t,@q,@from,@to,@at,'admin','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@pid", c.Item2), ("@t", c.Item3), ("@q", c.Item4), ("@from", c.Item5), ("@to", c.Item6), ("@at", now));
        foreach (var c in new[] { ("EWO01", "EQ01", "BM", "가공기 1호 베어링 교체"), ("EWO02", "EQ02", "PM", "검사기 1호 정기점검") })
            Exec(tx, "INSERT INTO EMS_WORK_ORDER (WO_ID,EQUIPMENT_ID,WO_TYPE,DESCRIPTION,ASSIGNEE_ID,ISSUED_AT,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@eq,@t,@desc,'admin',@at,'Issued','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@eq", c.Item2), ("@t", c.Item3), ("@desc", c.Item4), ("@at", now));
        foreach (var c in new[] { ("EMP01", "월간 정기점검", "EQ01", "PM", "Monthly"), ("EMP02", "분기 정밀점검", "EQ03", "PM", "Quarterly") })
            Exec(tx, "INSERT INTO EMS_MAINTENANCE_PLAN (PLAN_ID,PLAN_NAME,EQUIPMENT_ID,PLAN_TYPE,CYCLE_TYPE,SCHEDULED_DATE,ESTIMATED_DURATION_HOURS,ASSIGNEE_ID,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@eq,@pt,@ct,@at,2.0,'admin','Planned','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", c.Item1), ("@name", c.Item2), ("@eq", c.Item3), ("@pt", c.Item4), ("@ct", c.Item5), ("@at", now));

        // ===== EST OEE(설비종합효율) 시드(V050) — 점등된 OEE/유실/지표 화면이 실제 값을 보이도록. EQ01~03 참조(FK).
        // 비율(가용성/성능/품질/OEE)은 분율(0~1)로 저장하고 값은 사전집계 예시다(원자료→마트 집계는 배치/워커 소관). =====
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("o");
        const string oeeSql = "INSERT INTO EST_OEE_SUMMARY (OEE_ID,PLANT_ID,EQUIPMENT_ID,OEE_DATE,SHIFT_ID,PLANNED_MINUTES,DOWNTIME_MINUTES,OPERATING_MINUTES,IDEAL_CYCLE_TIME_SEC,TOTAL_COUNT,GOOD_COUNT,DEFECT_COUNT,AVAILABILITY,PERFORMANCE,QUALITY,OEE) " +
            "VALUES (@id,@plant,@eq,@date,@shift,@pm,@dm,@om,@ict,@tc,@gc,@dc,@av,@pf,@ql,@oee)";
        Exec(tx, oeeSql, ("@id", "OEE01"), ("@plant", "PLANT01"), ("@eq", "EQ01"), ("@date", now), ("@shift", "SHIFT_D"), ("@pm", 480m), ("@dm", 60m), ("@om", 420m), ("@ict", 30m), ("@tc", 800m), ("@gc", 780m), ("@dc", 20m), ("@av", 0.8750m), ("@pf", 0.9520m), ("@ql", 0.9750m), ("@oee", 0.8120m));
        Exec(tx, oeeSql, ("@id", "OEE02"), ("@plant", "PLANT01"), ("@eq", "EQ01"), ("@date", yesterday), ("@shift", "SHIFT_N"), ("@pm", 480m), ("@dm", 90m), ("@om", 390m), ("@ict", 30m), ("@tc", 760m), ("@gc", 740m), ("@dc", 20m), ("@av", 0.8125m), ("@pf", 0.9740m), ("@ql", 0.9737m), ("@oee", 0.7706m));
        Exec(tx, oeeSql, ("@id", "OEE03"), ("@plant", "PLANT01"), ("@eq", "EQ02"), ("@date", now), ("@shift", "SHIFT_D"), ("@pm", 480m), ("@dm", 120m), ("@om", 360m), ("@ict", 40m), ("@tc", 500m), ("@gc", 470m), ("@dc", 30m), ("@av", 0.7500m), ("@pf", 0.9259m), ("@ql", 0.9400m), ("@oee", 0.6528m));
        Exec(tx, oeeSql, ("@id", "OEE04"), ("@plant", "PLANT02"), ("@eq", "EQ03"), ("@date", now), ("@shift", "SHIFT_D"), ("@pm", 480m), ("@dm", 30m), ("@om", 450m), ("@ict", 25m), ("@tc", 1000m), ("@gc", 990m), ("@dc", 10m), ("@av", 0.9375m), ("@pf", 0.9259m), ("@ql", 0.9900m), ("@oee", 0.8594m));

        // 유실 상세(6대 손실) — WORST5 유실: EQ02(115분) > EQ01(65) > EQ03(30). LOSS_CODE는 느슨 참조(FK 없음).
        const string lossSql = "INSERT INTO EST_OEE_LOSS (LOSS_ID,PLANT_ID,EQUIPMENT_ID,OEE_DATE,SHIFT_ID,LOSS_CATEGORY,LOSS_CODE,LOSS_NAME,LOSS_MINUTES,OCCURRED_AT,REASON) " +
            "VALUES (@id,@plant,@eq,@date,'SHIFT_D',@cat,@code,@name,@min,@at,@reason)";
        foreach (var l in new[] {
        ("LOSS01", "PLANT01", "EQ01", "Breakdown", "RC_FAULT",  "고장 정지", 45m, "베어링 파손"),
        ("LOSS02", "PLANT01", "EQ01", "Setup",     "RC_PLAN",   "계획 정지", 20m, "금형 교체"),
        ("LOSS03", "PLANT01", "EQ02", "Breakdown", "RC_FAULT",  "고장 정지", 90m, "모터 과열"),
        ("LOSS04", "PLANT01", "EQ02", "MinorStop", "RC_MINOR",  "순간 정지", 15m, "자재 걸림"),
        ("LOSS05", "PLANT01", "EQ02", "Defect",    "RC_SCRATCH","불량 손실", 10m, "흠집 다발"),
        ("LOSS06", "PLANT02", "EQ03", "Setup",     "RC_PLAN",   "계획 정지", 25m, "셋업 조정"),
        ("LOSS07", "PLANT02", "EQ03", "SpeedLoss", "RC_SPEED",  "속도 저하",  5m, "저속 운전") })
            Exec(tx, lossSql, ("@id", l.Item1), ("@plant", l.Item2), ("@eq", l.Item3), ("@date", now), ("@cat", l.Item4), ("@code", l.Item5), ("@name", l.Item6), ("@min", l.Item7), ("@at", now), ("@reason", l.Item8));

        // EPT 관심지표 마스터 + 값(지표 관리/관심지표 등록·조회 화면).
        foreach (var i in new[] {
        ("IDX_MTBF", "평균고장간격(MTBF)", "신뢰성", "시간", "고장 간 평균 가동시간"),
        ("IDX_MTTR", "평균수리시간(MTTR)", "보전성", "시간", "고장 1건당 평균 수리시간"),
        ("IDX_UPTIME", "설비 가동률", "가동", "%", "계획 대비 가동시간 비율") })
            Exec(tx, "INSERT INTO EST_EPT_INDEX (INDEX_ID,INDEX_NAME,INDEX_CATEGORY,UNIT,DESCRIPTION,IS_ACTIVE) VALUES (@id,@name,@cat,@unit,@desc,1)",
                ("@id", i.Item1), ("@name", i.Item2), ("@cat", i.Item3), ("@unit", i.Item4), ("@desc", i.Item5));
        const string ivSql = "INSERT INTO EST_EPT_INDEX_VALUE (VALUE_ID,INDEX_ID,EQUIPMENT_ID,PLANT_ID,OEE_DATE,SHIFT_ID,INDEX_VALUE) VALUES (@id,@idx,@eq,@plant,@date,'SHIFT_D',@val)";
        foreach (var v in new[] {
        ("IV01", "IDX_MTBF", "EQ01", "PLANT01", 120.5m),
        ("IV02", "IDX_MTTR", "EQ01", "PLANT01", 2.5m),
        ("IV03", "IDX_UPTIME", "EQ03", "PLANT02", 93.75m) })
            Exec(tx, ivSql, ("@id", v.Item1), ("@idx", v.Item2), ("@eq", v.Item3), ("@plant", v.Item4), ("@date", now), ("@val", v.Item5));

        // ===== OEE 집계 워커 설정(V051) — 상태 분류 + 설비 목표. 워커가 켜지면 원자료를 이 설정과 결합해 마트를 계산한다. =====
        // 작업조(MDM_SHIFT, V046) — OEE 작업조 단위 윈도/계획시간 근거. DAY 08:00~20:00, NIGHT 20:00~08:00(야간 교대).
        foreach (var sh in new[] { ("DAY", "주간조", "08:00", "20:00"), ("NIGHT", "야간조", "20:00", "08:00") })
            Exec(tx, "INSERT INTO MDM_SHIFT (SHIFT_ID,SHIFT_NAME,START_TIME,END_TIME,DESCRIPTION,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@start,@end,@name,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", sh.Item1), ("@name", sh.Item2), ("@start", sh.Item3), ("@end", sh.Item4), ("@at", now));
        // 상태 분류: RUN=가동, DOWN/SETUP/MINOR=비가동(계획 포함), IDLE=비계획(계획시간 제외).
        foreach (var s in new[] {
        ("RUN", "가동", "Productive", 1, 0, 1),
        ("DOWN", "고장 정지", "Breakdown", 0, 1, 1),
        ("SETUP", "셋업/교체", "Setup", 0, 1, 1),
        ("MINOR", "순간 정지", "MinorStop", 0, 1, 1),
        ("IDLE", "비계획 대기", "Idle", 0, 0, 0) })
            Exec(tx, "INSERT INTO EST_STATE_CATEGORY (STATE_ID,STATE_NAME,CATEGORY,IS_PRODUCTIVE,IS_DOWNTIME,IS_SCHEDULED,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@cat,@prod,@down,@sched,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", s.Item1), ("@name", s.Item2), ("@cat", s.Item3), ("@prod", s.Item4), ("@down", s.Item5), ("@sched", s.Item6), ("@at", now));
        foreach (var t in new[] {
        ("EQ01", 30m, 480m, "가공기 1호 목표(30초/개)"),
        ("EQ02", 40m, 480m, "검사기 1호 목표(40초/개)"),
        ("EQ03", 25m, 480m, "조립기 1호 목표(25초/개)") })
            Exec(tx, "INSERT INTO EST_OEE_TARGET (EQUIPMENT_ID,IDEAL_CYCLE_TIME_SEC,PLANNED_MINUTES,DESCRIPTION,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@eq,@ict,@pm,@desc,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@eq", t.Item1), ("@ict", t.Item2), ("@pm", t.Item3), ("@desc", t.Item4), ("@at", now));

        // POM_LOT(WPM 작업진행/LOT추적/수율 화면용) — 홀드·불량 섞어 시드. ROUTE_STEPS/CREATED_BY NOT NULL.
        foreach (var l in new[] {
        ("LOT01", "PLANT01", "ITEM01", 100m, 5m, "Processing", "N"),
        ("LOT02", "PLANT01", "ITEM01", 200m, 0m, "Completed", "N"),
        ("LOT03", "PLANT02", "ITEM02", 150m, 12m, "Processing", "Y") })
            Exec(tx, "INSERT INTO POM_LOT (LOT_ID,PLANT_ID,PRODUCT_ID,QTY,DEFECT_QTY,LOT_STATE,PROCESS_STATE,ROUTE_STEPS,CURRENT_STEP,IS_HOLD,CREATED_BY,CREATED_AT) " +
                     "VALUES (@id,@plant,@prod,@qty,@def,@st,'Idle','투입>가공>검사',1,@hold,'SYSTEM',@at)",
                ("@id", l.Item1), ("@plant", l.Item2), ("@prod", l.Item3), ("@qty", l.Item4), ("@def", l.Item5), ("@st", l.Item6), ("@hold", l.Item7), ("@at", now));
        // POM_LOT_HISTORY(LOT 추적 화면용) — LOT_HISTORY_ID는 IDENTITY(자동).
        foreach (var h in new[] { ("PLANT01", "LOT01", "EQ01", "TrackIn"), ("PLANT01", "LOT02", "EQ02", "TrackOut") })
            Exec(tx, "INSERT INTO POM_LOT_HISTORY (PLANT_ID,LOT_ID,EQUIPMENT_ID,PROCESS_ID,TRACK_IN_TIME,EXECUTION_ID,EXECUTION_USER,QTY,DEFECT_QTY,LOT_STATE,PROCESS_STATE) " +
                     "VALUES (@plant,@lot,@eq,'PROC_MACH',@at,@exec,'admin',100,0,'Processing','Run')",
                ("@plant", h.Item1), ("@lot", h.Item2), ("@eq", h.Item3), ("@exec", h.Item4), ("@at", now));

        // PRC_PURCHASE_ORDER(구매오더 관리/현황 화면용, V052) — 발주 헤더 시드.
        foreach (var po in new[] {
        ("PO01", "PLANT01", "원자재 발주", "VEN_A", 500m, "Ordered"),
        ("PO02", "PLANT01", "부자재 발주", "VEN_B", 300m, "Draft"),
        ("PO03", "PLANT02", "소모품 발주", "VEN_A", 120m, "Incoming") })
            Exec(tx, "INSERT INTO PRC_PURCHASE_ORDER (PURCHASE_ORDER_ID,PLANT_ID,PURCHASE_ORDER_NAME,VENDOR_ID,ORDER_DATE,ORDER_QTY,OWNER_ID,STATUS,IS_HOLD,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@plant,@name,@vendor,@at,@qty,'admin',@st,'N','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", po.Item1), ("@plant", po.Item2), ("@name", po.Item3), ("@vendor", po.Item4), ("@qty", po.Item5), ("@st", po.Item6), ("@at", now));

        // SLS_SALES_ORDER/REQUEST(수주/판매 요청 화면용, V053) — 헤더+요청 시드.
        foreach (var so in new[] {
        ("SO01", "PLANT01", "완제품 A 판매", "CUST_X", 1000m, "Confirmed"),
        ("SO02", "PLANT01", "완제품 A 추가", "CUST_Y", 500m, "Draft") })
            Exec(tx, "INSERT INTO SLS_SALES_ORDER (SALES_ORDER_ID,PLANT_ID,SALES_ORDER_NAME,CUSTOMER_ID,PRODUCT_ID,PLAN_START_DATE,PLAN_QTY,DELIVERED_QTY,OWNER_ID,STATUS,IS_HOLD,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@plant,@name,@cust,'ITEM01',@at,@qty,0,'admin',@st,'N','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", so.Item1), ("@plant", so.Item2), ("@name", so.Item3), ("@cust", so.Item4), ("@qty", so.Item5), ("@st", so.Item6), ("@at", now));
        foreach (var sr in new[] { ("SR01", "SO01", 400m, "Confirmed"), ("SR02", "SO01", 600m, "Draft") })
            Exec(tx, "INSERT INTO SLS_SALES_REQUEST (SALES_REQUEST_ID,SALES_REQUEST_NAME,SALES_ORDER_ID,CUSTOMER_ID,PRODUCT_ID,REQUEST_DATE,REQUEST_QTY,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,'판매 요청',@so,'CUST_X','ITEM01',@at,@qty,@st,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", sr.Item1), ("@so", sr.Item2), ("@qty", sr.Item3), ("@st", sr.Item4), ("@at", now));

        // MDM_ITEM_PLANNING(MRP v1 데모, V079) — SO01(Confirmed, ITEM01×1000) + BOM(ITEM01→ITEM02/03)과 정합:
        // ITEM01=생산(로트 100·리드 2일), ITEM02=구매(안전재고 50·리드 5일), ITEM03=구매(로트 500·리드 7일).
        foreach (var ip in new[] {
        ("ITEM01", 0m, 2, 100m, "Make", "완제품 A — 생산 계획"),
        ("ITEM02", 50m, 5, 1m, "Buy", "반제품 B — 구매 조달"),
        ("ITEM03", 0m, 7, 500m, "Buy", "원자재 C — 구매 조달") })
            Exec(tx, "INSERT INTO MDM_ITEM_PLANNING (ITEM_ID,SAFETY_STOCK,LEAD_TIME_DAYS,LOT_SIZE,MAKE_OR_BUY,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@ss,@lt,@lot,@mb,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", ip.Item1), ("@ss", ip.Item2), ("@lt", ip.Item3), ("@lot", ip.Item4), ("@mb", ip.Item5), ("@desc", ip.Item6), ("@at", now));

        // CRP v1(V087) — 워크센터·라우팅 스텝(RT01/RT02는 위 라우팅 시드). WO 제안 500 기준 부하 실측 정합:
        // WC01 = 500×2.0 = 1000분(480분/일 → 2.08일), WC02 = 500×1.0 = 500분(960분/일 → 0.52일).
        foreach (var wc in new[] { ("WC01", "조립 셀", 480m, "1교대"), ("WC02", "가공 라인", 960m, "2교대") })
            Exec(tx, "INSERT INTO MDM_WORK_CENTER (WORK_CENTER_ID,WORK_CENTER_NAME,PLANT_ID,DAILY_CAPACITY_MIN,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,'PLANT01',@cap,@desc,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", wc.Item1), ("@name", wc.Item2), ("@cap", wc.Item3), ("@desc", wc.Item4), ("@at", now));
        foreach (var st in new[]
                 {
                     ("RT01", 10, "조립", "PROC01", "WC01", 2.0m),
                     ("RT01", 20, "검사/가공", "PROC02", "WC02", 1.0m),
                     ("RT02", 10, "가공", "PROC01", "WC02", 0.5m)
                 })
            Exec(tx, "INSERT INTO MDM_ROUTING_STEP (ROUTING_ID,STEP_NO,STEP_NAME,PROCESS_ID,WORK_CENTER_ID,STD_TIME_MIN,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@rt,@no,@name,@process,@wc,@t,'SYSTEM',@at,'SYSTEM',@at)",
                ("@rt", st.Item1), ("@no", st.Item2), ("@name", st.Item3), ("@process", st.Item4),
                ("@wc", st.Item5), ("@t", st.Item6), ("@at", now));

        // MDM_LABEL*(FACTORY_STD 라벨 마스터/발행/매핑 화면용, V054). FK: 발행/매핑 → 라벨(선삽입).
        foreach (var lb in new[] { ("LBL01", "제품 라벨"), ("LBL02", "박스 라벨") })
            Exec(tx, "INSERT INTO MDM_LABEL (LABEL_ID,PLANT_ID,LABEL_NAME,DESCRIPTION,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,'PLANT01',@name,@name,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", lb.Item1), ("@name", lb.Item2), ("@at", now));
        foreach (var iss in new[] { ("LIS01", "LBL01", "LOT01", "SN0001", 2), ("LIS02", "LBL01", "LOT02", "SN0002", 1) })
            Exec(tx, "INSERT INTO MDM_LABEL_ISSUE (ISSUE_ID,PLANT_ID,LABEL_ID,ITEM_ID,LOT_ID,SERIAL_NUM,PRINT_CNT,ISSUED_AT,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,'PLANT01',@label,'ITEM01',@lot,@sn,@cnt,@at,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", iss.Item1), ("@label", iss.Item2), ("@lot", iss.Item3), ("@sn", iss.Item4), ("@cnt", iss.Item5), ("@at", now));
        foreach (var mp in new[] { ("LMP01", "LBL01", "PROC_MACH"), ("LMP02", "LBL02", "PROC_ASSY") })
            Exec(tx, "INSERT INTO MDM_LABEL_MAPPING (MAPPING_ID,PLANT_ID,PROCESS_ID,ITEM_ID,LABEL_ID,PRINT_LIMIT_CNT,PRINT_LIMIT_YN,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,'PLANT01',@proc,'ITEM01',@label,5,'Y','SYSTEM',@at,'SYSTEM',@at)",
                ("@id", mp.Item1), ("@label", mp.Item2), ("@proc", mp.Item3), ("@at", now));

        // EST_EPT_LAYOUT/EQUIPMENT_PROPERTY(EPT_STD 레이아웃/속성 화면용, V055). 속성 FK: EQ01~03(선삽입됨).
        foreach (var lo in new[] { ("LAYOUT01", "PLANT01", "조립1동 레이아웃", "AREA01"), ("LAYOUT02", "PLANT02", "가공동 레이아웃", "AREA03") })
            Exec(tx, "INSERT INTO EST_EPT_LAYOUT (LAYOUT_ID,PLANT_ID,LAYOUT_NAME,AREA_ID,WIDTH,HEIGHT,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@plant,@name,@area,1024,768,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", lo.Item1), ("@plant", lo.Item2), ("@name", lo.Item3), ("@area", lo.Item4), ("@at", now));
        foreach (var pr in new[] { ("EQ01", "PLANT01", 30m), ("EQ02", "PLANT01", 40m), ("EQ03", "PLANT02", 25m) })
            Exec(tx, "INSERT INTO EST_EPT_EQUIPMENT_PROPERTY (EQUIPMENT_ID,PLANT_ID,DESCRIPTION,CYCLE_TIME,DO_ALARM_INTERLOCK,DO_MCC,DO_SUMMARY,DO_TACT_TIME,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@eq,@plant,'설비 EPT 속성',@ct,'Y','Y','Y','Y',1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@eq", pr.Item1), ("@plant", pr.Item2), ("@ct", pr.Item3), ("@at", now));

        // MICUBE→EST(설비상태 표준) 시드 — 상태매트릭스(V025) + 이벤트/알람상태/이벤트상태 매핑(V056).
        foreach (var m in new[] { ("PLANT01", "IDLE", "RUN"), ("PLANT01", "RUN", "DOWN"), ("PLANT02", "RUN", "IDLE") })
            Exec(tx, "INSERT INTO EST_STATE_MATRIX (PLANT_ID,FROM_STATE_ID,TO_STATE_ID,ALLOW_FLAG,SET_STATE_ID,REQUIRE_REASON,VALID_STATE) VALUES (@p,@from,@to,'Y',@to,'N','Valid')",
                ("@p", m.Item1), ("@from", m.Item2), ("@to", m.Item3));
        foreach (var ev in new[] { ("EV01", "도어 열림", "EQ01", "Safety"), ("EV02", "비상정지", "EQ02", "Safety") })
            Exec(tx, "INSERT INTO EST_EQUIPMENT_EVENT (EVENT_ID,PLANT_ID,EVENT_NAME,EQUIPMENT_ID,EVENT_TYPE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,'PLANT01',@name,@eq,@type,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", ev.Item1), ("@name", ev.Item2), ("@eq", ev.Item3), ("@type", ev.Item4), ("@at", now));
        Exec(tx, "INSERT INTO EST_STATE_ALARM_MAP (MAP_ID,PLANT_ID,EQUIPMENT_ID,ALARM_DEF_ID,SET_STATE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES ('SAM01','PLANT01','EQ01','ALM_OVERHEAT','DOWN',1,'SYSTEM',@at,'SYSTEM',@at)", ("@at", now));
        Exec(tx, "INSERT INTO EST_STATE_EVENT_MAP (MAP_ID,PLANT_ID,EQUIPMENT_ID,EVENT_ID,SET_STATE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES ('SEM01','PLANT01','EQ01','EV01','IDLE',1,'SYSTEM',@at,'SYSTEM',@at)", ("@at", now));

        // MICUBE→COM(알람메일 알림) 시드 — 메일서버/수신자(일반·알람)/서비스(V057).
        Exec(tx, "INSERT INTO COM_MAIL_SERVER (SERVER_ID,SERVER_NAME,HOST,PORT,SENDER_ADDRESS,USE_SSL,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES ('SMTP01','기본 SMTP','smtp.factory.local',587,'noreply@factory.local','Y',1,'SYSTEM',@at,'SYSTEM',@at)", ("@at", now));
        foreach (var rc in new[] { ("RC01", "admin", "EQ01", "Alarm"), ("RC02", "admin", "EQ02", "Mail") })
            Exec(tx, "INSERT INTO COM_MAIL_RECIPIENT (RECIPIENT_ID,PLANT_ID,USER_ID,EQUIPMENT_ID,MAIL_ADDRESS,MAIL_TYPE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,'PLANT01',@user,@eq,'admin@factory.local',@type,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", rc.Item1), ("@user", rc.Item2), ("@eq", rc.Item3), ("@type", rc.Item4), ("@at", now));
        foreach (var sv in new[] { ("SVC01", "알람 수집 서비스", "Collector", "Running"), ("SVC02", "메일 발송 서비스", "Mailer", "Stopped") })
            Exec(tx, "INSERT INTO COM_SERVICE (SERVICE_ID,SERVICE_NAME,SERVICE_TYPE,STATUS,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@type,@st,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", sv.Item1), ("@name", sv.Item2), ("@type", sv.Item3), ("@st", sv.Item4), ("@at", now));

        // MDM_BOR/RESOURCE(FACTORY_STD BOR 화면용, V058). 자원 FK: BOR 선삽입.
        foreach (var b in new[] { ("BOR01", "PLANT01", "조립 BOR", "Condition"), ("BOR02", "PLANT01", "가공 BOR", "Resource") })
            Exec(tx, "INSERT INTO MDM_BOR (BOR_ID,PLANT_ID,BOR_NAME,PROCESS_ID,PRODUCT_ID,BOR_TYPE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@plant,@name,'PROC_ASSY','ITEM01',@type,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", b.Item1), ("@plant", b.Item2), ("@name", b.Item3), ("@type", b.Item4), ("@at", now));
        foreach (var r in new[] { ("BRS01", "BOR01", "Equipment", "EQ01", "가공기 1호", 1m), ("BRS02", "BOR02", "Tool", "TOOL01", "지그 A", 2m) })
            Exec(tx, "INSERT INTO MDM_BOR_RESOURCE (RESOURCE_ID,BOR_ID,RESOURCE_TYPE,RESOURCE_REF_ID,RESOURCE_NAME,REQUIRED_QTY,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@bor,@type,@ref,@name,@qty,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", r.Item1), ("@bor", r.Item2), ("@type", r.Item3), ("@ref", r.Item4), ("@name", r.Item5), ("@qty", r.Item6), ("@at", now));
        // IVT_MATERIAL_TX 이동(이동오더 현황 화면용) — TX_TYPE='Move'.
        foreach (var m in new[] { ("MTX01", "LOT01", "ITEM03", 50m, "자재창고", "조립1동"), ("MTX02", "LOT02", "ITEM03", 30m, "자재창고", "가공동") })
            Exec(tx, "INSERT INTO IVT_MATERIAL_TX (TX_ID,LOT_ID,MATERIAL_ID,TX_TYPE,QTY,FROM_WAREHOUSE,TO_WAREHOUSE,TX_AT,PROCESSED_BY,STATUS) VALUES (@id,@lot,@mat,'Move',@qty,@from,@to,@at,'admin','Completed')",
                ("@id", m.Item1), ("@lot", m.Item2), ("@mat", m.Item3), ("@qty", m.Item4), ("@from", m.Item5), ("@to", m.Item6), ("@at", now));

        // MDM_VENDOR/ITEM(벤더 관리 화면용, V059) — FK: 품목 선삽입됨(ITEM03).
        foreach (var v in new[] { ("VEN_A", "대한자재", "Material"), ("VEN_B", "한빛부품", "Part") })
            Exec(tx, "INSERT INTO MDM_VENDOR (VENDOR_ID,VENDOR_NAME,VENDOR_TYPE,CORPORATION_NO,OWNER_NAME,PHONE,EMAIL,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@type,'123-45-67890','대표','02-000-0000','vendor@x.com',1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", v.Item1), ("@name", v.Item2), ("@type", v.Item3), ("@at", now));
        foreach (var vi in new[] { ("VI01", "VEN_A", "ITEM03", 7m, 100m, 1500m), ("VI02", "VEN_B", "ITEM02", 14m, 50m, 3200m) })
            Exec(tx, "INSERT INTO MDM_VENDOR_ITEM (VENDOR_ITEM_ID,VENDOR_ID,PRODUCT_ID,LEAD_TIME_DAYS,MOQ,BASE_PRICE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@ven,@prod,@lt,@moq,@price,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", vi.Item1), ("@ven", vi.Item2), ("@prod", vi.Item3), ("@lt", vi.Item4), ("@moq", vi.Item5), ("@price", vi.Item6), ("@at", now));

        // 생산계획→생산관리지시→공정 작업지시 계층은 master 트랜잭션 뒤의
        // EnsureDevPomOrderHierarchy에서 신규·기존 개발 DB 모두에 구성한다.

        // COM_ACTION/ALARM_ACTION(알람 액션 화면용, V061) — FK: 액션 선삽입.
        foreach (var ac in new[] { ("ACT_MAIL", "알람 메일 발송", "Email"), ("ACT_HOLD", "LOT 홀드", "Hold") })
            Exec(tx, "INSERT INTO COM_ACTION (ACTION_ID,ACTION_NAME,ACTION_TYPE,EMAIL_TITLE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@name,@type,'설비 알람 발생',1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", ac.Item1), ("@name", ac.Item2), ("@type", ac.Item3), ("@at", now));
        foreach (var aa in new[] { ("AA01", "ALM01", "ACT_MAIL", 1), ("AA02", "ALM01", "ACT_HOLD", 2) })
            Exec(tx, "INSERT INTO COM_ALARM_ACTION (ALARM_ACTION_ID,ALARM_ID,ACTION_ID,ACTION_SEQUENCE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                     "VALUES (@id,@alarm,@act,@seq,1,'SYSTEM',@at,'SYSTEM',@at)",
                ("@id", aa.Item1), ("@alarm", aa.Item2), ("@act", aa.Item3), ("@seq", aa.Item4), ("@at", now));

        // SYS_REQUEST_LOG(요청 로그 뷰어 화면용, V062) — 실기록은 RequestLogMiddleware(기본 OFF)가 담당, 데모 2행.
        foreach (var rl in new[] { ("RL01", "POST", "/api/v1/query/MDM.PlantList", 200, 12), ("RL02", "POST", "/api/v1/auth/login", 401, 35) })
            Exec(tx, "INSERT INTO SYS_REQUEST_LOG (LOG_ID,METHOD,PATH,STATUS_CODE,ELAPSED_MS,USER_ID,CLIENT_IP,REQUESTED_AT) " +
                     "VALUES (@id,@m,@p,@st,@ms,'admin','127.0.0.1',@at)",
                ("@id", rl.Item1), ("@m", rl.Item2), ("@p", rl.Item3), ("@st", rl.Item4), ("@ms", rl.Item5), ("@at", now));

        // SYS_APP_LOG(로그 뷰어 + 대시보드 시간대별 추이용, V064) — 실기록은 DbLoggerProvider(기본 OFF)가 담당.
        // 데모: 최근 ~11시간에 걸쳐 레벨/시간 분산(대시보드 추이 차트가 채워지고 일부 시간대는 2~3건).
        var logBase = DateTime.UtcNow;
        var demoLogs = new (string Lvl, string Cat, string Msg, int HoursAgo)[]
        {
        ("Information", "NexaOne.POM.Application", "작업지시 WO-2401 시작", 11),
        ("Information", "NexaOne.EST.Application", "설비 EQ02 가동 전환", 10),
        ("Warning",     "NexaOne.FDC.Application", "수집 파라미터 임계 접근: EQ01/TEMP 76.2", 9),
        ("Information", "NexaOne.SHP.Application", "출하오더 SO-8842 확정", 9),
        ("Warning",     "NexaOne.QMS.Application", "SPC 관리한계 근접: 공정 P-3", 7),
        ("Information", "NexaOne.POM.Application", "Lot L-5521 트랙인", 6),
        ("Error",       "NexaOne.Server.Gateway", "명명 쿼리 실행 실패: timeout (재시도 성공)", 6),
        ("Warning",     "NexaOne.FDC.Application", "수집 파라미터 임계 접근: EQ01/TEMP 78.5", 6),
        ("Information", "NexaOne.EMS.Application", "예방보전 WO 생성: EQ05", 3),
        ("Warning",     "NexaOne.EST.Application", "설비 EQ03 대기 지연", 2),
        ("Information", "NexaOne.POM.Application", "작업지시 WO-2402 완료", 1),
        ("Error",       "NexaOne.FDC.Application", "인터락 발생: EQ01 도어 개방", 0),
        };
        var alIdx = 0;
        foreach (var al in demoLogs)
            Exec(tx, "INSERT INTO SYS_APP_LOG (LOG_ID,LOG_LEVEL,CATEGORY,MESSAGE,LOGGED_AT) VALUES (@id,@lvl,@cat,@msg,@at)",
                ("@id", $"AL{++alIdx:D2}"), ("@lvl", al.Lvl), ("@cat", al.Cat), ("@msg", al.Msg),
                ("@at", logBase.AddHours(-al.HoursAgo).ToString("o")));

        tx.Commit();
        Console.WriteLine("[NexaOne.Server] MDM/QMS master data seeded (core + V035 ext: class/segment/process/routing/bom/qtime).");
    }

    // 개발 SQLite 전용 — 관리 생산오더와 공정 작업지시를 분리한 데모 계층을 증분 보장한다.
    // 기존 DB의 부모 없는 WO01/WO02도 실제 부모에 연결하며, 임의의 다른 작업지시는 자동 추측하지 않는다.
    /// <summary>
    /// 생산계획 → 생산관리지시 → 공정 작업지시 계층을 멱등 생성하고 기존 데모 작업지시의 부모만 보정한다.
    /// </summary>
    static void EnsureDevPomOrderHierarchy(string connectionString)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        conn.Open();
        var start = DateTime.UtcNow.ToString("o");
        var end = DateTime.UtcNow.AddDays(7).ToString("o");

        // 계획·생산관리지시·작업지시가 함께 커밋되도록 같은 연결과 트랜잭션을 공유한다.
        void Exec(System.Data.IDbTransaction tx, string sql, params (string, object)[] ps)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            cmd.CommandText = sql;
            foreach (var (key, value) in ps) cmd.Parameters.AddWithValue(key, value);
            cmd.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        Exec(tx, "INSERT OR IGNORE INTO POM_PRODUCTION_PLAN " +
                 "(PLAN_ID,PLAN_NAME,PLANT_ID,PRODUCT_ID,PLANNED_QTY,PLANNED_START_DATE,PLANNED_END_DATE,STATUS,REMARK,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES ('PPLAN01','완제품 A 생산계획','PLANT01','ITEM01',800,@start,@end,'InProgress','개발 데모 생산계획','SYSTEM',@start,'SYSTEM',@start)",
            ("@start", start), ("@end", end));
        Exec(tx, "INSERT OR IGNORE INTO POM_PRODUCTION_ORDER " +
                 "(ORDER_ID,PLAN_ID,EQUIPMENT_ID,PRODUCT_ID,ORDER_QTY,ACTUAL_QTY,SCHEDULED_START,SCHEDULED_END,ACTUAL_START,ACTUAL_END,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES ('PORD01','PPLAN01','EQ01','ITEM01',500,NULL,@start,@end,@start,NULL,'InProgress','SYSTEM',@start,'SYSTEM',@start)",
            ("@start", start), ("@end", end));
        Exec(tx, "INSERT OR IGNORE INTO POM_PRODUCTION_ORDER " +
                 "(ORDER_ID,PLAN_ID,EQUIPMENT_ID,PRODUCT_ID,ORDER_QTY,ACTUAL_QTY,SCHEDULED_START,SCHEDULED_END,ACTUAL_START,ACTUAL_END,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES ('PORD02','PPLAN01','EQ02','ITEM01',300,NULL,@start,@end,NULL,NULL,'Issued','SYSTEM',@start,'SYSTEM',@start)",
            ("@start", start), ("@end", end));

        Exec(tx, "INSERT OR IGNORE INTO POM_WORK_ORDER " +
                 "(WORK_ORDER_ID,PRODUCTION_ORDER_ID,PLANT_ID,WORK_ORDER_NAME,AREA_ID,EQUIPMENT_ID,WORK_ORDER_TYPE,PRODUCT_ID," +
                 "ROUTING_ID,ROUTING_SCOPE,ROUTING_STEP_NO,PROCESS_ID,WORK_CENTER_ID,PLAN_START_DATE,PLAN_END_DATE,PLAN_QTY,START_QTY," +
                 "COMPLETE_QTY,SCRAP_QTY,OWNER_ID,STATUS,IS_HOLD,STARTED_AT,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES ('WO01','PORD01','PLANT01','완제품 A 1차 작업','AREA01','EQ01','Normal','ITEM01'," +
                 "'RT01','Operation',10,'PROC01','WC01',@start,@end,500,300,0,0,'admin','Started','N',@start,'가공 공정 실행'," +
                 "'SYSTEM',@start,'SYSTEM',@start)",
            ("@start", start), ("@end", end));
        Exec(tx, "INSERT OR IGNORE INTO POM_WORK_ORDER " +
                 "(WORK_ORDER_ID,PRODUCTION_ORDER_ID,PLANT_ID,WORK_ORDER_NAME,AREA_ID,EQUIPMENT_ID,WORK_ORDER_TYPE,PRODUCT_ID," +
                 "ROUTING_ID,ROUTING_SCOPE,ROUTING_STEP_NO,PROCESS_ID,WORK_CENTER_ID,PLAN_START_DATE,PLAN_END_DATE,PLAN_QTY,START_QTY," +
                 "COMPLETE_QTY,SCRAP_QTY,OWNER_ID,STATUS,IS_HOLD,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES ('WO02','PORD02','PLANT01','완제품 A 2차 작업','AREA02','EQ02','Normal','ITEM01'," +
                 "'RT01','Operation',20,'PROC02','WC02',@start,@end,300,0,0,0,'admin','Created','N','검사 공정 실행'," +
                 "'SYSTEM',@start,'SYSTEM',@start)",
            ("@start", start), ("@end", end));

        Exec(tx, "UPDATE POM_WORK_ORDER SET PRODUCTION_ORDER_ID='PORD01' " +
                 "WHERE WORK_ORDER_ID='WO01' AND (PRODUCTION_ORDER_ID IS NULL OR TRIM(PRODUCTION_ORDER_ID)='')");
        Exec(tx, "UPDATE POM_WORK_ORDER SET PRODUCTION_ORDER_ID='PORD02' " +
                 "WHERE WORK_ORDER_ID='WO02' AND (PRODUCTION_ORDER_ID IS NULL OR TRIM(PRODUCTION_ORDER_ID)='')");
        tx.Commit();
    }

    // 개발 SQLite 전용 — 로그 보존 정리 배치 정의 시드(SYS_BATCH_PROCESS 비었을 때만, idempotent).
    // 규약: BATCH_RULE=명명 쓰기쿼리, BATCH_INPUTDATA=JSON 파라미터, Interval=초(86400=일 1회).
    /// <summary>배치 정의가 비어 있을 때 애플리케이션 및 요청 로그 보존 정리 작업을 시드한다.</summary>
    static void SeedDevBatchDefinitionsIfEmpty(string connectionString)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        conn.Open();

        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM SYS_BATCH_PROCESS";
            if (Convert.ToInt64(count.ExecuteScalar() ?? 0L) > 0) return;
        }

        var seeds = new (string Id, string Name, string Rule, string Input, string Desc)[]
        {
        ("PURGE-APP-LOG", "앱 로그 보존 정리(30일)", "SYS.PurgeOldAppLogs", "{\"retentionDays\":30}",
            "SYS_APP_LOG 30일 초과분 삭제 — V064 보존 정리"),
        ("PURGE-REQUEST-LOG", "요청 로그 보존 정리(14일)", "SYS.PurgeOldRequestLogs", "{\"retentionDays\":14}",
            "SYS_REQUEST_LOG 14일 초과분 삭제 — V062 보존 정리"),
        };
        using var tx = conn.BeginTransaction();
        foreach (var (id, name, rule, input, desc) in seeds)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO SYS_BATCH_PROCESS
            (BATCH_ID, BATCH_NAME, BATCH_TYPE, BATCH_RULE, BATCH_OPTIONS, BATCH_INPUTDATA, DESCRIPTION,
             VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @name, 'Interval', @rule, '86400', @input, @desc,
                    'Valid', 'SYSTEM', @now, 'SYSTEM', @now)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@rule", rule);
            cmd.Parameters.AddWithValue("@input", input);
            cmd.Parameters.AddWithValue("@desc", desc);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Console.WriteLine("[NexaOne.Server] SYS_BATCH_PROCESS seeded (log retention x2, worker OFF — run API로 즉시 실행 가능).");
    }

    // 임베드된 nexaone-menu.json(SUX 데스크톱 트리)를 로드하고, 실제 동작하는 데모/관리 화면을 별도 폴더로 덧붙여 반환한다.
    // 리소스가 없거나 비면 null을 반환(호출부가 최소 폴백 사용).
    /// <summary>임베디드 SmartUX 메뉴를 읽고 NexaOne 개발·관리 메뉴를 병합한다.</summary>
    static List<MenuSeedRow>? LoadSmartUxMenuSeed()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("nexaone-menu.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<MenuSeedRow>>(
            reader.ReadToEnd(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (rows is null || rows.Count == 0) return null;
        rows.AddRange(DevDemoMenu());
        return rows;
    }

    // 실제 백엔드가 있어 '동작하는' 데모/관리 화면 — SmartUX 트리와 명확히 구분된 별도 폴더(맨 끝 정렬)로 노출해
    // 메뉴 관리 등 핵심 화면이 사이드바에서 항상 접근 가능하게 한다. SmartUX 화면이 마이그될수록 본 폴더 의존은 줄어든다.
    /// <summary>현재 백엔드와 연결되어 즉시 사용할 수 있는 개발·관리 화면 메뉴를 반환한다.</summary>
    static IEnumerable<MenuSeedRow> DevDemoMenu() => new[]
    {
    new MenuSeedRow("NX_DEV",        "● NexaOne 데모/관리",   null,     9000, "Folder", ""),
    new MenuSeedRow("NX_DEV_DASH",   "대시보드(운영 요약)",    "NX_DEV", 5,    "Screen", "DASHBOARD_SUMMARY"),
    new MenuSeedRow("NX_DEV_MENU",   "메뉴 관리",             "NX_DEV", 10,   "Screen", "SYS_MENU_MGMT"),
    new MenuSeedRow("NX_DEV_USERREQ","사용자 신청 승인",       "NX_DEV", 15,   "Screen", "SYS_USER_REQUESTS"),
    new MenuSeedRow("NX_DEV_MRP",    "자재 소요 계획(MRP)",     "NX_DEV", 16,   "Screen", "NX_MRP_PLANNING"),
    new MenuSeedRow("NX_DEV_UOM",    "단위(UOM) 관리",          "NX_DEV", 17,   "Screen", "FACTORY_STD_UOM"),
    new MenuSeedRow("NX_DEV_ITEMPLN","품목 계획 파라미터",      "NX_DEV", 18,   "Screen", "FACTORY_STD_ITEM_PLANNING"),
    new MenuSeedRow("NX_DEV_MENUUSE","메뉴 사용 통계",          "NX_DEV", 19,   "Screen", "SYS_MENU_USAGE_STATS"),
    new MenuSeedRow("NX_DEV_CRP",    "능력 소요(CRP) 부하",     "NX_DEV", 20,   "Screen", "NX_CRP_LOAD"),
    new MenuSeedRow("NX_DEV_WC",     "워크센터 관리",           "NX_DEV", 21,   "Screen", "FACTORY_STD_WORK_CENTER"),
    new MenuSeedRow("NX_DEV_RTSTEP", "라우팅 스텝 관리",        "NX_DEV", 22,   "Screen", "FACTORY_STD_ROUTING_STEP"),
    new MenuSeedRow("NX_DEV_GRID",   "공장 관리(데모)",        "NX_DEV", 20,   "Screen", "DEMO_GRID"),
    new MenuSeedRow("NX_DEV_LAYOUT", "생산 현황(데모)",        "NX_DEV", 30,   "Screen", "DEMO_LAYOUT"),
    new MenuSeedRow("NX_DEV_PARAM",  "파라미터 입력(데모)",     "NX_DEV", 40,   "Screen", "DEMO_PARAM"),
    new MenuSeedRow("NX_DEV_DEFECT", "결함 분류(데모)",        "NX_DEV", 50,   "Screen", "DEMO_QMS_DEFECT_CLASS"),
    new MenuSeedRow("NX_DEV_PLANT",  "공장 폼(데모)",          "NX_DEV", 60,   "Screen", "DEMO_PLANT_FORM"),
};

    // 임베드 리소스가 없을 때의 최소 폴백 트리(셸이 절대 빈 사이드바가 되지 않게).
    /// <summary>임베디드 메뉴가 없을 때 빈 탐색 영역을 방지할 최소 메뉴 트리를 반환한다.</summary>
    static List<MenuSeedRow> MinimalFallbackMenu() => new()
{
    new("M_STD", "기준정보", null, 10, "Folder", ""),
    new("M_STD_PLANT", "공장 관리", "M_STD", 10, "Screen", "DEMO_GRID"),
    new("M_PRD", "생산관리", null, 20, "Folder", ""),
    new("M_PRD_STATUS", "생산 현황", "M_PRD", 10, "Screen", "DEMO_LAYOUT"),
    new("M_SYS", "시스템관리", null, 90, "Folder", ""),
    new("M_SYS_MENU", "메뉴 관리", "M_SYS", 10, "Screen", "SYS_MENU_MGMT"),
};

    // nexaone-menu.json 한 행(camelCase JSON ↔ PascalCase 매핑은 PropertyNameCaseInsensitive로 처리).
    // UiId는 Screen에만 채워진다(Folder는 공백 → 클릭=토글). ParentMenuId는 최상위에서 null.
    // LegacyId — 오타 정정(V081)으로 ID가 바뀐 항목의 원본 SmartUX ID(PROGRAM_ID로 시드, 이관 추적성).
    /// <summary>개발 메뉴 시드 한 행의 계층, 화면 연결 및 레거시 추적 정보를 표현한다.</summary>
    internal sealed record MenuSeedRow(
        string MenuId, string MenuName, string? ParentMenuId, int DisplaySequence, string MenuType, string UiId,
        string? LegacyId = null);
}
