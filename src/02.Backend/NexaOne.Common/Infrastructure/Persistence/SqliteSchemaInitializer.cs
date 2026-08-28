using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// db/migrations의 MSSQL DDL을 SQLite 방언으로 변환해 SQLite DB에 스키마를 생성한다.
/// (NVARCHAR→TEXT, DATETIME2→TEXT, BIT→INTEGER, GETUTCDATE()→CURRENT_TIMESTAMP, IDENTITY 제거 등)
/// 실 MSSQL 없이 로컬/테스트로 호스트를 띄우기 위한 경량 부트스트랩 — 운영 스키마와 1:1은 아니나 구조 동등.
/// 통합 테스트(SqliteSchemaBootstrapper)와 NexaOne.Server(SQLite 모드)가 공유하는 단일 구현이다.
/// </summary>
public static class SqliteSchemaInitializer
{
    private static readonly Regex MigrationFileNamePattern = new(
        @"^V(?<version>[0-9]{3})__(?<description>[A-Z0-9]+(?:_[A-Z0-9]+)*)\.sql$",
        RegexOptions.CultureInvariant);

    private const string TraceInboxWorkStateInsertTrigger = """
        CREATE TRIGGER TR_IVT_TRACE_INBOX_WORK_STATE_BI
        BEFORE INSERT ON IVT_TRACE_PROJECTION_INBOX
        WHEN NOT (
            (NEW.STATUS IN ('Pending', 'Error') AND NEW.IS_WORK_ITEM = 1)
            OR (NEW.STATUS IN ('Applied', 'Ignored') AND NEW.IS_WORK_ITEM = 0))
        BEGIN
            SELECT RAISE(ABORT, 'IVT TRACE inbox STATUS and IS_WORK_ITEM must agree');
        END;
        """;

    private const string TraceInboxWorkStateUpdateTrigger = """
        CREATE TRIGGER TR_IVT_TRACE_INBOX_WORK_STATE_BU
        BEFORE UPDATE OF STATUS, IS_WORK_ITEM ON IVT_TRACE_PROJECTION_INBOX
        WHEN NOT (
            (NEW.STATUS IN ('Pending', 'Error') AND NEW.IS_WORK_ITEM = 1)
            OR (NEW.STATUS IN ('Applied', 'Ignored') AND NEW.IS_WORK_ITEM = 0))
        BEGIN
            SELECT RAISE(ABORT, 'IVT TRACE inbox STATUS and IS_WORK_ITEM must agree');
        END;
        """;

    private static readonly string FdcEffectLifecycleInsertTrigger = $"""
        CREATE TRIGGER TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BI
        BEFORE INSERT ON FDC_INTERLOCK_HISTORY
        WHEN NOT (
        {BuildFdcLifecycleRowValidityPredicate("NEW.")}
        )
        BEGIN
            SELECT RAISE(ABORT, 'FDC interlock effect lifecycle state is invalid');
        END;
        """;

    private static readonly string FdcEffectLifecycleUpdateTrigger = $"""
        CREATE TRIGGER TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BU
        BEFORE UPDATE ON FDC_INTERLOCK_HISTORY
        WHEN NOT (
        {BuildFdcLifecycleRowValidityPredicate("NEW.")}
        ) OR NOT (
        {BuildFdcLifecycleTransitionValidityPredicate("NEW.", "OLD.")}
        )
        BEGIN
            SELECT RAISE(ABORT, 'FDC interlock effect lifecycle state or transition is invalid');
        END;
        """;

    private const string FdcEffectLifecycleDeleteTrigger = """
        CREATE TRIGGER TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BD
        BEFORE DELETE ON FDC_INTERLOCK_HISTORY
        BEGIN
            SELECT RAISE(ABORT, 'FDC interlock effect history is append-only');
        END;
        """;

    private const string FdcRuntimeOwnershipUpdateTrigger = """
        CREATE TRIGGER TR_FDC_RUNTIME_OWNERSHIP_FENCE_BU
        BEFORE UPDATE ON FDC_RUNTIME_OWNERSHIP
        WHEN NOT (
            (NEW.FENCE_TOKEN = OLD.FENCE_TOKEN
             AND OLD.OWNER_ID IS NOT NULL
             AND NEW.OWNER_ID = OLD.OWNER_ID
             AND NEW.CONFIG_REVISION = OLD.CONFIG_REVISION
             AND NEW.LEASE_SECRET_HASH = OLD.LEASE_SECRET_HASH
             AND NEW.HEARTBEAT_AT >= OLD.HEARTBEAT_AT
             AND OLD.LEASE_EXPIRES_AT > STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
             AND NEW.HEARTBEAT_AT BETWEEN
                 STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '-5 seconds')
                 AND STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
             AND NEW.LEASE_EXPIRES_AT >= OLD.LEASE_EXPIRES_AT
             AND NEW.LEASE_EXPIRES_AT > NEW.HEARTBEAT_AT
             AND NEW.LEASE_EXPIRES_AT <=
                 STRFTIME('%Y-%m-%d %H:%M:%f', NEW.HEARTBEAT_AT, '+1 day'))
            OR
            (NEW.FENCE_TOKEN = OLD.FENCE_TOKEN
             AND OLD.OWNER_ID IS NOT NULL
             AND NEW.OWNER_ID IS NULL)
            OR
            (OLD.FENCE_TOKEN = NEW.FENCE_TOKEN - 1
             AND NEW.OWNER_ID IS NOT NULL
             AND LENGTH(NEW.CONFIG_REVISION) = 64
             AND NEW.CONFIG_REVISION NOT GLOB '*[^0-9a-f]*'
             AND LENGTH(NEW.LEASE_SECRET_HASH) = 64
             AND NEW.LEASE_SECRET_HASH NOT GLOB '*[^0-9a-f]*'
             AND (OLD.OWNER_ID IS NULL
                  OR OLD.LEASE_EXPIRES_AT <= STRFTIME('%Y-%m-%d %H:%M:%f', 'now'))
             AND NEW.HEARTBEAT_AT BETWEEN
                 STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '-5 seconds')
                 AND STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
             AND NEW.LEASE_EXPIRES_AT > NEW.HEARTBEAT_AT
             AND NEW.LEASE_EXPIRES_AT <=
                 STRFTIME('%Y-%m-%d %H:%M:%f', NEW.HEARTBEAT_AT, '+1 day')))
        BEGIN
            SELECT RAISE(ABORT, 'FDC runtime ownership transition or fence token is invalid');
        END;
        """;

    private const string FdcRuntimeOwnershipDeleteTrigger = """
        CREATE TRIGGER TR_FDC_RUNTIME_OWNERSHIP_FENCE_BD
        BEFORE DELETE ON FDC_RUNTIME_OWNERSHIP
        BEGIN
            SELECT RAISE(ABORT, 'FDC runtime ownership row and fence counter are not deletable');
        END;
        """;

    /// <summary>
    /// SQLite cannot add V146's SQL Server CHECK constraints to an existing table. Keep the row
    /// shape in one expression so INSERT, UPDATE, and boot-time reconciliation cannot drift.
    /// </summary>
    private static string BuildFdcLifecycleRowValidityPredicate(string qualifier) => $"""
        TYPEOF({qualifier}VERSION) = 'integer'
        AND {qualifier}VERSION > 0
        AND TYPEOF({qualifier}IS_RESOLVED) = 'integer'
        AND {qualifier}EFFECT_STATE IN ('Prepared', 'Applied', 'ConditionNormalized', 'ReleasePending', 'Resolved')
        AND (
            ({qualifier}IS_RESOLVED = 0 AND {qualifier}EFFECT_STATE <> 'Resolved')
            OR ({qualifier}IS_RESOLVED = 1 AND {qualifier}EFFECT_STATE = 'Resolved'))
        AND (
            ({qualifier}EFFECT_STATE = 'Prepared'
             AND {qualifier}APPLY_ACK_ID IS NULL
             AND {qualifier}APPLY_CONFIRMED_AT IS NULL
             AND {qualifier}CONDITION_NORMALIZED_AT IS NULL
             AND {qualifier}CONDITION_NORMALIZED_VALUE IS NULL
             AND {qualifier}RELEASE_ACK_ID IS NULL
             AND {qualifier}RELEASE_CONFIRMED_AT IS NULL)
            OR (
                {qualifier}EFFECT_STATE = 'Applied'
                AND NULLIF(TRIM({qualifier}APPLY_ACK_ID), '') IS NOT NULL
                AND {qualifier}APPLY_CONFIRMED_AT IS NOT NULL
                AND {qualifier}CONDITION_NORMALIZED_AT IS NULL
                AND {qualifier}CONDITION_NORMALIZED_VALUE IS NULL
                AND {qualifier}RELEASE_ACK_ID IS NULL
                AND {qualifier}RELEASE_CONFIRMED_AT IS NULL)
            OR (
                {qualifier}EFFECT_STATE IN ('ConditionNormalized', 'ReleasePending')
                AND NULLIF(TRIM({qualifier}APPLY_ACK_ID), '') IS NOT NULL
                AND {qualifier}APPLY_CONFIRMED_AT IS NOT NULL
                AND {qualifier}CONDITION_NORMALIZED_AT IS NOT NULL
                AND {qualifier}CONDITION_NORMALIZED_VALUE IS NOT NULL
                AND {qualifier}CONDITION_NORMALIZED_AT >= {qualifier}APPLY_CONFIRMED_AT
                AND {qualifier}RELEASE_ACK_ID IS NULL
                AND {qualifier}RELEASE_CONFIRMED_AT IS NULL)
            OR (
                {qualifier}EFFECT_STATE = 'Resolved'
                AND ((
                    NULLIF(TRIM({qualifier}APPLY_ACK_ID), '') IS NOT NULL
                    AND {qualifier}APPLY_CONFIRMED_AT IS NOT NULL
                    AND {qualifier}CONDITION_NORMALIZED_AT IS NOT NULL
                    AND {qualifier}CONDITION_NORMALIZED_VALUE IS NOT NULL
                    AND NULLIF(TRIM({qualifier}RELEASE_ACK_ID), '') IS NOT NULL
                    AND {qualifier}RELEASE_CONFIRMED_AT IS NOT NULL
                    AND {qualifier}RESOLVED_AT IS NOT NULL
                    AND {qualifier}CONDITION_NORMALIZED_AT >= {qualifier}APPLY_CONFIRMED_AT
                    AND {qualifier}RELEASE_CONFIRMED_AT >= {qualifier}CONDITION_NORMALIZED_AT
                    AND {qualifier}RESOLVED_AT >= {qualifier}RELEASE_CONFIRMED_AT)
                OR {qualifier}LAST_ERROR = 'LegacyResolvedBeforeV146')))
        """;

    /// <summary>
    /// Reconciliation may deliberately reassert a normalized/release-pending STOP and return to
    /// Applied before trusting a fresh PLC snapshot. Other backward jumps, terminal mutation, and
    /// non-increasing concurrency versions are invalid direct-writer transitions.
    /// </summary>
    private static string BuildFdcLifecycleTransitionValidityPredicate(
        string newQualifier,
        string oldQualifier) => $"""
        {newQualifier}VERSION > {oldQualifier}VERSION
        AND {oldQualifier}EFFECT_STATE <> 'Resolved'
        AND (
            ({oldQualifier}EFFECT_STATE = 'Prepared'
             AND {newQualifier}EFFECT_STATE IN ('Prepared', 'Applied'))
            OR ({oldQualifier}EFFECT_STATE = 'Applied'
                AND {newQualifier}EFFECT_STATE IN ('Applied', 'ConditionNormalized'))
            OR ({oldQualifier}EFFECT_STATE = 'ConditionNormalized'
                AND {newQualifier}EFFECT_STATE IN ('Applied', 'ConditionNormalized', 'ReleasePending', 'Resolved'))
            OR ({oldQualifier}EFFECT_STATE = 'ReleasePending'
                AND {newQualifier}EFFECT_STATE IN ('Applied', 'ReleasePending', 'Resolved')))
        """;

    /// <summary>
    /// 스키마를 보장한다(idempotent). 빈 DB면 전체 마이그레이션을 1회 적용하고(시드·ALTER 포함),
    /// 이미 사용자 테이블이 있으면 '새로 추가된 마이그레이션의 누락 테이블'만 증분 생성한다.
    /// 후자 덕분에 기존 DB에 신규 마이그레이션(예: 새 모듈 테이블)을 추가해도 재기동 시 자동 생성된다
    /// (과거에는 테이블이 하나라도 있으면 전부 건너뛰어 신규 테이블이 영영 생성되지 않았다).
    /// </summary>
    public static void EnsureSchema(string connectionString)
    {
        // Validate the release bundle before HasUserTables opens (and may create) the SQLite file.
        _ = GetOrderedMigrationFiles(FindMigrationsDir());
        if (HasUserTables(connectionString))
        {
            CreateMissingTables(connectionString);
            return;
        }
        Apply(connectionString);
    }

    /// <summary>
    /// 모든 마이그레이션을 적용한다(빈 DB 가정 — CREATE TABLE에 IF NOT EXISTS 없음).
    /// 통합 테스트는 매번 새 임시 DB를 만들므로 이 경로를 직접 쓴다.
    /// </summary>
    public static void Apply(string connectionString)
    {
        var dir = FindMigrationsDir();
        var migrationFiles = GetOrderedMigrationFiles(dir);
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        // FK는 단순화를 위해 비강제(마이그레이션 순서·일부 교차참조 무시). 운영(MSSQL)은 FK를 그대로 강제한다.
        Exec(conn, "PRAGMA foreign_keys = OFF;");

        foreach (var file in migrationFiles)
        {
            var ddl = ToSqlite(File.ReadAllText(file));
            try
            {
                Exec(conn, ddl);
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException(
                    $"SQLite 스키마 생성 실패 @ {Path.GetFileName(file)}: {ex.Message}", ex);
            }
        }

        EnsureReadQueryRoleDefaults(conn);
        EnsureEstEquipmentOutputScope(conn);
        EnsurePomBoundaryTriggers(conn);
        EnsureQmsInspectionExecutionV2(conn);
        EnsureQmsAiEvidenceIntegrity(conn);
        EnsureQmsInspectionIntegrity(conn);
        EnsureEmsMaintenancePlanBoundary(conn);
        EnsureEmsMaintenanceExecutionIntegrity(conn);
        EnsureEmsSparePartManagementIntegrity(conn);
        EnsureEmsMdmMasterIntegrity(conn);
        EnsureUtilityMeterConfigurationHistory(conn);
        EnsureAppendOnlyEvidenceGuards(conn);
        EnsureEmsToolMountPositionGuard(conn);
        EnsureTraceProjectionPerformanceSchema(conn, migrationDmlAlreadyApplied: true);
        EnsureFdcInterlockEffectLifecycleSchema(conn, migrationDmlAlreadyApplied: true);
        EnsureFdcRuntimeOwnershipSchema(conn);
        EnsureFdcOpenStateIndexes(conn);
        EnsureFdcEndpointConfigurationIntegrity(conn);
        EnsureQueryPerformanceIndexes(conn);
    }

    /// <summary>
    /// 이미 테이블이 있는 DB에 누락 테이블/인덱스와 단순 ADD COLUMN을 멱등 적용한다
    /// (CREATE TABLE/INDEX는 IF NOT EXISTS로 변환하고, 컬럼은 실제 존재 여부를 먼저 확인한다).
    /// 컬럼 변경·삭제와 일반 INSERT/UPDATE 같은 데이터 migration은 이 공통 루프에서 건너뛰며,
    /// 보정이 필요한 버전은 아래의 명시적 Ensure* reconciliation 단계가 검증·적용한다.
    /// </summary>
    public static void CreateMissingTables(string connectionString)
    {
        var dir = FindMigrationsDir();
        var migrationFiles = GetOrderedMigrationFiles(dir);
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        Exec(conn, "PRAGMA foreign_keys = OFF;");
        // V110의 테이블이 이미 존재하는 개발 DB에도 관리 서비스가 요구하는 버전/멱등 컬럼을
        // 먼저 보강한다. 이후 루프가 새 filtered index를 만들 때 missing-column으로 실패하지 않는다.
        EnsureEmsSparePartManagementColumns(conn);

        foreach (var file in migrationFiles)
        {
            var ddl = ToSqlite(File.ReadAllText(file));
            foreach (var stmt in SplitSqlStatements(ddl))
            {
                // 증분 생성 패스 — 누락 컬럼의 ALTER TABLE ADD COLUMN과 멱등 CREATE TABLE/INDEX만 실행한다.
                // 분류는 -- 주석을 벗겨낸 코드로만 판정한다 — 주석에 CREATE TABLE이 '언급'된 ALTER 문장이
                // 생성문으로 오분류돼 실행되면 기존 DB에서 duplicate column으로 기동이 죽는다(V080 실사고).
                var code = stmt;
                var addColumn = Regex.Match(code,
                    @"^\s*ALTER\s+TABLE\s+(\w+)\s+ADD\s+COLUMN\s+(\w+)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (addColumn.Success)
                {
                    var table = addColumn.Groups[1].Value;
                    var column = addColumn.Groups[2].Value;
                    if (HasColumn(conn, table, column)) continue;
                    try { Exec(conn, code); }
                    catch (SqliteException ex)
                    {
                        throw new InvalidOperationException(
                            $"SQLite incremental column creation failed @ {Path.GetFileName(file)}: {ex.Message}", ex);
                    }
                    continue;
                }
                if (!Regex.IsMatch(code, @"\bCREATE\s+(TABLE|(?:UNIQUE\s+)?INDEX)\b", RegexOptions.IgnoreCase))
                    continue;
                // V142's partial ready index must be built only after the one-time terminal-row
                // backfill. Creating it here would first index the entire legacy inbox (default=1)
                // and then delete most entries again in the reconciliation phase below.
                if (Regex.IsMatch(
                        code,
                        @"\bCREATE\s+INDEX\s+IX_IVT_TRACE_INBOX_READY\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;
                try
                {
                    Exec(conn, code);
                }
                catch (SqliteException ex)
                {
                    throw new InvalidOperationException(
                        $"SQLite 증분 스키마 생성 실패 @ {Path.GetFileName(file)}: {ex.Message}", ex);
                }
            }
        }

        // 증분 경로는 일반 INSERT/UPDATE를 실행하지 않는다. 표준 역할의 정확한 레거시 값만
        // 바꾸는 멱등 데이터 보정은 명시적으로 재조정한다. 사용자 커스텀 역할은 보존한다.
        EnsureReadQueryRoleDefaults(conn);
        EnsureEstEquipmentOutputScope(conn);
        EnsurePomBoundaryTriggers(conn);
        EnsureQmsInspectionExecutionV2(conn);
        EnsureQmsAiEvidenceIntegrity(conn);
        EnsureQmsInspectionIntegrity(conn);
        EnsureEmsMaintenancePlanBoundary(conn);
        EnsureEmsMaintenanceExecutionIntegrity(conn);
        EnsureEmsSparePartManagementIntegrity(conn);
        EnsureEmsMdmMasterIntegrity(conn);
        EnsureUtilityMeterConfigurationHistory(conn);
        EnsureAppendOnlyEvidenceGuards(conn);
        EnsureEmsToolMountPositionGuard(conn);
        EnsureTraceProjectionPerformanceSchema(conn, migrationDmlAlreadyApplied: false);
        EnsureFdcInterlockEffectLifecycleSchema(conn, migrationDmlAlreadyApplied: false);
        EnsureFdcRuntimeOwnershipSchema(conn);
        EnsureFdcOpenStateIndexes(conn);
        EnsureFdcEndpointConfigurationIntegrity(conn);
        EnsureQueryPerformanceIndexes(conn);
    }

    /// <summary>
    /// V121's SQL Server migration reports conflicting active mounts before creating its filtered
    /// unique index. SQLite omits that server-only block, so reproduce the same preflight here and
    /// fail with the physical equipment/position and mount ids instead of a bare UNIQUE error.
    /// </summary>
    private static void EnsureEmsToolMountPositionGuard(SqliteConnection conn)
    {
        if (!HasTable(conn, "EMS_TOOL_MOUNT_HISTORY")
            || !HasColumn(conn, "EMS_TOOL_MOUNT_HISTORY", "EQUIPMENT_ID")
            || !HasColumn(conn, "EMS_TOOL_MOUNT_HISTORY", "POSITION_CODE")
            || !HasColumn(conn, "EMS_TOOL_MOUNT_HISTORY", "UNMOUNTED_AT"))
            return;

        using (var duplicate = conn.CreateCommand())
        {
            duplicate.CommandText = """
                SELECT EQUIPMENT_ID, POSITION_CODE, COUNT(*) AS DUPLICATE_COUNT,
                       GROUP_CONCAT(MOUNT_ID, ',') AS MOUNT_IDS
                FROM EMS_TOOL_MOUNT_HISTORY
                WHERE UNMOUNTED_AT IS NULL AND POSITION_CODE IS NOT NULL
                GROUP BY EQUIPMENT_ID, POSITION_CODE
                HAVING COUNT(*) > 1
                ORDER BY DUPLICATE_COUNT DESC, EQUIPMENT_ID, POSITION_CODE
                LIMIT 1;
                """;
            using var reader = duplicate.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidOperationException(
                    "V121 cannot create UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION. " +
                    $"Duplicate active mounts: equipment='{reader.GetString(0)}', " +
                    $"position='{reader.GetString(1)}', count={reader.GetInt64(2)}, " +
                    $"mountIds='{reader.GetString(3)}'. Reconcile the physical mount state first.");
            }
        }

        EnsureSqliteIndex(
            conn,
            "EMS_TOOL_MOUNT_HISTORY",
            "UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION",
            unique: true,
            partial: true,
            """
            CREATE UNIQUE INDEX UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION
                ON EMS_TOOL_MOUNT_HISTORY (EQUIPMENT_ID, POSITION_CODE)
                WHERE UNMOUNTED_AT IS NULL AND POSITION_CODE IS NOT NULL;
            """,
            new IndexKey("EQUIPMENT_ID", Descending: false),
            new IndexKey("POSITION_CODE", Descending: false));
    }

    /// <summary>
    /// Reconciles the V142 cursor/work-set schema for databases upgraded through the incremental
    /// path. That path deliberately skips UPDATE/INSERT backfills, so a SQLite-only durable marker
    /// runs them once. Repeating EnsureSchema must never scan/sort the ever-growing inbox again.
    /// Terminal inbox evidence remains queryable but is excluded from the small retry queue and no
    /// longer participates in source-cursor reads.
    /// </summary>
    private static void EnsureTraceProjectionPerformanceSchema(
        SqliteConnection conn,
        bool migrationDmlAlreadyApplied)
    {
        if (!HasTable(conn, "IVT_TRACE_PROJECTION_INBOX")) return;

        if (!HasColumn(conn, "IVT_TRACE_PROJECTION_INBOX", "IS_WORK_ITEM"))
        {
            Exec(conn, """
                ALTER TABLE IVT_TRACE_PROJECTION_INBOX
                    ADD COLUMN IS_WORK_ITEM INTEGER NOT NULL DEFAULT 1;
                """);
        }

        if (HasTable(conn, "IVT_TRACE_CONSUMPTION_BINDING")
            && !HasTable(conn, "IVT_TRACE_INGESTION_CURSOR"))
        {
            Exec(conn, """
                CREATE TABLE IVT_TRACE_INGESTION_CURSOR (
                    BINDING_ID TEXT NOT NULL PRIMARY KEY,
                    LAST_COLLECT_ID TEXT NOT NULL,
                    LAST_COLLECTED_AT TEXT NOT NULL,
                    CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                    CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                    UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                """);
        }

        var hasCursorSchema = HasTable(conn, "IVT_TRACE_CONSUMPTION_BINDING")
                              && HasTable(conn, "IVT_TRACE_INGESTION_CURSOR");
        if (hasCursorSchema)
        {
            EnsureSqliteReconciliationLedger(conn);
        }

        // BEGIN IMMEDIATE prevents an old process from writing between reconciliation and the
        // invariant becoming durable. The marker is committed only after canonical triggers are
        // installed. A stale/missing trigger is repaired transactionally; inconsistent data fails
        // closed and rolls the previous definitions back.
        using (var transaction = conn.BeginTransaction(deferred: false))
        {
            try
            {
                const string reconciliationId = "V142__IVT_TRACE_INGESTION_CURSOR";
                var hasMarker = hasCursorSchema
                                && HasSqliteReconciliation(conn, transaction, reconciliationId);
                var triggersCanonical = SqliteObjectDefinitionMatches(
                                            conn,
                                            transaction,
                                            "trigger",
                                            "TR_IVT_TRACE_INBOX_WORK_STATE_BI",
                                            TraceInboxWorkStateInsertTrigger)
                                        && SqliteObjectDefinitionMatches(
                                            conn,
                                            transaction,
                                            "trigger",
                                            "TR_IVT_TRACE_INBOX_WORK_STATE_BU",
                                            TraceInboxWorkStateUpdateTrigger);

                if (!triggersCanonical)
                {
                    Exec(conn, "DROP TRIGGER IF EXISTS TR_IVT_TRACE_INBOX_WORK_STATE_BI;", transaction);
                    Exec(conn, "DROP TRIGGER IF EXISTS TR_IVT_TRACE_INBOX_WORK_STATE_BU;", transaction);
                }

                if (hasCursorSchema && !hasMarker && !migrationDmlAlreadyApplied)
                {
                    Exec(conn, """
                        UPDATE IVT_TRACE_PROJECTION_INBOX
                           SET IS_WORK_ITEM = CASE
                               WHEN STATUS IN ('Pending', 'Error') THEN 1 ELSE 0 END
                         WHERE IS_WORK_ITEM <> CASE
                               WHEN STATUS IN ('Pending', 'Error') THEN 1 ELSE 0 END;
                        """, transaction);

                    Exec(conn, """
                        WITH MissingBinding AS (
                            SELECT DISTINCT I.BINDING_ID
                              FROM IVT_TRACE_PROJECTION_INBOX I
                              LEFT JOIN IVT_TRACE_INGESTION_CURSOR C
                                ON C.BINDING_ID = I.BINDING_ID
                             WHERE C.BINDING_ID IS NULL
                        ),
                        RankedInbox AS (
                            SELECT I.BINDING_ID,
                                   I.COLLECT_ID,
                                   I.COLLECTED_AT,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY I.BINDING_ID
                                       ORDER BY I.COLLECTED_AT DESC, I.COLLECT_ID DESC) AS RN
                              FROM IVT_TRACE_PROJECTION_INBOX I
                              JOIN MissingBinding M ON M.BINDING_ID = I.BINDING_ID
                        )
                        INSERT INTO IVT_TRACE_INGESTION_CURSOR
                            (BINDING_ID, LAST_COLLECT_ID, LAST_COLLECTED_AT,
                             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                        SELECT I.BINDING_ID, I.COLLECT_ID, I.COLLECTED_AT,
                               'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP
                          FROM RankedInbox I
                         WHERE I.RN = 1;
                        """, transaction);
                }

                if (!hasMarker || !triggersCanonical)
                {
                    using var mismatch = conn.CreateCommand();
                    mismatch.Transaction = transaction;
                    mismatch.CommandText = """
                        SELECT COUNT(*)
                          FROM IVT_TRACE_PROJECTION_INBOX
                         WHERE NOT (
                             (STATUS IN ('Pending', 'Error') AND IS_WORK_ITEM = 1)
                             OR (STATUS IN ('Applied', 'Ignored') AND IS_WORK_ITEM = 0));
                        """;
                    var mismatchCount = Convert.ToInt64(mismatch.ExecuteScalar() ?? 0L);
                    if (mismatchCount != 0)
                    {
                        throw new InvalidOperationException(
                            $"V142 SQLite reconciliation found {mismatchCount} TRACE inbox row(s) " +
                            "whose STATUS and IS_WORK_ITEM disagree. Repair the rows before startup.");
                    }
                }

                if (!triggersCanonical)
                {
                    Exec(conn, TraceInboxWorkStateInsertTrigger, transaction);
                    Exec(conn, TraceInboxWorkStateUpdateTrigger, transaction);
                }

                if (hasCursorSchema && !hasMarker)
                {
                    using var marker = conn.CreateCommand();
                    marker.Transaction = transaction;
                    marker.CommandText = """
                        INSERT INTO SYS_SQLITE_RECONCILIATION
                            (RECONCILIATION_ID, APPLIED_AT)
                        VALUES (@id, CURRENT_TIMESTAMP);
                        """;
                    marker.Parameters.AddWithValue("@id", reconciliationId);
                    marker.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        if (HasIndex(conn, "IX_IVT_TRACE_INBOX_BINDING_CURSOR"))
            Exec(conn, "DROP INDEX IX_IVT_TRACE_INBOX_BINDING_CURSOR;");
        if (HasIndex(conn, "IX_IVT_TRACE_INBOX_WORK"))
            Exec(conn, "DROP INDEX IX_IVT_TRACE_INBOX_WORK;");
        if (HasIndex(conn, "IX_IVT_TRACE_INBOX_CURSOR_BACKFILL"))
            Exec(conn, "DROP INDEX IX_IVT_TRACE_INBOX_CURSOR_BACKFILL;");

        EnsureSqliteIndex(
            conn,
            "IVT_TRACE_PROJECTION_INBOX",
            "IX_IVT_TRACE_INBOX_READY",
            unique: false,
            partial: true,
            """
            CREATE INDEX IX_IVT_TRACE_INBOX_READY
                ON IVT_TRACE_PROJECTION_INBOX (COLLECTED_AT, COLLECT_ID, BINDING_ID)
                WHERE IS_WORK_ITEM = 1;
            """,
            new IndexKey("COLLECTED_AT", Descending: false),
            new IndexKey("COLLECT_ID", Descending: false),
            new IndexKey("BINDING_ID", Descending: false));
    }

    private static void EnsureSqliteReconciliationLedger(SqliteConnection conn) =>
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS SYS_SQLITE_RECONCILIATION (
                RECONCILIATION_ID TEXT NOT NULL PRIMARY KEY,
                APPLIED_AT TEXT NOT NULL);
            """);

    private static bool HasSqliteReconciliation(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string reconciliationId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
              FROM SYS_SQLITE_RECONCILIATION
             WHERE RECONCILIATION_ID = @id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", reconciliationId);
        return command.ExecuteScalar() is not null;
    }

    private static bool SqliteObjectDefinitionMatches(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string type,
        string name,
        string expectedSql)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sql
              FROM sqlite_master
             WHERE type = @type AND name = @name;
            """;
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);
        var actualSql = Convert.ToString(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(
            NormalizeSqliteDefinition(actualSql),
            NormalizeSqliteDefinition(expectedSql),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSqliteDefinition(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        return Regex.Replace(sql, @"\s+", " ", RegexOptions.CultureInvariant)
            .Trim()
            .TrimEnd(';');
    }

    /// <summary>
    /// V146 maps pre-lifecycle terminal rows to Resolved exactly once and installs the SQLite
    /// equivalent of the SQL Server lifecycle CHECK constraints. Backfill, canonical triggers and
    /// the durable marker share one immediate transaction so an older writer cannot enter between
    /// data reconciliation and invariant publication.
    /// </summary>
    private static void EnsureFdcInterlockEffectLifecycleSchema(
        SqliteConnection conn,
        bool migrationDmlAlreadyApplied)
    {
        const string table = "FDC_INTERLOCK_HISTORY";
        if (!HasTable(conn, table)
            || !HasColumn(conn, table, "EFFECT_STATE")
            || !HasColumn(conn, table, "APPLY_ACK_ID")
            || !HasColumn(conn, table, "APPLY_CONFIRMED_AT")
            || !HasColumn(conn, table, "CONDITION_NORMALIZED_AT")
            || !HasColumn(conn, table, "CONDITION_NORMALIZED_VALUE")
            || !HasColumn(conn, table, "RELEASE_ACK_ID")
            || !HasColumn(conn, table, "RELEASE_CONFIRMED_AT")
            || !HasColumn(conn, table, "LAST_ERROR")
            || !HasColumn(conn, table, "VERSION"))
            return;

        EnsureSqliteReconciliationLedger(conn);
        using (var transaction = conn.BeginTransaction(deferred: false))
        {
            try
            {
                const string reconciliationId = "V146__FDC_INTERLOCK_EFFECT_LIFECYCLE";
                var hasMarker = HasSqliteReconciliation(conn, transaction, reconciliationId);
                var triggersCanonical = SqliteObjectDefinitionMatches(
                                            conn,
                                            transaction,
                                            "trigger",
                                            "TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BI",
                                            FdcEffectLifecycleInsertTrigger)
                                        && SqliteObjectDefinitionMatches(
                                            conn,
                                            transaction,
                                            "trigger",
                                            "TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BU",
                                            FdcEffectLifecycleUpdateTrigger)
                                        && SqliteObjectDefinitionMatches(
                                            conn,
                                            transaction,
                                            "trigger",
                                            "TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BD",
                                            FdcEffectLifecycleDeleteTrigger);

                if (!triggersCanonical)
                {
                    Exec(conn, "DROP TRIGGER IF EXISTS TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BI;", transaction);
                    Exec(conn, "DROP TRIGGER IF EXISTS TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BU;", transaction);
                    Exec(conn, "DROP TRIGGER IF EXISTS TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BD;", transaction);
                }

                if (!hasMarker && !migrationDmlAlreadyApplied)
                {
                    Exec(conn, """
                        UPDATE FDC_INTERLOCK_HISTORY
                           SET EFFECT_STATE = CASE
                               WHEN IS_RESOLVED = 1 THEN 'Resolved' ELSE 'Prepared' END,
                               LAST_ERROR = CASE
                               WHEN IS_RESOLVED = 1 THEN 'LegacyResolvedBeforeV146' ELSE LAST_ERROR END,
                               VERSION = CASE
                               WHEN TYPEOF(VERSION) = 'integer' AND VERSION > 0 THEN VERSION ELSE 1 END;
                        """, transaction);
                }

                if (!hasMarker || !triggersCanonical)
                {
                    using var invalid = conn.CreateCommand();
                    invalid.Transaction = transaction;
                    invalid.CommandText = $"""
                        SELECT COUNT(*)
                          FROM FDC_INTERLOCK_HISTORY
                         WHERE NOT (
                         {BuildFdcLifecycleRowValidityPredicate(string.Empty)}
                         );
                        """;
                    var invalidCount = Convert.ToInt64(invalid.ExecuteScalar() ?? 0L);
                    if (invalidCount != 0)
                    {
                        throw new InvalidOperationException(
                            $"V146 SQLite reconciliation found {invalidCount} invalid FDC interlock " +
                            "effect lifecycle row(s). Repair the rows before startup.");
                    }
                }

                if (!triggersCanonical)
                {
                    Exec(conn, FdcEffectLifecycleInsertTrigger, transaction);
                    Exec(conn, FdcEffectLifecycleUpdateTrigger, transaction);
                    Exec(conn, FdcEffectLifecycleDeleteTrigger, transaction);
                }

                if (!hasMarker)
                {
                    using var marker = conn.CreateCommand();
                    marker.Transaction = transaction;
                    marker.CommandText = """
                        INSERT INTO SYS_SQLITE_RECONCILIATION
                            (RECONCILIATION_ID, APPLIED_AT)
                        VALUES (@id, CURRENT_TIMESTAMP);
                        """;
                    marker.Parameters.AddWithValue("@id", reconciliationId);
                    marker.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

    }

    /// <summary>
    /// V149의 GLOBAL writer 행과 fence counter를 보장한다. 증분 migration loop는 일반 INSERT를
    /// 실행하지 않으므로 최초 upgrade에서만 seed를 만들고 durable marker 이후 행이 사라지면
    /// token 0으로 재생성하지 않고 fail-closed 한다. canonical trigger는 직접 writer의 fence
    /// 감소·재사용과 행 삭제를 차단한다.
    /// </summary>
    private static void EnsureFdcRuntimeOwnershipSchema(SqliteConnection conn)
    {
        const string table = "FDC_RUNTIME_OWNERSHIP";
        if (!HasTable(conn, table)) return;

        EnsureSqliteReconciliationLedger(conn);
        using var transaction = conn.BeginTransaction(deferred: false);
        try
        {
            const string reconciliationId = "V149__FDC_RUNTIME_OWNERSHIP_FENCE";
            var hasMarker = HasSqliteReconciliation(conn, transaction, reconciliationId);
            if (!HasColumn(conn, table, "LEASE_SECRET_HASH"))
            {
                Exec(conn, "ALTER TABLE FDC_RUNTIME_OWNERSHIP ADD COLUMN LEASE_SECRET_HASH TEXT NULL;", transaction);
            }

            var triggersCanonical = SqliteObjectDefinitionMatches(
                                        conn,
                                        transaction,
                                        "trigger",
                                        "TR_FDC_RUNTIME_OWNERSHIP_FENCE_BU",
                                        FdcRuntimeOwnershipUpdateTrigger)
                                    && SqliteObjectDefinitionMatches(
                                        conn,
                                        transaction,
                                        "trigger",
                                        "TR_FDC_RUNTIME_OWNERSHIP_FENCE_BD",
                                        FdcRuntimeOwnershipDeleteTrigger);

            if (!triggersCanonical)
            {
                Exec(conn, "DROP TRIGGER IF EXISTS TR_FDC_RUNTIME_OWNERSHIP_FENCE_BU;", transaction);
                Exec(conn, "DROP TRIGGER IF EXISTS TR_FDC_RUNTIME_OWNERSHIP_FENCE_BD;", transaction);
            }

            using (var count = conn.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM FDC_RUNTIME_OWNERSHIP;";
                var rowCount = Convert.ToInt64(count.ExecuteScalar() ?? 0L);
                if (rowCount == 0 && !hasMarker)
                {
                    Exec(conn, """
                        INSERT INTO FDC_RUNTIME_OWNERSHIP
                            (LEASE_SCOPE, OWNER_ID, FENCE_TOKEN, LEASE_EXPIRES_AT, HEARTBEAT_AT,
                             CONFIG_REVISION, LEASE_SECRET_HASH,
                             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                        VALUES
                            ('GLOBAL', NULL, 0, NULL, NULL, NULL, NULL,
                             'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                        """, transaction);
                    rowCount = 1;
                }

                if (rowCount != 1)
                {
                    var reason = rowCount == 0 && hasMarker
                        ? "The durable marker exists, so recreating fence token 0 would reuse issued tokens."
                        : "Exactly one GLOBAL writer row is required.";
                    throw new InvalidOperationException(
                        $"V149 SQLite reconciliation found {rowCount} FDC runtime ownership rows. {reason}");
                }
            }

            using (var invalid = conn.CreateCommand())
            {
                invalid.Transaction = transaction;
                invalid.CommandText = """
                    SELECT COUNT(*)
                      FROM FDC_RUNTIME_OWNERSHIP
                     WHERE LEASE_SCOPE <> 'GLOBAL'
                        OR TYPEOF(FENCE_TOKEN) <> 'integer'
                        OR FENCE_TOKEN < 0
                        OR NOT (
                            (OWNER_ID IS NULL
                             AND LEASE_EXPIRES_AT IS NULL
                             AND HEARTBEAT_AT IS NULL
                             AND CONFIG_REVISION IS NULL
                             AND LEASE_SECRET_HASH IS NULL)
                            OR
                            (OWNER_ID IS NOT NULL
                             AND NULLIF(TRIM(OWNER_ID), '') IS NOT NULL
                             AND LEASE_EXPIRES_AT IS NOT NULL
                             AND HEARTBEAT_AT IS NOT NULL
                             AND JULIANDAY(LEASE_EXPIRES_AT) IS NOT NULL
                             AND JULIANDAY(HEARTBEAT_AT) IS NOT NULL
                             AND LEASE_EXPIRES_AT > HEARTBEAT_AT
                             AND CONFIG_REVISION IS NOT NULL
                             AND LENGTH(CONFIG_REVISION) = 64
                             AND CONFIG_REVISION NOT GLOB '*[^0-9a-f]*'
                             AND LEASE_SECRET_HASH IS NOT NULL
                             AND LENGTH(LEASE_SECRET_HASH) = 64
                             AND LEASE_SECRET_HASH NOT GLOB '*[^0-9a-f]*'
                             AND LEASE_EXPIRES_AT <=
                                 STRFTIME('%Y-%m-%d %H:%M:%f', HEARTBEAT_AT, '+1 day')));
                    """;
                var invalidCount = Convert.ToInt64(invalid.ExecuteScalar() ?? 0L);
                if (invalidCount != 0)
                {
                    throw new InvalidOperationException(
                        $"V149 SQLite reconciliation found {invalidCount} invalid FDC runtime " +
                        "ownership row(s). Repair the row without decreasing its fence before startup.");
                }
            }

            if (!triggersCanonical)
            {
                Exec(conn, FdcRuntimeOwnershipUpdateTrigger, transaction);
                Exec(conn, FdcRuntimeOwnershipDeleteTrigger, transaction);
            }

            if (!hasMarker)
            {
                using var marker = conn.CreateCommand();
                marker.Transaction = transaction;
                marker.CommandText = """
                    INSERT INTO SYS_SQLITE_RECONCILIATION
                        (RECONCILIATION_ID, APPLIED_AT)
                    VALUES (@id, CURRENT_TIMESTAMP);
                    """;
                marker.Parameters.AddWithValue("@id", reconciliationId);
                marker.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>Locks the V141 process-restart recovery access paths on SQLite upgrades.</summary>
    private static void EnsureFdcOpenStateIndexes(SqliteConnection conn)
    {
        if (HasTable(conn, "FDC_INTERLOCK_HISTORY"))
        {
            EnsureSqliteIndex(
                conn,
                "FDC_INTERLOCK_HISTORY",
                "IX_FDC_INTERLOCK_OPEN_EQUIPMENT_PARAMETER",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_FDC_INTERLOCK_OPEN_EQUIPMENT_PARAMETER
                    ON FDC_INTERLOCK_HISTORY
                       (EQUIPMENT_ID, PARAMETER_ID, TRIGGERED_AT DESC)
                    WHERE IS_RESOLVED = 0;
                """,
                new IndexKey("EQUIPMENT_ID", Descending: false),
                new IndexKey("PARAMETER_ID", Descending: false),
                new IndexKey("TRIGGERED_AT", Descending: true));
        }

        if (HasTable(conn, "FDC_ALARM_HISTORY"))
        {
            EnsureSqliteIndex(
                conn,
                "FDC_ALARM_HISTORY",
                "IX_FDC_ALARM_OPEN_EQUIPMENT_PARAMETER",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_FDC_ALARM_OPEN_EQUIPMENT_PARAMETER
                    ON FDC_ALARM_HISTORY
                       (EQUIPMENT_ID, PARAMETER_ID, OCCURRED_AT DESC)
                    WHERE IS_CLEARED = 0;
                """,
                new IndexKey("EQUIPMENT_ID", Descending: false),
                new IndexKey("PARAMETER_ID", Descending: false),
                new IndexKey("OCCURRED_AT", Descending: true));
        }
    }

    /// <summary>V145의 구조화 PLC endpoint 옵션과 timeout/recovery 제약을 SQLite에도 재조정한다.
    /// ALTER TABLE ADD CONSTRAINT를 제거하는 방언 변환 특성 때문에 fresh/incremental 양쪽에서 동일한
    /// 사전검사와 INSERT/UPDATE trigger를 설치한다. 임의 options/비밀 컬럼은 만들지 않는다.</summary>
    private static void EnsureFdcEndpointConfigurationIntegrity(SqliteConnection conn)
    {
        const string table = "FDC_EQUIPMENT_ENDPOINT";
        if (!HasTable(conn, table)) return;

        EnsureColumn("MODBUS_UNIT_ID", "INTEGER NULL");
        EnsureColumn("S7_RACK", "INTEGER NULL");
        EnsureColumn("S7_SLOT", "INTEGER NULL");
        EnsureColumn("MITSUBISHI_STATION_NO", "INTEGER NULL");
        EnsureColumn("MITSUBISHI_NETWORK_NO", "INTEGER NULL");
        EnsureColumn("MITSUBISHI_PC_NO", "INTEGER NULL");
        EnsureColumn("MITSUBISHI_IO_NO", "INTEGER NULL");
        EnsureColumn("MITSUBISHI_FRAME_FORMAT", "TEXT NULL");
        EnsureColumn("CONNECTION_TIMEOUT_MS", "INTEGER NOT NULL DEFAULT 5000");
        EnsureColumn("READ_WRITE_TIMEOUT_MS", "INTEGER NOT NULL DEFAULT 5000");
        EnsureColumn("HEARTBEAT_TIMEOUT_MS", "INTEGER NOT NULL DEFAULT 5000");
        EnsureColumn("POLLING_DISCONNECT_BACKOFF_MS", "INTEGER NOT NULL DEFAULT 100");
        EnsureColumn("POLLING_MAX_DISCONNECT_BACKOFF_MS", "INTEGER NOT NULL DEFAULT 1000");

        // A deliberately tiny legacy test table can omit the V019 base columns. The additive V145
        // columns are still reconciled, while validation triggers wait until the real base contract exists.
        if (!HasColumn(conn, table, "PROTOCOL") || !HasColumn(conn, table, "ENDPOINT_URL"))
            return;

        // Some development databases may already contain nullable preview columns. Preserve explicit
        // values and supply only the V145 production defaults that a normal ADD NOT NULL would provide.
        Exec(conn, """
            UPDATE FDC_EQUIPMENT_ENDPOINT
               SET CONNECTION_TIMEOUT_MS = COALESCE(CONNECTION_TIMEOUT_MS, 5000),
                   READ_WRITE_TIMEOUT_MS = COALESCE(READ_WRITE_TIMEOUT_MS, 5000),
                   HEARTBEAT_TIMEOUT_MS = COALESCE(HEARTBEAT_TIMEOUT_MS, 5000),
                   POLLING_DISCONNECT_BACKOFF_MS = COALESCE(POLLING_DISCONNECT_BACKOFF_MS, 100),
                   POLLING_MAX_DISCONNECT_BACKOFF_MS = COALESCE(POLLING_MAX_DISCONNECT_BACKOFF_MS, 1000);
            """);

        const string invalidPredicate = """
            CONNECTION_TIMEOUT_MS IS NULL OR CONNECTION_TIMEOUT_MS <= 0
            OR READ_WRITE_TIMEOUT_MS IS NULL OR READ_WRITE_TIMEOUT_MS <= 0
            OR HEARTBEAT_TIMEOUT_MS IS NULL OR HEARTBEAT_TIMEOUT_MS <= 0
            OR POLLING_DISCONNECT_BACKOFF_MS IS NULL OR POLLING_DISCONNECT_BACKOFF_MS <= 0
            OR POLLING_MAX_DISCONNECT_BACKOFF_MS IS NULL
            OR POLLING_MAX_DISCONNECT_BACKOFF_MS < POLLING_DISCONNECT_BACKOFF_MS
            OR MODBUS_UNIT_ID < 0 OR MODBUS_UNIT_ID > 255
            OR S7_RACK < 0 OR S7_RACK > 7
            OR S7_SLOT < 0 OR S7_SLOT > 31
            OR MITSUBISHI_STATION_NO < 0 OR MITSUBISHI_STATION_NO > 255
            OR MITSUBISHI_NETWORK_NO < 0 OR MITSUBISHI_NETWORK_NO > 255
            OR MITSUBISHI_PC_NO < 0 OR MITSUBISHI_PC_NO > 255
            OR MITSUBISHI_IO_NO < 0 OR MITSUBISHI_IO_NO > 65535
            OR (MITSUBISHI_FRAME_FORMAT IS NOT NULL
                AND UPPER(MITSUBISHI_FRAME_FORMAT) NOT IN ('BINARY', 'ASCII'))
            OR (MODBUS_UNIT_ID IS NOT NULL AND UPPER(PROTOCOL) <> 'MODBUSTCP')
            OR ((S7_RACK IS NOT NULL OR S7_SLOT IS NOT NULL) AND UPPER(PROTOCOL) <> 'SIEMENSS7')
            OR ((MITSUBISHI_STATION_NO IS NOT NULL
                 OR MITSUBISHI_NETWORK_NO IS NOT NULL
                 OR MITSUBISHI_PC_NO IS NOT NULL
                 OR MITSUBISHI_IO_NO IS NOT NULL
                 OR MITSUBISHI_FRAME_FORMAT IS NOT NULL)
                AND UPPER(PROTOCOL) <> 'MITSUBISHIMC')
            OR INSTR(ENDPOINT_URL, '@') > 0
            OR INSTR(ENDPOINT_URL, '?') > 0
            OR INSTR(ENDPOINT_URL, '#') > 0
            OR INSTR(ENDPOINT_URL, '\') > 0
            OR (INSTR(ENDPOINT_URL, '://') > 0
                AND UPPER(SUBSTR(TRIM(ENDPOINT_URL), 1, 6)) <> 'TCP://')
            OR (INSTR(ENDPOINT_URL, '://') > 0
                AND INSTR(SUBSTR(ENDPOINT_URL, INSTR(ENDPOINT_URL, '://') + 3), '/') > 0)
            OR (INSTR(ENDPOINT_URL, '://') = 0 AND INSTR(ENDPOINT_URL, '/') > 0)
            """;
        var triggerInvalidPredicate = Regex.Replace(
            invalidPredicate,
            @"\b(CONNECTION_TIMEOUT_MS|READ_WRITE_TIMEOUT_MS|HEARTBEAT_TIMEOUT_MS|POLLING_DISCONNECT_BACKOFF_MS|POLLING_MAX_DISCONNECT_BACKOFF_MS|MODBUS_UNIT_ID|S7_RACK|S7_SLOT|MITSUBISHI_STATION_NO|MITSUBISHI_NETWORK_NO|MITSUBISHI_PC_NO|MITSUBISHI_IO_NO|MITSUBISHI_FRAME_FORMAT|PROTOCOL|ENDPOINT_URL)\b",
            "NEW.$1",
            RegexOptions.CultureInvariant);

        using (var invalid = conn.CreateCommand())
        {
            invalid.CommandText = $"""
                SELECT ENDPOINT_ID
                  FROM FDC_EQUIPMENT_ENDPOINT
                 WHERE {invalidPredicate}
                 ORDER BY ENDPOINT_ID
                 LIMIT 1;
                """;
            var endpointId = Convert.ToString(invalid.ExecuteScalar());
            if (!string.IsNullOrEmpty(endpointId))
            {
                throw new InvalidOperationException(
                    $"V145 cannot enable FDC PLC endpoint configuration. Invalid or secret-bearing endpoint='{endpointId}'.");
            }
        }

        Exec(conn, $"""
            DROP TRIGGER IF EXISTS TR_FDC_ENDPOINT_CONFIG_VALIDATE_INSERT;
            CREATE TRIGGER TR_FDC_ENDPOINT_CONFIG_VALIDATE_INSERT
            BEFORE INSERT ON FDC_EQUIPMENT_ENDPOINT
            WHEN {triggerInvalidPredicate}
            BEGIN
                SELECT RAISE(ABORT, 'V145 FDC PLC endpoint configuration is invalid');
            END;

            DROP TRIGGER IF EXISTS TR_FDC_ENDPOINT_CONFIG_VALIDATE_UPDATE;
            CREATE TRIGGER TR_FDC_ENDPOINT_CONFIG_VALIDATE_UPDATE
            BEFORE UPDATE ON FDC_EQUIPMENT_ENDPOINT
            WHEN {triggerInvalidPredicate}
            BEGIN
                SELECT RAISE(ABORT, 'V145 FDC PLC endpoint configuration is invalid');
            END;
            """);

        void EnsureColumn(string columnName, string definition)
        {
            if (!HasColumn(conn, table, columnName))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN {columnName} {definition};");
        }
    }

    /// <summary>
    /// Reconciles query-index definitions for existing SQLite databases. CREATE INDEX IF
    /// NOT EXISTS cannot replace stale definitions, so keys, direction, uniqueness and filters are
    /// compared before rebuilding. The exact V115 checklist duplicate is removed while its UNIQUE
    /// constraint index remains.
    /// </summary>
    private static void EnsureQueryPerformanceIndexes(SqliteConnection conn)
    {
        if (HasTable(conn, "EMS_TOOL_USAGE_HISTORY"))
        {
            EnsureSqliteIndex(
                conn,
                "EMS_TOOL_USAGE_HISTORY",
                "IX_EMS_TOOL_USAGE_MOUNT",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_EMS_TOOL_USAGE_MOUNT
                    ON EMS_TOOL_USAGE_HISTORY (MOUNT_ID, USED_AT DESC);
                """,
                new IndexKey("MOUNT_ID", Descending: false),
                new IndexKey("USED_AT", Descending: true));
        }

        if (HasTable(conn, "EMS_WORK_ORDER"))
        {
            EnsureSqliteIndex(
                conn,
                "EMS_WORK_ORDER",
                "IX_EMS_WO_EQUIPMENT_ISSUED",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_EMS_WO_EQUIPMENT_ISSUED
                    ON EMS_WORK_ORDER (EQUIPMENT_ID, ISSUED_AT DESC);
                """,
                new IndexKey("EQUIPMENT_ID", Descending: false),
                new IndexKey("ISSUED_AT", Descending: true));
            EnsureSqliteIndex(
                conn,
                "EMS_WORK_ORDER",
                "IX_EMS_WO_ISSUED",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_EMS_WO_ISSUED
                    ON EMS_WORK_ORDER (ISSUED_AT DESC, WO_ID DESC);
                """,
                new IndexKey("ISSUED_AT", Descending: true),
                new IndexKey("WO_ID", Descending: true));
        }

        if (HasTable(conn, "EMS_SPARE_PART_USAGE"))
        {
            EnsureSqliteIndex(
                conn,
                "EMS_SPARE_PART_USAGE",
                "IX_EMS_SPARE_USAGE_WO_TIME",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_EMS_SPARE_USAGE_WO_TIME
                    ON EMS_SPARE_PART_USAGE (WO_ID, USED_AT DESC)
                    WHERE WO_ID IS NOT NULL;
                """,
                new IndexKey("WO_ID", Descending: false),
                new IndexKey("USED_AT", Descending: true));
        }

        if (HasTable(conn, "RMS_RECIPE_EQUIPMENT_ASSIGNMENT"))
        {
            EnsureSqliteIndex(
                conn,
                "RMS_RECIPE_EQUIPMENT_ASSIGNMENT",
                "IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE
                    ON RMS_RECIPE_EQUIPMENT_ASSIGNMENT
                       (EQUIPMENT_ID, EFFECTIVE_FROM DESC, ASSIGNMENT_ID, EFFECTIVE_TO)
                    WHERE EQUIPMENT_ID IS NOT NULL;
                """,
                new IndexKey("EQUIPMENT_ID", Descending: false),
                new IndexKey("EFFECTIVE_FROM", Descending: true),
                new IndexKey("ASSIGNMENT_ID", Descending: false),
                new IndexKey("EFFECTIVE_TO", Descending: false));
            EnsureSqliteIndex(
                conn,
                "RMS_RECIPE_EQUIPMENT_ASSIGNMENT",
                "IX_RMS_RECIPE_ASSIGNMENT_CLASS_EFFECTIVE",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_RMS_RECIPE_ASSIGNMENT_CLASS_EFFECTIVE
                    ON RMS_RECIPE_EQUIPMENT_ASSIGNMENT
                       (EQUIPMENT_CLASS_ID, EFFECTIVE_FROM DESC, ASSIGNMENT_ID, EFFECTIVE_TO)
                    WHERE EQUIPMENT_CLASS_ID IS NOT NULL;
                """,
                new IndexKey("EQUIPMENT_CLASS_ID", Descending: false),
                new IndexKey("EFFECTIVE_FROM", Descending: true),
                new IndexKey("ASSIGNMENT_ID", Descending: false),
                new IndexKey("EFFECTIVE_TO", Descending: false));
        }

        if (HasTable(conn, "EST_TAKT_SUMMARY"))
        {
            EnsureSqliteIndex(
                conn,
                "EST_TAKT_SUMMARY",
                "IX_EST_TAKT_RECONCILIATION_DATE",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_EST_TAKT_RECONCILIATION_DATE
                    ON EST_TAKT_SUMMARY (TAKT_DATE, TAKT_SUMMARY_ID);
                """,
                new IndexKey("TAKT_DATE", Descending: false),
                new IndexKey("TAKT_SUMMARY_ID", Descending: false));
        }

        if (HasTable(conn, "EST_OEE_LOSS"))
        {
            EnsureSqliteIndex(
                conn,
                "EST_OEE_LOSS",
                "IX_EST_OEE_LOSS_RECONCILIATION_DATE",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_EST_OEE_LOSS_RECONCILIATION_DATE
                    ON EST_OEE_LOSS (OEE_DATE, LOSS_ID);
                """,
                new IndexKey("OEE_DATE", Descending: false),
                new IndexKey("LOSS_ID", Descending: false));
        }

        if (HasTable(conn, "EST_OEE_SUMMARY"))
        {
            EnsureSqliteIndex(
                conn,
                "EST_OEE_SUMMARY",
                "IX_EST_OEE_SUMMARY_RECONCILIATION_DATE",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_EST_OEE_SUMMARY_RECONCILIATION_DATE
                    ON EST_OEE_SUMMARY (OEE_DATE, OEE_ID);
                """,
                new IndexKey("OEE_DATE", Descending: false),
                new IndexKey("OEE_ID", Descending: false));
        }

        if (HasTable(conn, "POM_LOT"))
        {
            EnsureSqliteIndex(
                conn,
                "POM_LOT",
                "IX_POM_LOT_PLANT_CREATED",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_POM_LOT_PLANT_CREATED
                    ON POM_LOT (PLANT_ID, CREATED_AT DESC, LOT_ID);
                """,
                new IndexKey("PLANT_ID", Descending: false),
                new IndexKey("CREATED_AT", Descending: true),
                new IndexKey("LOT_ID", Descending: false));
            EnsureSqliteIndex(
                conn,
                "POM_LOT",
                "IX_POM_LOT_CREATED",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_POM_LOT_CREATED
                    ON POM_LOT (CREATED_AT DESC, LOT_ID);
                """,
                new IndexKey("CREATED_AT", Descending: true),
                new IndexKey("LOT_ID", Descending: false));
            EnsureSqliteIndex(
                conn,
                "POM_LOT",
                "IX_POM_LOT_HOLD_CREATED",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_POM_LOT_HOLD_CREATED
                    ON POM_LOT (CREATED_AT DESC, LOT_ID)
                    WHERE IS_HOLD = 'Y';
                """,
                new IndexKey("CREATED_AT", Descending: true),
                new IndexKey("LOT_ID", Descending: false));
            EnsureSqliteIndex(
                conn,
                "POM_LOT",
                "IX_POM_LOT_DEFECT_QTY",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_POM_LOT_DEFECT_QTY
                    ON POM_LOT (DEFECT_QTY DESC, CREATED_AT DESC, LOT_ID)
                    WHERE DEFECT_QTY > 0;
                """,
                new IndexKey("DEFECT_QTY", Descending: true),
                new IndexKey("CREATED_AT", Descending: true),
                new IndexKey("LOT_ID", Descending: false));
        }

        if (HasTable(conn, "POM_LOT_HISTORY"))
        {
            EnsureSqliteIndex(
                conn,
                "POM_LOT_HISTORY",
                "IX_POM_LOT_HISTORY_OEE_TRACK_OUT",
                unique: false,
                partial: true,
                """
                CREATE INDEX IX_POM_LOT_HISTORY_OEE_TRACK_OUT
                    ON POM_LOT_HISTORY (PLANT_ID, EQUIPMENT_ID, TRACK_OUT_TIME)
                    WHERE EXECUTION_ID = 'TrackOut' AND TRACK_OUT_TIME IS NOT NULL;
                """,
                new IndexKey("PLANT_ID", Descending: false),
                new IndexKey("EQUIPMENT_ID", Descending: false),
                new IndexKey("TRACK_OUT_TIME", Descending: false));
        }

        if (HasTable(conn, "POM_WORK_ORDER"))
        {
            EnsureSqliteIndex(
                conn,
                "POM_WORK_ORDER",
                "IX_POM_WORK_ORDER_PLAN_START",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_POM_WORK_ORDER_PLAN_START
                    ON POM_WORK_ORDER (PLAN_START_DATE DESC, WORK_ORDER_ID);
                """,
                new IndexKey("PLAN_START_DATE", Descending: true),
                new IndexKey("WORK_ORDER_ID", Descending: false));
        }

        if (HasTable(conn, "POM_LOT_DISPOSITION"))
        {
            EnsureSqliteIndex(
                conn,
                "POM_LOT_DISPOSITION",
                "IX_POM_LOT_DISPOSITION_PLANT_DATE",
                unique: false,
                partial: false,
                """
                CREATE INDEX IX_POM_LOT_DISPOSITION_PLANT_DATE
                    ON POM_LOT_DISPOSITION (PLANT_ID, DECIDED_AT DESC, DISPOSITION_ID DESC);
                """,
                new IndexKey("PLANT_ID", Descending: false),
                new IndexKey("DECIDED_AT", Descending: true),
                new IndexKey("DISPOSITION_ID", Descending: true));
        }

        if (HasIndex(conn, "IX_POM_LOT_MIXING_OUTPUT"))
            Exec(conn, "DROP INDEX IX_POM_LOT_MIXING_OUTPUT;");

        if (HasIndex(conn, "IX_EMS_WORK_ORDER_CHECK_RESULT_WO"))
            Exec(conn, "DROP INDEX IX_EMS_WORK_ORDER_CHECK_RESULT_WO;");
    }

    private static void EnsureSqliteIndex(
        SqliteConnection conn,
        string table,
        string index,
        bool unique,
        bool partial,
        string createSql,
        params IndexKey[] keys)
    {
        if (IndexMatches(conn, table, index, unique, partial, createSql, keys)) return;
        if (HasIndex(conn, index))
            Exec(conn, $"DROP INDEX [{index.Replace("]", "]]", StringComparison.Ordinal)}];");
        Exec(conn, createSql);
    }

    private static bool IndexMatches(
        SqliteConnection conn,
        string table,
        string index,
        bool unique,
        bool partial,
        string expectedCreateSql,
        IReadOnlyList<IndexKey> expectedKeys)
    {
        var found = false;
        using (var list = conn.CreateCommand())
        {
            list.CommandText = $"PRAGMA index_list([{table.Replace("]", "]]", StringComparison.Ordinal)}]);";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), index, StringComparison.OrdinalIgnoreCase)) continue;
                found = reader.GetInt64(2) == (unique ? 1 : 0)
                        && reader.GetInt64(4) == (partial ? 1 : 0);
                break;
            }
        }
        if (!found) return false;

        var actualKeys = new List<IndexKey>();
        using var info = conn.CreateCommand();
        info.CommandText = $"PRAGMA index_xinfo([{index.Replace("]", "]]", StringComparison.Ordinal)}]);";
        using var infoReader = info.ExecuteReader();
        while (infoReader.Read())
        {
            if (infoReader.GetInt64(5) != 1 || infoReader.IsDBNull(2)) continue;
            actualKeys.Add(new IndexKey(infoReader.GetString(2), infoReader.GetInt64(3) == 1));
        }
        if (!actualKeys.SequenceEqual(expectedKeys)) return false;
        if (!partial) return true;

        using var definition = conn.CreateCommand();
        definition.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @name;";
        definition.Parameters.AddWithValue("@name", index);
        var actualCreateSql = Convert.ToString(
            definition.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);

        return string.Equals(
            NormalizeIndexPredicate(actualCreateSql),
            NormalizeIndexPredicate(expectedCreateSql),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeIndexPredicate(string? createSql)
    {
        if (string.IsNullOrWhiteSpace(createSql)) return null;
        var match = Regex.Match(
            createSql,
            @"\bWHERE\b(?<predicate>.+?)\s*;?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!match.Success) return null;
        return Regex.Replace(
                match.Groups["predicate"].Value,
                @"\s+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim();
    }

    private static bool HasIndex(SqliteConnection conn, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name;";
        cmd.Parameters.AddWithValue("@name", index);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private readonly record struct IndexKey(string Name, bool Descending);

    /// <summary>
    /// V127 introduced immutable utility-meter configuration snapshots. The generic incremental pass deliberately
    /// skips migration DML, so upgraded SQLite databases need this explicit idempotent seed/backfill boundary.
    /// A blank reading PLANT_ID is the unambiguous legacy marker created by ADD COLUMN; already-versioned readings
    /// are never reinterpreted through the current master on later boots.
    /// </summary>
    private static void EnsureUtilityMeterConfigurationHistory(SqliteConnection conn)
    {
        if (!HasTable(conn, "EST_UTILITY_METER")
            || !HasTable(conn, "EST_UTILITY_READING")
            || !HasTable(conn, "EST_UTILITY_METER_EVENT")
            || !HasTable(conn, "EST_UTILITY_METER_CONFIG_HISTORY")
            || !HasColumn(conn, "EST_UTILITY_METER", "CONFIG_VERSION")
            || !HasColumn(conn, "EST_UTILITY_READING", "METER_CONFIG_VERSION")
            || !HasColumn(conn, "EST_UTILITY_READING", "PLANT_ID")
            || !HasColumn(conn, "EST_UTILITY_READING", "READING_MODE")
            || !HasColumn(conn, "EST_UTILITY_METER_EVENT", "METER_CONFIG_VERSION"))
            return;

        Exec(conn, """
            BEGIN IMMEDIATE;
            INSERT INTO EST_UTILITY_METER_CONFIG_HISTORY
                (HISTORY_ID, METER_ID, CONFIG_VERSION, METER_NAME, PLANT_ID, EQUIPMENT_ID,
                 UTILITY_TYPE, UNIT, FDC_PARAMETER_ID, READING_MODE, SCALE_FACTOR,
                 COST_PER_UNIT, CARBON_PER_UNIT, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
            SELECT M.METER_ID, M.METER_ID, 1, M.METER_NAME, M.PLANT_ID, M.EQUIPMENT_ID,
                   M.UTILITY_TYPE, M.UNIT, M.FDC_PARAMETER_ID, M.READING_MODE, M.SCALE_FACTOR,
                   M.COST_PER_UNIT, M.CARBON_PER_UNIT, M.IS_ACTIVE, M.UPDATED_BY, M.UPDATED_AT
            FROM EST_UTILITY_METER M
            WHERE M.CONFIG_VERSION = 1
              AND NOT EXISTS (
                  SELECT 1 FROM EST_UTILITY_METER_CONFIG_HISTORY H
                  WHERE H.METER_ID = M.METER_ID AND H.CONFIG_VERSION = 1
              );

            UPDATE EST_UTILITY_READING
               SET METER_CONFIG_VERSION = 1,
                   PLANT_ID = (SELECT H.PLANT_ID FROM EST_UTILITY_METER_CONFIG_HISTORY H
                               WHERE H.METER_ID = EST_UTILITY_READING.METER_ID
                                 AND H.CONFIG_VERSION = 1),
                   READING_MODE = (SELECT H.READING_MODE FROM EST_UTILITY_METER_CONFIG_HISTORY H
                                   WHERE H.METER_ID = EST_UTILITY_READING.METER_ID
                                     AND H.CONFIG_VERSION = 1),
                   COST_PER_UNIT = (SELECT H.COST_PER_UNIT FROM EST_UTILITY_METER_CONFIG_HISTORY H
                                    WHERE H.METER_ID = EST_UTILITY_READING.METER_ID
                                      AND H.CONFIG_VERSION = 1),
                   CARBON_PER_UNIT = (SELECT H.CARBON_PER_UNIT FROM EST_UTILITY_METER_CONFIG_HISTORY H
                                      WHERE H.METER_ID = EST_UTILITY_READING.METER_ID
                                        AND H.CONFIG_VERSION = 1)
             WHERE COALESCE(TRIM(PLANT_ID), '') = ''
               AND EXISTS (
                   SELECT 1 FROM EST_UTILITY_METER_CONFIG_HISTORY H
                   WHERE H.METER_ID = EST_UTILITY_READING.METER_ID AND H.CONFIG_VERSION = 1
               );
            COMMIT;
            """);

        using var verify = conn.CreateCommand();
        verify.CommandText = """
            SELECT OBJECT_TYPE, OBJECT_ID, CONFIG_VERSION
            FROM (
                SELECT 'METER' AS OBJECT_TYPE, M.METER_ID AS OBJECT_ID,
                       M.CONFIG_VERSION AS CONFIG_VERSION
                FROM EST_UTILITY_METER M
                LEFT JOIN EST_UTILITY_METER_CONFIG_HISTORY H
                  ON H.METER_ID = M.METER_ID AND H.CONFIG_VERSION = M.CONFIG_VERSION
                WHERE H.METER_ID IS NULL OR COALESCE(TRIM(H.PLANT_ID), '') = ''
                UNION ALL
                SELECT 'METER_HISTORY_GAP', M.METER_ID, M.CONFIG_VERSION
                FROM EST_UTILITY_METER M
                WHERE (SELECT COUNT(*)
                       FROM EST_UTILITY_METER_CONFIG_HISTORY H
                       WHERE H.METER_ID = M.METER_ID
                         AND H.CONFIG_VERSION BETWEEN 1 AND M.CONFIG_VERSION) <> M.CONFIG_VERSION
                UNION ALL
                SELECT 'READING', R.READING_ID, R.METER_CONFIG_VERSION
                FROM EST_UTILITY_READING R
                LEFT JOIN EST_UTILITY_METER_CONFIG_HISTORY H
                  ON H.METER_ID = R.METER_ID AND H.CONFIG_VERSION = R.METER_CONFIG_VERSION
                WHERE H.METER_ID IS NULL
                   OR COALESCE(TRIM(H.PLANT_ID), '') = ''
                   OR COALESCE(TRIM(R.PLANT_ID), '') = ''
                UNION ALL
                SELECT 'EVENT', E.EVENT_ID, E.METER_CONFIG_VERSION
                FROM EST_UTILITY_METER_EVENT E
                LEFT JOIN EST_UTILITY_METER_CONFIG_HISTORY H
                  ON H.METER_ID = E.METER_ID AND H.CONFIG_VERSION = E.METER_CONFIG_VERSION
                WHERE H.METER_ID IS NULL OR COALESCE(TRIM(H.PLANT_ID), '') = ''
            )
            LIMIT 1;
            """;
        using var invalid = verify.ExecuteReader();
        if (invalid.Read())
        {
            throw new InvalidOperationException(
                "V128 utility configuration reconciliation failed: " +
                $"objectType='{invalid.GetString(0)}', objectId='{invalid.GetString(1)}', " +
                $"configVersion={invalid.GetInt64(2)}. Repair the meter snapshot reference before startup.");
        }
    }

    private static void EnsureAppendOnlyEvidenceGuards(SqliteConnection conn)
    {
        if (HasTable(conn, "IVT_MATERIAL_CONSUMPTION_HISTORY"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_IVT_MATERIAL_CONSUMPTION_BU;
                DROP TRIGGER IF EXISTS TR_IVT_MATERIAL_CONSUMPTION_BD;
                DROP TRIGGER IF EXISTS TR_IVT_MATERIAL_CONSUMPTION_BR;
                CREATE TRIGGER TR_IVT_MATERIAL_CONSUMPTION_BU
                BEFORE UPDATE ON IVT_MATERIAL_CONSUMPTION_HISTORY
                BEGIN SELECT RAISE(ABORT, 'IVT_MATERIAL_CONSUMPTION_HISTORY is append-only'); END;
                CREATE TRIGGER TR_IVT_MATERIAL_CONSUMPTION_BD
                BEFORE DELETE ON IVT_MATERIAL_CONSUMPTION_HISTORY
                BEGIN SELECT RAISE(ABORT, 'IVT_MATERIAL_CONSUMPTION_HISTORY is append-only'); END;
                CREATE TRIGGER TR_IVT_MATERIAL_CONSUMPTION_BR
                BEFORE INSERT ON IVT_MATERIAL_CONSUMPTION_HISTORY
                WHEN EXISTS (
                    SELECT 1 FROM IVT_MATERIAL_CONSUMPTION_HISTORY H
                    WHERE H.CONSUMPTION_ID = NEW.CONSUMPTION_ID
                       OR H.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY
                       OR (NEW.REVERSAL_OF_ID IS NOT NULL
                           AND H.REVERSAL_OF_ID = NEW.REVERSAL_OF_ID)
                       OR (NEW.REVERSAL_OF_ID IS NULL AND H.REVERSAL_OF_ID IS NULL
                           AND H.SOURCE_SYSTEM = NEW.SOURCE_SYSTEM
                           AND H.SOURCE_EVENT_ID = NEW.SOURCE_EVENT_ID))
                BEGIN SELECT RAISE(ABORT, 'IVT_MATERIAL_CONSUMPTION_HISTORY replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "EMS_TOOL_SAVE_COMMAND"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_EMS_TOOL_SAVE_COMMAND_BU;
                DROP TRIGGER IF EXISTS TR_EMS_TOOL_SAVE_COMMAND_BD;
                DROP TRIGGER IF EXISTS TR_EMS_TOOL_SAVE_COMMAND_BR;
                CREATE TRIGGER TR_EMS_TOOL_SAVE_COMMAND_BU
                BEFORE UPDATE ON EMS_TOOL_SAVE_COMMAND
                BEGIN SELECT RAISE(ABORT, 'EMS_TOOL_SAVE_COMMAND is append-only'); END;
                CREATE TRIGGER TR_EMS_TOOL_SAVE_COMMAND_BD
                BEFORE DELETE ON EMS_TOOL_SAVE_COMMAND
                BEGIN SELECT RAISE(ABORT, 'EMS_TOOL_SAVE_COMMAND is append-only'); END;
                CREATE TRIGGER TR_EMS_TOOL_SAVE_COMMAND_BR
                BEFORE INSERT ON EMS_TOOL_SAVE_COMMAND
                WHEN EXISTS (
                    SELECT 1 FROM EMS_TOOL_SAVE_COMMAND C
                    WHERE C.COMMAND_ID = NEW.COMMAND_ID
                       OR C.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY)
                BEGIN SELECT RAISE(ABORT, 'EMS_TOOL_SAVE_COMMAND replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "EMS_SPARE_MASTER_COMMAND"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_EMS_SPARE_MASTER_COMMAND_BU;
                DROP TRIGGER IF EXISTS TR_EMS_SPARE_MASTER_COMMAND_BD;
                DROP TRIGGER IF EXISTS TR_EMS_SPARE_MASTER_COMMAND_BR;
                CREATE TRIGGER TR_EMS_SPARE_MASTER_COMMAND_BU
                BEFORE UPDATE ON EMS_SPARE_MASTER_COMMAND
                BEGIN SELECT RAISE(ABORT, 'EMS_SPARE_MASTER_COMMAND is append-only'); END;
                CREATE TRIGGER TR_EMS_SPARE_MASTER_COMMAND_BD
                BEFORE DELETE ON EMS_SPARE_MASTER_COMMAND
                BEGIN SELECT RAISE(ABORT, 'EMS_SPARE_MASTER_COMMAND is append-only'); END;
                CREATE TRIGGER TR_EMS_SPARE_MASTER_COMMAND_BR
                BEFORE INSERT ON EMS_SPARE_MASTER_COMMAND
                WHEN EXISTS (
                    SELECT 1 FROM EMS_SPARE_MASTER_COMMAND C
                    WHERE C.COMMAND_ID = NEW.COMMAND_ID
                       OR C.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY)
                BEGIN SELECT RAISE(ABORT, 'EMS_SPARE_MASTER_COMMAND replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "EMS_WORK_ORDER_CREATE_COMMAND"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_EMS_WORK_ORDER_CREATE_COMMAND_BU;
                DROP TRIGGER IF EXISTS TR_EMS_WORK_ORDER_CREATE_COMMAND_BD;
                DROP TRIGGER IF EXISTS TR_EMS_WORK_ORDER_CREATE_COMMAND_BR;
                CREATE TRIGGER TR_EMS_WORK_ORDER_CREATE_COMMAND_BU
                BEFORE UPDATE ON EMS_WORK_ORDER_CREATE_COMMAND
                BEGIN SELECT RAISE(ABORT, 'EMS_WORK_ORDER_CREATE_COMMAND is append-only'); END;
                CREATE TRIGGER TR_EMS_WORK_ORDER_CREATE_COMMAND_BD
                BEFORE DELETE ON EMS_WORK_ORDER_CREATE_COMMAND
                BEGIN SELECT RAISE(ABORT, 'EMS_WORK_ORDER_CREATE_COMMAND is append-only'); END;
                CREATE TRIGGER TR_EMS_WORK_ORDER_CREATE_COMMAND_BR
                BEFORE INSERT ON EMS_WORK_ORDER_CREATE_COMMAND
                WHEN EXISTS (
                    SELECT 1 FROM EMS_WORK_ORDER_CREATE_COMMAND C
                    WHERE C.COMMAND_ID = NEW.COMMAND_ID
                       OR C.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY)
                BEGIN SELECT RAISE(ABORT, 'EMS_WORK_ORDER_CREATE_COMMAND replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "RMS_RECIPE_APPROVAL_HISTORY"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_APPROVAL_HISTORY_BU;
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_APPROVAL_HISTORY_BD;
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_APPROVAL_HISTORY_BR;
                CREATE TRIGGER TR_RMS_RECIPE_APPROVAL_HISTORY_BU
                BEFORE UPDATE ON RMS_RECIPE_APPROVAL_HISTORY
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_APPROVAL_HISTORY is append-only'); END;
                CREATE TRIGGER TR_RMS_RECIPE_APPROVAL_HISTORY_BD
                BEFORE DELETE ON RMS_RECIPE_APPROVAL_HISTORY
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_APPROVAL_HISTORY is append-only'); END;
                CREATE TRIGGER TR_RMS_RECIPE_APPROVAL_HISTORY_BR
                BEFORE INSERT ON RMS_RECIPE_APPROVAL_HISTORY
                WHEN EXISTS (
                    SELECT 1 FROM RMS_RECIPE_APPROVAL_HISTORY H
                    WHERE H.HISTORY_ID = NEW.HISTORY_ID
                       OR H.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY)
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_APPROVAL_HISTORY replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "RMS_RECIPE_COMMAND"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_COMMAND_BU;
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_COMMAND_BD;
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_COMMAND_BR;
                CREATE TRIGGER TR_RMS_RECIPE_COMMAND_BU
                BEFORE UPDATE ON RMS_RECIPE_COMMAND
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_COMMAND is append-only'); END;
                CREATE TRIGGER TR_RMS_RECIPE_COMMAND_BD
                BEFORE DELETE ON RMS_RECIPE_COMMAND
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_COMMAND is append-only'); END;
                CREATE TRIGGER TR_RMS_RECIPE_COMMAND_BR
                BEFORE INSERT ON RMS_RECIPE_COMMAND
                WHEN EXISTS (
                    SELECT 1 FROM RMS_RECIPE_COMMAND C
                    WHERE C.COMMAND_ID = NEW.COMMAND_ID
                       OR C.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY)
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_COMMAND replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "RMS_RECIPE_PARAM_COMMAND"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_PARAM_COMMAND_BU;
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_PARAM_COMMAND_BD;
                DROP TRIGGER IF EXISTS TR_RMS_RECIPE_PARAM_COMMAND_BR;
                CREATE TRIGGER TR_RMS_RECIPE_PARAM_COMMAND_BU
                BEFORE UPDATE ON RMS_RECIPE_PARAM_COMMAND
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_PARAM_COMMAND is append-only'); END;
                CREATE TRIGGER TR_RMS_RECIPE_PARAM_COMMAND_BD
                BEFORE DELETE ON RMS_RECIPE_PARAM_COMMAND
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_PARAM_COMMAND is append-only'); END;
                CREATE TRIGGER TR_RMS_RECIPE_PARAM_COMMAND_BR
                BEFORE INSERT ON RMS_RECIPE_PARAM_COMMAND
                WHEN EXISTS (
                    SELECT 1 FROM RMS_RECIPE_PARAM_COMMAND C
                    WHERE C.COMMAND_ID = NEW.COMMAND_ID
                       OR C.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY)
                BEGIN SELECT RAISE(ABORT, 'RMS_RECIPE_PARAM_COMMAND replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "EST_UTILITY_METER_EVENT"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_EST_UTILITY_METER_EVENT_BU;
                DROP TRIGGER IF EXISTS TR_EST_UTILITY_METER_EVENT_BD;
                DROP TRIGGER IF EXISTS TR_EST_UTILITY_METER_EVENT_BR;
                CREATE TRIGGER TR_EST_UTILITY_METER_EVENT_BU
                BEFORE UPDATE ON EST_UTILITY_METER_EVENT
                BEGIN SELECT RAISE(ABORT, 'EST_UTILITY_METER_EVENT is append-only'); END;
                CREATE TRIGGER TR_EST_UTILITY_METER_EVENT_BD
                BEFORE DELETE ON EST_UTILITY_METER_EVENT
                BEGIN SELECT RAISE(ABORT, 'EST_UTILITY_METER_EVENT is append-only'); END;
                CREATE TRIGGER TR_EST_UTILITY_METER_EVENT_BR
                BEFORE INSERT ON EST_UTILITY_METER_EVENT
                WHEN EXISTS (
                    SELECT 1 FROM EST_UTILITY_METER_EVENT E
                    WHERE E.EVENT_ID = NEW.EVENT_ID
                       OR E.IDEMPOTENCY_KEY = NEW.IDEMPOTENCY_KEY)
                BEGIN SELECT RAISE(ABORT, 'EST_UTILITY_METER_EVENT replacement is forbidden'); END;
                """);
        }

        if (HasTable(conn, "EST_UTILITY_METER_CONFIG_HISTORY"))
        {
            Exec(conn, """
                DROP TRIGGER IF EXISTS TR_EST_UTILITY_CONFIG_HISTORY_BU;
                DROP TRIGGER IF EXISTS TR_EST_UTILITY_CONFIG_HISTORY_BD;
                DROP TRIGGER IF EXISTS TR_EST_UTILITY_CONFIG_HISTORY_BR;
                CREATE TRIGGER TR_EST_UTILITY_CONFIG_HISTORY_BU
                BEFORE UPDATE ON EST_UTILITY_METER_CONFIG_HISTORY
                BEGIN SELECT RAISE(ABORT, 'EST_UTILITY_METER_CONFIG_HISTORY is append-only'); END;
                CREATE TRIGGER TR_EST_UTILITY_CONFIG_HISTORY_BD
                BEFORE DELETE ON EST_UTILITY_METER_CONFIG_HISTORY
                BEGIN SELECT RAISE(ABORT, 'EST_UTILITY_METER_CONFIG_HISTORY is append-only'); END;
                CREATE TRIGGER TR_EST_UTILITY_CONFIG_HISTORY_BR
                BEFORE INSERT ON EST_UTILITY_METER_CONFIG_HISTORY
                WHEN EXISTS (
                    SELECT 1 FROM EST_UTILITY_METER_CONFIG_HISTORY H
                    WHERE (H.METER_ID = NEW.METER_ID AND H.CONFIG_VERSION = NEW.CONFIG_VERSION)
                       OR H.HISTORY_ID = NEW.HISTORY_ID)
                BEGIN SELECT RAISE(ABORT, 'EST_UTILITY_METER_CONFIG_HISTORY replacement is forbidden'); END;
                """);
        }
    }

    private static void EnsureEmsSparePartManagementColumns(SqliteConnection conn)
    {
        foreach (var table in new[]
                 {
                     "EMS_SPARE_PART_STOCK_POLICY",
                     "EMS_SPARE_PART_SUPPLIER",
                     "EMS_EQUIPMENT_PART_BOM",
                 })
        {
            if (!HasTable(conn, table)) continue;
            if (!HasColumn(conn, table, "VERSION_NO"))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN VERSION_NO INTEGER NOT NULL DEFAULT 1;");
            if (!HasColumn(conn, table, "LAST_IDEMPOTENCY_KEY"))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN LAST_IDEMPOTENCY_KEY TEXT NOT NULL DEFAULT ''; ");
            if (!HasColumn(conn, table, "LAST_REQUEST_HASH"))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN LAST_REQUEST_HASH TEXT NOT NULL DEFAULT ''; ");
        }
    }

    private static void EnsureEmsSparePartManagementIntegrity(SqliteConnection conn)
    {
        EnsureEmsSparePartManagementColumns(conn);

        if (HasTable(conn, "EMS_SPARE_PART_STOCK_POLICY"))
        {
            const string policyChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'EMS spare-part stock policy has invalid quantities or version')
                    WHERE NEW.SAFETY_STOCK < 0 OR NEW.REORDER_POINT < 0
                       OR NEW.TARGET_STOCK < NEW.SAFETY_STOCK
                       OR NEW.TARGET_STOCK < NEW.REORDER_POINT
                       OR NEW.RESERVED_QTY < 0 OR NEW.AVG_DAILY_USAGE < 0
                       OR (NEW.SERVICE_LEVEL IS NOT NULL
                           AND (NEW.SERVICE_LEVEL < 0 OR NEW.SERVICE_LEVEL > 1))
                       OR (NEW.REVIEW_CYCLE_DAYS IS NOT NULL AND NEW.REVIEW_CYCLE_DAYS <= 0)
                       OR NEW.VERSION_NO <= 0;
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_SPARE_STOCK_POLICY_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_SPARE_STOCK_POLICY_BU;");
            Exec(conn, $"CREATE TRIGGER TR_EMS_SPARE_STOCK_POLICY_BI BEFORE INSERT ON EMS_SPARE_PART_STOCK_POLICY {policyChecks}");
            Exec(conn, $"CREATE TRIGGER TR_EMS_SPARE_STOCK_POLICY_BU BEFORE UPDATE ON EMS_SPARE_PART_STOCK_POLICY {policyChecks}");
        }

        if (HasTable(conn, "EMS_SPARE_PART_SUPPLIER"))
        {
            const string supplierChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'EMS spare-part supplier has invalid commercial terms or version')
                    WHERE NEW.LEAD_TIME_DAYS < 0
                       OR (NEW.MOQ IS NOT NULL AND NEW.MOQ <= 0)
                       OR (NEW.UNIT_PRICE IS NOT NULL AND NEW.UNIT_PRICE < 0)
                       OR ((NEW.UNIT_PRICE IS NULL) <> (NEW.CURRENCY IS NULL))
                       OR (NEW.IS_PRIMARY = 1 AND NEW.IS_ACTIVE <> 1)
                       OR NEW.VERSION_NO <= 0;
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_SPARE_SUPPLIER_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_SPARE_SUPPLIER_BU;");
            Exec(conn, $"CREATE TRIGGER TR_EMS_SPARE_SUPPLIER_BI BEFORE INSERT ON EMS_SPARE_PART_SUPPLIER {supplierChecks}");
            Exec(conn, $"CREATE TRIGGER TR_EMS_SPARE_SUPPLIER_BU BEFORE UPDATE ON EMS_SPARE_PART_SUPPLIER {supplierChecks}");
        }

        if (HasTable(conn, "EMS_EQUIPMENT_PART_BOM"))
        {
            const string bomChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'EMS equipment spare-part BOM has invalid scope, quantity, cycle, or version')
                    WHERE ((NEW.EQUIPMENT_ID IS NULL) = (NEW.EQUIPMENT_CLASS_ID IS NULL))
                       OR NEW.QUANTITY_PER <= 0
                       OR (NEW.CRITICALITY IS NOT NULL
                           AND NEW.CRITICALITY NOT IN ('Critical', 'High', 'Medium', 'Low'))
                       OR (NEW.REPLACEMENT_CYCLE_DAYS IS NOT NULL
                           AND NEW.REPLACEMENT_CYCLE_DAYS <= 0)
                       OR (NEW.REPLACEMENT_CYCLE_COUNT IS NOT NULL
                           AND NEW.REPLACEMENT_CYCLE_COUNT <= 0)
                       OR NEW.VERSION_NO <= 0;
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_EQUIPMENT_PART_BOM_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_EQUIPMENT_PART_BOM_BU;");
            Exec(conn, $"CREATE TRIGGER TR_EMS_EQUIPMENT_PART_BOM_BI BEFORE INSERT ON EMS_EQUIPMENT_PART_BOM {bomChecks}");
            Exec(conn, $"CREATE TRIGGER TR_EMS_EQUIPMENT_PART_BOM_BU BEFORE UPDATE ON EMS_EQUIPMENT_PART_BOM {bomChecks}");
        }
    }

    /// <summary>
    /// SQLite legacy builds could store an EMS maintenance-plan id in EMS_WORK_ORDER.PLAN_ID because
    /// foreign keys were intentionally disabled. V115 gives that relationship its own column. Only an
    /// unambiguous EMS-only identifier is migrated; POM-only, ambiguous, and orphan values remain in the
    /// legacy column for explicit operator review instead of being silently reinterpreted.
    /// </summary>
    private static void EnsureEmsMaintenancePlanBoundary(SqliteConnection conn)
    {
        if (!HasTable(conn, "EMS_WORK_ORDER")
            || !HasTable(conn, "EMS_MAINTENANCE_PLAN")
            || !HasTable(conn, "POM_PRODUCTION_PLAN")
            || !HasColumn(conn, "EMS_WORK_ORDER", "MAINTENANCE_PLAN_ID"))
            return;

        Exec(conn, """
            UPDATE EMS_WORK_ORDER
            SET MAINTENANCE_PLAN_ID = PLAN_ID,
                UPDATED_BY = 'SYSTEM_MIGRATION',
                UPDATED_AT = CURRENT_TIMESTAMP
            WHERE MAINTENANCE_PLAN_ID IS NULL
              AND PLAN_ID IS NOT NULL
              AND EXISTS (
                  SELECT 1 FROM EMS_MAINTENANCE_PLAN E WHERE E.PLAN_ID = EMS_WORK_ORDER.PLAN_ID)
              AND NOT EXISTS (
                  SELECT 1 FROM POM_PRODUCTION_PLAN P WHERE P.PLAN_ID = EMS_WORK_ORDER.PLAN_ID);
            """);
    }

    /// <summary>
    /// V115 adds SQL Server FK/CHECK constraints with ALTER TABLE ... ADD CONSTRAINT. SQLite cannot
    /// add those constraints after table creation, and this bootstrap intentionally disables native
    /// FK enforcement, so equivalent write boundaries are recreated as triggers. Legacy spare-part
    /// rows remain soft references: execution checks only apply once IDEMPOTENCY_KEY is populated.
    /// </summary>
    private static void EnsureEmsMaintenanceExecutionIntegrity(SqliteConnection conn)
    {
        if (HasTable(conn, "EMS_WORK_ORDER")
            && HasTable(conn, "EMS_MAINTENANCE_PLAN")
            && HasColumn(conn, "EMS_WORK_ORDER", "MAINTENANCE_PLAN_ID"))
        {
            const string maintenancePlanChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'EMS_WORK_ORDER maintenance plan scope/type mismatch')
                    WHERE NEW.MAINTENANCE_PLAN_ID IS NOT NULL
                      AND NOT EXISTS (
                        SELECT 1 FROM EMS_MAINTENANCE_PLAN P
                        WHERE P.PLAN_ID = NEW.MAINTENANCE_PLAN_ID
                          AND P.EQUIPMENT_ID = NEW.EQUIPMENT_ID
                          AND (CASE WHEN P.PLAN_TYPE = 'CM' THEN 'BM' ELSE P.PLAN_TYPE END)
                              = (CASE WHEN NEW.WO_TYPE = 'CM' THEN 'BM' ELSE NEW.WO_TYPE END)
                      );
                END;
                """;

            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_WORK_ORDER_MAINT_PLAN_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_WORK_ORDER_MAINT_PLAN_BU;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_MAINTENANCE_PLAN_WORK_ORDER_BU;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_MAINTENANCE_PLAN_WORK_ORDER_BD;");
            Exec(conn, $"CREATE TRIGGER TR_EMS_WORK_ORDER_MAINT_PLAN_BI BEFORE INSERT ON EMS_WORK_ORDER {maintenancePlanChecks}");
            Exec(conn, $"CREATE TRIGGER TR_EMS_WORK_ORDER_MAINT_PLAN_BU BEFORE UPDATE OF MAINTENANCE_PLAN_ID,EQUIPMENT_ID,WO_TYPE ON EMS_WORK_ORDER {maintenancePlanChecks}");
            Exec(conn, """
                CREATE TRIGGER TR_EMS_MAINTENANCE_PLAN_WORK_ORDER_BU
                BEFORE UPDATE OF PLAN_ID,EQUIPMENT_ID,PLAN_TYPE ON EMS_MAINTENANCE_PLAN
                WHEN EXISTS (
                  SELECT 1 FROM EMS_WORK_ORDER W
                  WHERE W.MAINTENANCE_PLAN_ID = OLD.PLAN_ID
                    AND (NEW.PLAN_ID <> OLD.PLAN_ID
                         OR NEW.EQUIPMENT_ID <> W.EQUIPMENT_ID
                         OR (CASE WHEN NEW.PLAN_TYPE = 'CM' THEN 'BM' ELSE NEW.PLAN_TYPE END)
                            <> (CASE WHEN W.WO_TYPE = 'CM' THEN 'BM' ELSE W.WO_TYPE END))
                )
                BEGIN
                  SELECT RAISE(ABORT, 'EMS_MAINTENANCE_PLAN has incompatible child EMS_WORK_ORDER rows');
                END;
                """);
            Exec(conn, """
                CREATE TRIGGER TR_EMS_MAINTENANCE_PLAN_WORK_ORDER_BD
                BEFORE DELETE ON EMS_MAINTENANCE_PLAN
                WHEN EXISTS (
                  SELECT 1 FROM EMS_WORK_ORDER W WHERE W.MAINTENANCE_PLAN_ID = OLD.PLAN_ID
                )
                BEGIN
                  SELECT RAISE(ABORT, 'EMS_MAINTENANCE_PLAN has child EMS_WORK_ORDER rows');
                END;
                """);
        }

        if (!HasTable(conn, "EMS_SPARE_PART_INOUT")
            || !HasTable(conn, "EMS_WORK_ORDER")
            || !HasColumn(conn, "EMS_SPARE_PART_INOUT", "IDEMPOTENCY_KEY")
            || !HasColumn(conn, "EMS_SPARE_PART_INOUT", "BALANCE_BEFORE")
            || !HasColumn(conn, "EMS_SPARE_PART_INOUT", "BALANCE_AFTER")
            || !HasColumn(conn, "EMS_SPARE_PART_INOUT", "CLIENT_CHANNEL")
            || !HasColumn(conn, "EMS_SPARE_PART_INOUT", "WO_ID"))
            return;

        const string sparePartExecutionChecks = """
            BEGIN
              SELECT RAISE(ABORT, 'EMS_SPARE_PART_INOUT has invalid execution evidence')
                WHERE NEW.IDEMPOTENCY_KEY IS NOT NULL
                  AND (NEW.QUANTITY IS NULL
                       OR (NEW.TRANSACTION_TYPE = 'Opening'
                           AND (NEW.QUANTITY < 0
                                OR NEW.BALANCE_BEFORE <> 0
                                OR NEW.BALANCE_AFTER <> NEW.QUANTITY
                                OR NEW.WO_ID IS NOT NULL
                                OR NEW.EQUIPMENT_ID IS NOT NULL))
                       OR (NEW.TRANSACTION_TYPE <> 'Opening' AND NEW.QUANTITY <= 0)
                       OR NEW.BALANCE_BEFORE IS NULL OR NEW.BALANCE_BEFORE < 0
                       OR NEW.BALANCE_AFTER IS NULL OR NEW.BALANCE_AFTER < 0
                       OR COALESCE(TRIM(NEW.PROCESSED_BY), '') = ''
                       OR NEW.CLIENT_CHANNEL IS NULL
                       OR NEW.CLIENT_CHANNEL NOT IN ('MES', 'MOBILE', 'POP')
                       OR (NEW.WO_ID IS NOT NULL AND NOT EXISTS (
                         SELECT 1 FROM EMS_WORK_ORDER W WHERE W.WO_ID = NEW.WO_ID
                       )));
            END;
            """;

        Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_SPARE_PART_INOUT_EXECUTION_BI;");
        Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_SPARE_PART_INOUT_EXECUTION_BU;");
        Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_WORK_ORDER_SPARE_PART_INOUT_BU;");
        Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_WORK_ORDER_SPARE_PART_INOUT_BD;");
        Exec(conn, $"CREATE TRIGGER TR_EMS_SPARE_PART_INOUT_EXECUTION_BI BEFORE INSERT ON EMS_SPARE_PART_INOUT {sparePartExecutionChecks}");
        Exec(conn, $"CREATE TRIGGER TR_EMS_SPARE_PART_INOUT_EXECUTION_BU BEFORE UPDATE ON EMS_SPARE_PART_INOUT {sparePartExecutionChecks}");
        Exec(conn, """
            CREATE TRIGGER TR_EMS_WORK_ORDER_SPARE_PART_INOUT_BU
            BEFORE UPDATE OF WO_ID ON EMS_WORK_ORDER
            WHEN NEW.WO_ID <> OLD.WO_ID
             AND EXISTS (
               SELECT 1 FROM EMS_SPARE_PART_INOUT I WHERE I.WO_ID = OLD.WO_ID
             )
            BEGIN
              SELECT RAISE(ABORT, 'EMS_WORK_ORDER has child EMS_SPARE_PART_INOUT rows');
            END;
            """);
        Exec(conn, """
            CREATE TRIGGER TR_EMS_WORK_ORDER_SPARE_PART_INOUT_BD
            BEFORE DELETE ON EMS_WORK_ORDER
            WHEN EXISTS (
              SELECT 1 FROM EMS_SPARE_PART_INOUT I WHERE I.WO_ID = OLD.WO_ID
            )
            BEGIN
              SELECT RAISE(ABORT, 'EMS_WORK_ORDER has child EMS_SPARE_PART_INOUT rows');
            END;
            """);
    }

    /// <summary>
    /// V124/V125 use SQL Server triggers for append-only equipment history and EMS cross-row guards.
    /// SQLite receives equivalent triggers here on both fresh and incremental startup paths. The
    /// insert-collision guard also blocks INSERT OR REPLACE when recursive_triggers is disabled.
    /// </summary>
    private static void EnsureEmsMdmMasterIntegrity(SqliteConnection conn)
    {
        if (HasTable(conn, "MDM_EQUIPMENT_CHANGE_HISTORY"))
        {
            Exec(conn, "DROP TRIGGER IF EXISTS TR_MDM_EQUIPMENT_CHANGE_APPEND_ONLY_BU;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_MDM_EQUIPMENT_CHANGE_APPEND_ONLY_BD;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_MDM_EQUIPMENT_CHANGE_APPEND_ONLY_BR;");
            Exec(conn, """
                CREATE TRIGGER TR_MDM_EQUIPMENT_CHANGE_APPEND_ONLY_BU
                BEFORE UPDATE ON MDM_EQUIPMENT_CHANGE_HISTORY
                BEGIN
                  SELECT RAISE(ABORT, 'MDM_EQUIPMENT_CHANGE_HISTORY is append-only');
                END;
                """);
            Exec(conn, """
                CREATE TRIGGER TR_MDM_EQUIPMENT_CHANGE_APPEND_ONLY_BD
                BEFORE DELETE ON MDM_EQUIPMENT_CHANGE_HISTORY
                BEGIN
                  SELECT RAISE(ABORT, 'MDM_EQUIPMENT_CHANGE_HISTORY is append-only');
                END;
                """);
            Exec(conn, """
                CREATE TRIGGER TR_MDM_EQUIPMENT_CHANGE_APPEND_ONLY_BR
                BEFORE INSERT ON MDM_EQUIPMENT_CHANGE_HISTORY
                WHEN EXISTS (
                  SELECT 1 FROM MDM_EQUIPMENT_CHANGE_HISTORY H
                  WHERE H.CHANGE_ID = NEW.CHANGE_ID
                )
                BEGIN
                  SELECT RAISE(ABORT, 'MDM_EQUIPMENT_CHANGE_HISTORY replacement is forbidden');
                END;
                """);
        }

        if (HasTable(conn, "EMS_TOOL") && HasTable(conn, "EMS_TOOL_MOUNT_HISTORY"))
        {
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_TOOL_MOUNTED_CLASS_BU;");
            Exec(conn, """
                CREATE TRIGGER TR_EMS_TOOL_MOUNTED_CLASS_BU
                BEFORE UPDATE OF EQUIPMENT_CLASS_ID ON EMS_TOOL
                WHEN NEW.EQUIPMENT_CLASS_ID IS NOT OLD.EQUIPMENT_CLASS_ID
                 AND EXISTS (
                   SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY M
                   WHERE M.TOOL_ID = OLD.TOOL_ID AND M.UNMOUNTED_AT IS NULL
                 )
                BEGIN
                  SELECT RAISE(ABORT, 'Mounted tool equipment class is immutable');
                END;
                """);
        }

        if (HasTable(conn, "EMS_TOOL_USAGE_HISTORY")
            && HasTable(conn, "EMS_TOOL_MOUNT_HISTORY"))
        {
            const string usageMountTimeCheck = """
                BEGIN
                  SELECT RAISE(ABORT, 'Tool usage cannot precede its mount')
                    WHERE NEW.MOUNT_ID IS NOT NULL
                      AND EXISTS (
                        SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY M
                        WHERE M.MOUNT_ID = NEW.MOUNT_ID
                          AND NEW.USED_AT < M.MOUNTED_AT
                      );
                  SELECT RAISE(ABORT, 'Tool usage cannot follow its unmount')
                    WHERE NEW.MOUNT_ID IS NOT NULL
                      AND EXISTS (
                        SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY M
                        WHERE M.MOUNT_ID = NEW.MOUNT_ID
                          AND M.UNMOUNTED_AT IS NOT NULL
                          AND NEW.USED_AT > M.UNMOUNTED_AT
                      );
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_TOOL_USAGE_MOUNT_TIME_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_TOOL_USAGE_MOUNT_TIME_BU;");
            Exec(conn, $"CREATE TRIGGER TR_EMS_TOOL_USAGE_MOUNT_TIME_BI BEFORE INSERT ON EMS_TOOL_USAGE_HISTORY {usageMountTimeCheck}");
            Exec(conn, $"CREATE TRIGGER TR_EMS_TOOL_USAGE_MOUNT_TIME_BU BEFORE UPDATE OF MOUNT_ID,USED_AT ON EMS_TOOL_USAGE_HISTORY {usageMountTimeCheck}");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_EMS_TOOL_UNMOUNT_USAGE_TIME_BU;");
            Exec(conn, """
                CREATE TRIGGER TR_EMS_TOOL_UNMOUNT_USAGE_TIME_BU
                BEFORE UPDATE OF UNMOUNTED_AT ON EMS_TOOL_MOUNT_HISTORY
                WHEN NEW.UNMOUNTED_AT IS NOT NULL
                 AND EXISTS (
                   SELECT 1 FROM EMS_TOOL_USAGE_HISTORY U
                   WHERE U.MOUNT_ID = NEW.MOUNT_ID AND U.USED_AT > NEW.UNMOUNTED_AT
                 )
                BEGIN
                  SELECT RAISE(ABORT, 'Tool unmount cannot precede recorded usage');
                END;
                """);
        }
    }

    private static void EnsureReadQueryRoleDefaults(SqliteConnection conn)
    {
        if (!HasTable(conn, "SYS_ROLE")) return;

        Exec(conn, """
            UPDATE SYS_ROLE
            SET PERMISSIONS = 'fdc:control|fdc:read|mdm:read|est:read|pom:read|pom:execute|pom:routing.request|rms:read',
                UPDATED_BY = 'SYSTEM',
                UPDATED_AT = CURRENT_TIMESTAMP
            WHERE ROLE_ID = 'OPERATOR'
              AND IS_DELETED = 0
              AND PERMISSIONS IN (
                  '',
                  'fdc:control|fdc:read',
                  'fdc:control|fdc:read|pom:execute',
                  'fdc:control|fdc:read|mdm:read|est:read|pom:read|pom:execute',
                  'fdc:control|fdc:read|mdm:read|est:read|pom:read|pom:execute|pom:routing.request'
              );

            INSERT INTO SYS_ROLE
                (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT
                'MAINTENANCE', 'Maintenance', 'Equipment maintenance worker role',
                'fdc:read|mdm:read|ems:read|ems:manage|est:read|pom:read|rms:read', 0,
                'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP
            WHERE NOT EXISTS (SELECT 1 FROM SYS_ROLE WHERE ROLE_ID = 'MAINTENANCE');
            """);
    }

    /// <summary>
    /// V117의 데이터 보정과 NOT NULL/CHECK 경계를 SQLite에도 적용한다. 증분 경로는 일반 UPDATE와
    /// ALTER ... ADD CONSTRAINT를 실행하지 않으므로, 기존 행을 한 번 분류하고 동등한 쓰기 경계를
    /// 트리거로 만든다. BEGIN IMMEDIATE로 보정과 경계 설치 사이의 동시 쓰기 틈을 막는다.
    /// </summary>
    private static void EnsureEstEquipmentOutputScope(SqliteConnection conn)
    {
        if (!HasTable(conn, "EST_EQUIPMENT_OUTPUT_EVENT")
            || !HasColumn(conn, "EST_EQUIPMENT_OUTPUT_EVENT", "IS_LOT_OUTPUT"))
            return;

        Exec(conn, """
            BEGIN IMMEDIATE;

            UPDATE EST_EQUIPMENT_OUTPUT_EVENT
               SET IS_LOT_OUTPUT = CASE WHEN PROCESS_LOT_ID IS NULL THEN 0 ELSE 1 END
             WHERE IS_LOT_OUTPUT IS NULL;

            DROP TRIGGER IF EXISTS TR_EST_EQUIPMENT_OUTPUT_SCOPE_BI;
            DROP TRIGGER IF EXISTS TR_EST_EQUIPMENT_OUTPUT_SCOPE_BU;

            CREATE TRIGGER TR_EST_EQUIPMENT_OUTPUT_SCOPE_BI
            BEFORE INSERT ON EST_EQUIPMENT_OUTPUT_EVENT
            BEGIN
              SELECT RAISE(ABORT, 'EST_EQUIPMENT_OUTPUT_EVENT has invalid output scope')
                WHERE NEW.IS_LOT_OUTPUT IS NULL
                   OR NEW.IS_LOT_OUTPUT NOT IN (0, 1)
                   OR (NEW.IS_LOT_OUTPUT = 1 AND NEW.PROCESS_LOT_ID IS NULL)
                   OR (UPPER(TRIM(NEW.OUTPUT_TYPE)) = 'CARRIERCLEANED'
                       AND (COALESCE(TRIM(NEW.CARRIER_ID), '') = ''
                            OR NEW.IS_LOT_OUTPUT <> 0
                            OR NEW.PROCESS_LOT_ID IS NOT NULL));
              SELECT RAISE(ABORT, 'EST_EQUIPMENT_OUTPUT_EVENT has invalid equipment/plant master scope')
                WHERE NOT EXISTS (
                  SELECT 1 FROM MDM_EQUIPMENT E
                  WHERE E.EQUIPMENT_ID = NEW.EQUIPMENT_ID
                    AND E.PLANT_ID = NEW.PLANT_ID
                    AND UPPER(E.VALID_STATE) = 'VALID'
                );
              SELECT RAISE(ABORT, 'EST_EQUIPMENT_OUTPUT_EVENT has unknown carrier')
                WHERE NEW.CARRIER_ID IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM MDM_CARRIER C WHERE C.CARRIER_ID = NEW.CARRIER_ID
                  );
            END;

            CREATE TRIGGER TR_EST_EQUIPMENT_OUTPUT_SCOPE_BU
            BEFORE UPDATE OF IS_LOT_OUTPUT, PROCESS_LOT_ID, PLANT_ID, EQUIPMENT_ID,
                             OUTPUT_TYPE, CARRIER_ID ON EST_EQUIPMENT_OUTPUT_EVENT
            BEGIN
              SELECT RAISE(ABORT, 'EST_EQUIPMENT_OUTPUT_EVENT has invalid output scope')
                WHERE NEW.IS_LOT_OUTPUT IS NULL
                   OR NEW.IS_LOT_OUTPUT NOT IN (0, 1)
                   OR (NEW.IS_LOT_OUTPUT = 1 AND NEW.PROCESS_LOT_ID IS NULL)
                   OR (UPPER(TRIM(NEW.OUTPUT_TYPE)) = 'CARRIERCLEANED'
                       AND (COALESCE(TRIM(NEW.CARRIER_ID), '') = ''
                            OR NEW.IS_LOT_OUTPUT <> 0
                            OR NEW.PROCESS_LOT_ID IS NOT NULL));
              SELECT RAISE(ABORT, 'EST_EQUIPMENT_OUTPUT_EVENT has invalid equipment/plant master scope')
                WHERE NOT EXISTS (
                  SELECT 1 FROM MDM_EQUIPMENT E
                  WHERE E.EQUIPMENT_ID = NEW.EQUIPMENT_ID
                    AND E.PLANT_ID = NEW.PLANT_ID
                    AND UPPER(E.VALID_STATE) = 'VALID'
                );
              SELECT RAISE(ABORT, 'EST_EQUIPMENT_OUTPUT_EVENT has unknown carrier')
                WHERE NEW.CARRIER_ID IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM MDM_CARRIER C WHERE C.CARRIER_ID = NEW.CARRIER_ID
                  );
            END;

            COMMIT;
            """);
    }

    private static bool HasUserTables(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        return count > 0;
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{table.Replace("]", "]]", StringComparison.Ordinal)}]);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool HasTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static void EnsureQmsInspectionIntegrity(SqliteConnection conn)
    {
        if (!HasTable(conn, "QMS_INSPECTION") || !HasTable(conn, "QMS_INSPECTION_RESULT") ||
            !HasTable(conn, "QMS_INSPECTION_SPEC") || !HasTable(conn, "POM_LOT") ||
            !HasTable(conn, "IVT_MATERIAL_LOT") ||
            !HasTable(conn, "MDM_EQUIPMENT") ||
            !HasColumn(conn, "QMS_INSPECTION_RESULT", "INSPECTION_ID"))
            return;

        Exec(conn, "UPDATE QMS_INSPECTION_RESULT SET INSPECTION_ID = RESULT_ID WHERE INSPECTION_ID IS NULL OR TRIM(INSPECTION_ID) = ''; ");
        Exec(conn, "UPDATE QMS_INSPECTION_SPEC SET MEASURE_TYPE = 'Variable' WHERE MEASURE_TYPE = 'Numeric';");
        Exec(conn, """
            INSERT INTO QMS_INSPECTION
                (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, SPEC_ID,
                 INSPECTED_AT, INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
                 REMARK, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT R.RESULT_ID, 'Process', R.LOT_ID, R.EQUIPMENT_ID, R.SPEC_ID,
                   R.INSPECTED_AT, R.INSPECTOR_ID,
                   CASE WHEN R.IS_PASS = 1 THEN 'Pass' ELSE 'Fail' END,
                   1, CASE WHEN R.IS_PASS = 1 THEN 0 ELSE 1 END, 1,
                   R.REMARK, R.CREATED_BY, R.CREATED_AT, R.UPDATED_BY, R.UPDATED_AT
            FROM QMS_INSPECTION_RESULT R
            WHERE NOT EXISTS (
                SELECT 1 FROM QMS_INSPECTION I WHERE I.INSPECTION_ID = R.RESULT_ID
            );
            """);

        const string inspectionChecks = """
            BEGIN
              SELECT RAISE(ABORT, 'QMS_INSPECTION has invalid sample/defect quantities')
                WHERE NOT ((NEW.SAMPLE_QTY IS NULL AND NEW.DEFECT_QTY IS NULL) OR
                  (NEW.SAMPLE_QTY IS NOT NULL AND NEW.DEFECT_QTY IS NOT NULL AND
                   NEW.SAMPLE_QTY >= 0 AND NEW.DEFECT_QTY >= 0 AND NEW.DEFECT_QTY <= NEW.SAMPLE_QTY));
              SELECT RAISE(ABORT, 'QMS_INSPECTION references an unknown lot')
                WHERE NEW.LOT_ID IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID)
                  AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_LOT L WHERE L.LOT_ID = NEW.LOT_ID);
              SELECT RAISE(ABORT, 'QMS_INSPECTION references an unknown equipment')
                WHERE NEW.EQUIPMENT_ID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM MDM_EQUIPMENT E WHERE E.EQUIPMENT_ID = NEW.EQUIPMENT_ID);
              SELECT RAISE(ABORT, 'QMS_INSPECTION references an unknown inspection spec')
                WHERE NEW.SPEC_ID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM QMS_INSPECTION_SPEC S WHERE S.SPEC_ID = NEW.SPEC_ID);
            END
            """;
        Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_INSPECTION_INTEGRITY_BI;");
        Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_INSPECTION_INTEGRITY_BU;");
        Exec(conn, $"CREATE TRIGGER TR_QMS_INSPECTION_INTEGRITY_BI BEFORE INSERT ON QMS_INSPECTION {inspectionChecks}");
        Exec(conn, $"CREATE TRIGGER TR_QMS_INSPECTION_INTEGRITY_BU BEFORE UPDATE ON QMS_INSPECTION {inspectionChecks}");

        const string resultChecks = """
            BEGIN
              SELECT RAISE(ABORT, 'Confirmed QMS v2 inspections cannot accept additional result rows')
                WHERE EXISTS (SELECT 1 FROM QMS_INSPECTION_EVENT E
                              WHERE E.INSPECTION_ID = NEW.INSPECTION_ID
                                AND E.EVENT_TYPE = 'Confirmed');
              SELECT RAISE(ABORT, 'QMS inspection result requires a matching header and references')
                WHERE COALESCE(TRIM(NEW.INSPECTION_ID), '') = ''
                   OR NOT EXISTS (
                       SELECT 1 FROM QMS_INSPECTION I
                       WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                         AND I.LOT_ID = NEW.LOT_ID
                         AND I.EQUIPMENT_ID = NEW.EQUIPMENT_ID
                         AND (I.IDEMPOTENCY_KEY IS NOT NULL OR I.SPEC_ID = NEW.SPEC_ID))
                   OR (NOT EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID)
                       AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_LOT L WHERE L.LOT_ID = NEW.LOT_ID))
                   OR NOT EXISTS (SELECT 1 FROM MDM_EQUIPMENT E WHERE E.EQUIPMENT_ID = NEW.EQUIPMENT_ID)
                   OR NOT EXISTS (SELECT 1 FROM QMS_INSPECTION_SPEC S WHERE S.SPEC_ID = NEW.SPEC_ID AND S.IS_ACTIVE = 1);
              SELECT RAISE(ABORT, 'QMS inspection result has an invalid verdict')
                WHERE NEW.IS_PASS NOT IN (0, 1);
              SELECT RAISE(ABORT, 'QMS v2 result requires a matching v2 header, lot, equipment, and inspection type')
                WHERE ((NEW.ITEM_SEQUENCE IS NOT NULL OR EXISTS (
                          SELECT 1 FROM QMS_INSPECTION I
                          WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                            AND I.IDEMPOTENCY_KEY IS NOT NULL))
                   AND NOT EXISTS (
                       SELECT 1 FROM QMS_INSPECTION I
                       WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                         AND I.IDEMPOTENCY_KEY IS NOT NULL
                         AND NEW.ITEM_SEQUENCE IS NOT NULL
                         AND I.LOT_ID = NEW.LOT_ID
                         AND I.EQUIPMENT_ID = NEW.EQUIPMENT_ID
                         AND I.INSPECTION_TYPE IN ('Incoming', 'Process', 'Shipping')
                         AND (EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID)
                              OR EXISTS (SELECT 1 FROM IVT_MATERIAL_LOT L WHERE L.LOT_ID = NEW.LOT_ID))));
              SELECT RAISE(ABORT, 'QMS v2 result requires active equipment and an active inspection specification')
                WHERE EXISTS (SELECT 1 FROM QMS_INSPECTION I
                              WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                                AND I.IDEMPOTENCY_KEY IS NOT NULL)
                  AND (NOT EXISTS (SELECT 1 FROM MDM_EQUIPMENT E
                                   WHERE E.EQUIPMENT_ID = NEW.EQUIPMENT_ID
                                     AND E.VALID_STATE = 'Active')
                       OR NOT EXISTS (SELECT 1 FROM QMS_INSPECTION_SPEC S
                                     WHERE S.SPEC_ID = NEW.SPEC_ID AND S.IS_ACTIVE = 1));
              SELECT RAISE(ABORT, 'QMS v2 result quantities must be positive/bounded and cannot exceed header quantities')
                WHERE EXISTS (
                    SELECT 1 FROM QMS_INSPECTION I
                    WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                      AND I.IDEMPOTENCY_KEY IS NOT NULL
                      AND (NEW.SAMPLE_QTY IS NULL OR NEW.SAMPLE_QTY <= 0
                           OR NEW.DEFECT_QTY IS NULL OR NEW.DEFECT_QTY < 0
                           OR NEW.DEFECT_QTY > NEW.SAMPLE_QTY
                           OR I.SAMPLE_QTY IS NULL OR NEW.SAMPLE_QTY > I.SAMPLE_QTY
                           OR I.DEFECT_QTY IS NULL OR NEW.DEFECT_QTY > I.DEFECT_QTY));
              SELECT RAISE(ABORT, 'QMS v2 result value/verdict does not match its inspection specification type')
                WHERE EXISTS (
                    SELECT 1
                    FROM QMS_INSPECTION I
                    JOIN QMS_INSPECTION_SPEC S ON S.SPEC_ID = NEW.SPEC_ID
                    WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                      AND I.IDEMPOTENCY_KEY IS NOT NULL
                      AND (
                        (S.MEASURE_TYPE IN ('Variable', 'Numeric') AND
                           (NEW.MEASURED_VALUE IS NULL OR S.NOMINAL_VALUE IS NULL OR
                            NEW.IS_PASS <> CASE WHEN
                              (NEW.MEASURED_VALUE <= S.NOMINAL_VALUE OR S.TOLERANCE_PLUS IS NULL
                               OR NEW.MEASURED_VALUE - S.NOMINAL_VALUE <= S.TOLERANCE_PLUS)
                              AND
                              (NEW.MEASURED_VALUE >= S.NOMINAL_VALUE OR S.TOLERANCE_MINUS IS NULL
                               OR S.NOMINAL_VALUE - NEW.MEASURED_VALUE <= S.TOLERANCE_MINUS)
                              THEN 1 ELSE 0 END))
                        OR
                        (S.MEASURE_TYPE = 'Attribute' AND
                           (NEW.MEASURED_VALUE IS NOT NULL
                            OR NEW.ATTRIBUTE_RESULT IS NULL
                            OR NEW.ATTRIBUTE_RESULT NOT IN ('Pass', 'Fail')
                            OR NEW.IS_PASS <> CASE WHEN NEW.ATTRIBUTE_RESULT = 'Pass' THEN 1 ELSE 0 END))
                        OR S.MEASURE_TYPE NOT IN ('Variable', 'Numeric', 'Attribute')));
              SELECT RAISE(ABORT, 'A QMS v2 inspection specification can appear only once per execution')
                WHERE EXISTS (SELECT 1 FROM QMS_INSPECTION I
                              WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                                AND I.IDEMPOTENCY_KEY IS NOT NULL)
                  AND EXISTS (SELECT 1 FROM QMS_INSPECTION_RESULT R
                              WHERE R.INSPECTION_ID = NEW.INSPECTION_ID
                                AND R.RESULT_ID <> NEW.RESULT_ID
                                AND R.SPEC_ID = NEW.SPEC_ID COLLATE NOCASE);
            END
            """;
        // Rebuild instead of CREATE IF NOT EXISTS so a database bootstrapped by an older
        // V093 runtime receives the expanded POM/IVT lot boundary on its next startup.
        Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_RESULT_INTEGRITY_BI;");
        Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_RESULT_INTEGRITY_BU;");
        Exec(conn, $"CREATE TRIGGER TR_QMS_RESULT_INTEGRITY_BI BEFORE INSERT ON QMS_INSPECTION_RESULT {resultChecks}");
        Exec(conn, $"CREATE TRIGGER TR_QMS_RESULT_INTEGRITY_BU BEFORE UPDATE ON QMS_INSPECTION_RESULT {resultChecks}");
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS TR_QMS_INSPECTION_RESULT_BD
            BEFORE DELETE ON QMS_INSPECTION
            BEGIN
              SELECT RAISE(ABORT, 'QMS inspection header has result rows')
                WHERE EXISTS (SELECT 1 FROM QMS_INSPECTION_RESULT R WHERE R.INSPECTION_ID = OLD.INSPECTION_ID);
            END;
            """);
    }

    /// <summary>
    /// V093의 SQLite UNIQUE(INSPECTION_ID)는 ALTER DROP으로 제거할 수 없으므로 결과 테이블을
    /// 한 번 재구성해 1:N 항목을 허용합니다. 기존 DB와 빈 DB가 같은 v2 구조/불변 트리거를 갖게 합니다.
    /// </summary>
    private static void EnsureQmsInspectionExecutionV2(SqliteConnection conn)
    {
        if (!HasTable(conn, "QMS_INSPECTION") || !HasTable(conn, "QMS_INSPECTION_RESULT")
            || !HasTable(conn, "QMS_INSPECTION_EVENT")
            || !HasColumn(conn, "QMS_INSPECTION", "IDEMPOTENCY_KEY")
            || !HasColumn(conn, "QMS_INSPECTION_RESULT", "ITEM_SEQUENCE"))
            return;

        if (HasUniqueIndexOnColumn(conn, "QMS_INSPECTION_RESULT", "INSPECTION_ID"))
        {
            Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_RESULT_INTEGRITY_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_RESULT_INTEGRITY_BU;");
            Exec(conn, "ALTER TABLE QMS_INSPECTION_RESULT RENAME TO QMS_INSPECTION_RESULT_V1;");
            Exec(conn, """
                CREATE TABLE QMS_INSPECTION_RESULT (
                    RESULT_ID TEXT NOT NULL PRIMARY KEY,
                    SPEC_ID TEXT NOT NULL,
                    LOT_ID TEXT NOT NULL,
                    EQUIPMENT_ID TEXT NOT NULL,
                    MEASURED_VALUE NUMERIC NULL,
                    ATTRIBUTE_RESULT TEXT NULL,
                    INSPECTED_AT TEXT NOT NULL,
                    INSPECTOR_ID TEXT NOT NULL,
                    IS_PASS INTEGER NOT NULL DEFAULT 0,
                    REMARK TEXT NULL,
                    CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                    CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                    UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INSPECTION_ID TEXT NOT NULL,
                    ITEM_SEQUENCE INTEGER NULL,
                    SAMPLE_QTY INTEGER NULL,
                    DEFECT_QTY INTEGER NULL
                );
                INSERT INTO QMS_INSPECTION_RESULT
                    (RESULT_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, MEASURED_VALUE,
                     ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID, IS_PASS, REMARK,
                     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT, INSPECTION_ID,
                     ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY)
                SELECT RESULT_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, MEASURED_VALUE,
                       ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID, IS_PASS, REMARK,
                       CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT, INSPECTION_ID,
                       ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY
                FROM QMS_INSPECTION_RESULT_V1;
                DROP TABLE QMS_INSPECTION_RESULT_V1;
                CREATE INDEX IF NOT EXISTS IX_QMS_INSP_RESULT_LOT
                    ON QMS_INSPECTION_RESULT (LOT_ID, INSPECTED_AT DESC);
                CREATE INDEX IF NOT EXISTS IX_QMS_INSP_RESULT_SPEC
                    ON QMS_INSPECTION_RESULT (SPEC_ID, INSPECTED_AT DESC);
                CREATE UNIQUE INDEX IF NOT EXISTS UX_QMS_INSPECTION_RESULT_SEQUENCE
                    ON QMS_INSPECTION_RESULT (INSPECTION_ID, ITEM_SEQUENCE)
                    WHERE ITEM_SEQUENCE IS NOT NULL;
                """);
        }

        const string headerChecks = """
            BEGIN
              SELECT RAISE(ABORT, 'QMS v2 inspection has invalid immutable metadata')
                WHERE NEW.IDEMPOTENCY_KEY IS NOT NULL AND
                    (COALESCE(TRIM(NEW.IDEMPOTENCY_KEY), '') = '' OR
                     LENGTH(NEW.IDEMPOTENCY_KEY) > 150 OR
                     NEW.REQUEST_HASH IS NULL OR LENGTH(NEW.REQUEST_HASH) <> 64 OR
                     NEW.REQUEST_HASH GLOB '*[^0-9A-Fa-f]*' OR
                     COALESCE(TRIM(NEW.LOT_ID), '') = '' OR
                     COALESCE(TRIM(NEW.EQUIPMENT_ID), '') = '' OR
                     COALESCE(TRIM(NEW.INSPECTOR_ID), '') = '' OR
                     NEW.INSPECTION_TYPE NOT IN ('Incoming', 'Process', 'Shipping') OR
                     NEW.LOT_QTY IS NULL OR NEW.LOT_QTY <= 0 OR
                     NEW.SAMPLE_QTY IS NULL OR NEW.SAMPLE_QTY <= 0 OR NEW.SAMPLE_QTY > NEW.LOT_QTY OR
                     NEW.DEFECT_QTY IS NULL OR NEW.DEFECT_QTY < 0 OR NEW.DEFECT_QTY > NEW.SAMPLE_QTY OR
                     NEW.RELATION_TYPE NOT IN ('Original', 'Correction', 'Reinspection') OR
                     COALESCE(TRIM(NEW.ROOT_INSPECTION_ID), '') = '' OR
                     (NEW.RELATION_TYPE = 'Original' AND NEW.PARENT_INSPECTION_ID IS NOT NULL) OR
                     (NEW.RELATION_TYPE IN ('Correction', 'Reinspection') AND NEW.PARENT_INSPECTION_ID IS NULL));
              SELECT RAISE(ABORT, 'QMS v2 original inspection must be its own root')
                WHERE NEW.IDEMPOTENCY_KEY IS NOT NULL
                  AND NEW.RELATION_TYPE = 'Original'
                  AND NEW.ROOT_INSPECTION_ID <> NEW.INSPECTION_ID;
              SELECT RAISE(ABORT, 'QMS v2 lineage requires a matching parent and root')
                WHERE NEW.IDEMPOTENCY_KEY IS NOT NULL
                  AND NEW.RELATION_TYPE IN ('Correction', 'Reinspection')
                  AND NOT EXISTS (
                    SELECT 1 FROM QMS_INSPECTION P
                    WHERE P.INSPECTION_ID = NEW.PARENT_INSPECTION_ID
                      AND P.ROOT_INSPECTION_ID = NEW.ROOT_INSPECTION_ID
                      AND P.INSPECTION_TYPE = NEW.INSPECTION_TYPE
                      AND P.LOT_ID = NEW.LOT_ID
                      AND P.IDEMPOTENCY_KEY IS NOT NULL);
              SELECT RAISE(ABORT, 'QMS v2 sampling revision does not exist')
                WHERE NEW.IDEMPOTENCY_KEY IS NOT NULL
                  AND NEW.SAMPLING_PLAN_REVISION_ID IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM QMS_SAMPLING_PLAN_REVISION S
                                  WHERE S.PLAN_REVISION_ID = NEW.SAMPLING_PLAN_REVISION_ID);
              SELECT RAISE(ABORT, 'QMS sampling-plan revision is not effective at inspection time')
                WHERE NEW.IDEMPOTENCY_KEY IS NOT NULL
                  AND NEW.SAMPLING_PLAN_REVISION_ID IS NOT NULL
                  AND EXISTS (SELECT 1 FROM QMS_SAMPLING_PLAN_REVISION S
                              WHERE S.PLAN_REVISION_ID = NEW.SAMPLING_PLAN_REVISION_ID
                                AND S.EFFECTIVE_FROM > NEW.INSPECTED_AT);
            END
            """;
        Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_V2_HEADER_BI;");
        Exec(conn, $"CREATE TRIGGER TR_QMS_V2_HEADER_BI BEFORE INSERT ON QMS_INSPECTION {headerChecks}");
        Exec(conn, "DROP TRIGGER IF EXISTS TR_QMS_V2_EVENT_BI;");
        Exec(conn, """
            CREATE TRIGGER TR_QMS_V2_EVENT_BI
            BEFORE INSERT ON QMS_INSPECTION_EVENT
            BEGIN
              SELECT RAISE(ABORT, 'QMS inspection event has invalid metadata')
                WHERE NEW.EVENT_TYPE NOT IN ('Confirmed', 'Cancelled', 'Corrected', 'Reinspected')
                   OR COALESCE(TRIM(NEW.IDEMPOTENCY_KEY), '') = ''
                   OR LENGTH(NEW.REQUEST_HASH) <> 64
                   OR COALESCE(TRIM(NEW.ACTOR_ID), '') = '';
              SELECT RAISE(ABORT, 'QMS inspection event requires a matching execution and root')
                WHERE NOT EXISTS (
                    SELECT 1 FROM QMS_INSPECTION I
                    WHERE I.INSPECTION_ID = NEW.INSPECTION_ID
                      AND I.ROOT_INSPECTION_ID = NEW.ROOT_INSPECTION_ID
                      AND I.IDEMPOTENCY_KEY IS NOT NULL);
              SELECT RAISE(ABORT, 'QMS inspection event related execution is invalid')
                WHERE NEW.RELATED_INSPECTION_ID IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM QMS_INSPECTION I
                    WHERE I.INSPECTION_ID = NEW.RELATED_INSPECTION_ID
                      AND I.ROOT_INSPECTION_ID = NEW.ROOT_INSPECTION_ID
                      AND I.IDEMPOTENCY_KEY IS NOT NULL);
              SELECT RAISE(ABORT, 'QMS inspection event parent is invalid')
                WHERE NEW.PARENT_INSPECTION_ID IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM QMS_INSPECTION I
                                  WHERE I.INSPECTION_ID = NEW.PARENT_INSPECTION_ID
                                    AND I.ROOT_INSPECTION_ID = NEW.ROOT_INSPECTION_ID
                                    AND I.IDEMPOTENCY_KEY IS NOT NULL);
              SELECT RAISE(ABORT, 'QMS correction/reinspection event has invalid successor lineage')
                WHERE NEW.EVENT_TYPE IN ('Corrected', 'Reinspected')
                  AND (NEW.RELATED_INSPECTION_ID IS NULL
                       OR NEW.PARENT_INSPECTION_ID IS NULL
                       OR NEW.PARENT_INSPECTION_ID <> NEW.INSPECTION_ID
                       OR NOT EXISTS (
                         SELECT 1 FROM QMS_INSPECTION C
                         WHERE C.INSPECTION_ID = NEW.RELATED_INSPECTION_ID
                           AND C.PARENT_INSPECTION_ID = NEW.INSPECTION_ID
                           AND C.ROOT_INSPECTION_ID = NEW.ROOT_INSPECTION_ID
                           AND ((NEW.EVENT_TYPE = 'Corrected' AND C.RELATION_TYPE = 'Correction')
                                OR (NEW.EVENT_TYPE = 'Reinspected' AND C.RELATION_TYPE = 'Reinspection'))));
              SELECT RAISE(ABORT, 'QMS confirmation/cancellation event cannot identify a successor')
                WHERE NEW.EVENT_TYPE IN ('Confirmed', 'Cancelled')
                  AND NEW.RELATED_INSPECTION_ID IS NOT NULL;
              SELECT RAISE(ABORT, 'A QMS v2 inspection requires at least one result item before confirmation')
                WHERE NEW.EVENT_TYPE = 'Confirmed'
                  AND NOT EXISTS (SELECT 1 FROM QMS_INSPECTION_RESULT R
                                  WHERE R.INSPECTION_ID = NEW.INSPECTION_ID
                                    AND R.ITEM_SEQUENCE IS NOT NULL);
              SELECT RAISE(ABORT, 'QMS inspection event actor does not exist')
                WHERE EXISTS (SELECT 1 FROM SYS_USER)
                  AND NOT EXISTS (SELECT 1 FROM SYS_USER U WHERE U.USER_ID = NEW.ACTOR_ID);
            END;
            """);
        Exec(conn, """
            DROP INDEX IF EXISTS UX_QMS_INSPECTION_RESULT_SPEC;
            CREATE UNIQUE INDEX UX_QMS_INSPECTION_RESULT_SPEC
                ON QMS_INSPECTION_RESULT (INSPECTION_ID, SPEC_ID COLLATE NOCASE)
                WHERE ITEM_SEQUENCE IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS UX_QMS_INSPECTION_EVENT_CANCELLED
                ON QMS_INSPECTION_EVENT (INSPECTION_ID)
                WHERE EVENT_TYPE = 'Cancelled';
            CREATE TRIGGER IF NOT EXISTS TR_QMS_V2_RESULT_BI
            BEFORE INSERT ON QMS_INSPECTION_RESULT
            WHEN EXISTS (SELECT 1 FROM QMS_INSPECTION_EVENT E
                         WHERE E.INSPECTION_ID = NEW.INSPECTION_ID
                           AND E.EVENT_TYPE = 'Confirmed')
            BEGIN SELECT RAISE(ABORT, 'Confirmed QMS v2 inspections cannot accept additional result rows'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_V2_HEADER_BU
            BEFORE UPDATE ON QMS_INSPECTION
            WHEN OLD.IDEMPOTENCY_KEY IS NOT NULL
            BEGIN SELECT RAISE(ABORT, 'Confirmed QMS v2 inspection headers are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_V2_HEADER_BD
            BEFORE DELETE ON QMS_INSPECTION
            WHEN OLD.IDEMPOTENCY_KEY IS NOT NULL
            BEGIN SELECT RAISE(ABORT, 'Confirmed QMS v2 inspection headers are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_V2_RESULT_BU
            BEFORE UPDATE ON QMS_INSPECTION_RESULT
            WHEN EXISTS (SELECT 1 FROM QMS_INSPECTION I
                         WHERE I.INSPECTION_ID = OLD.INSPECTION_ID AND I.IDEMPOTENCY_KEY IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'Confirmed QMS v2 inspection results are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_V2_RESULT_BD
            BEFORE DELETE ON QMS_INSPECTION_RESULT
            WHEN EXISTS (SELECT 1 FROM QMS_INSPECTION I
                         WHERE I.INSPECTION_ID = OLD.INSPECTION_ID AND I.IDEMPOTENCY_KEY IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'Confirmed QMS v2 inspection results are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_V2_EVENT_BU
            BEFORE UPDATE ON QMS_INSPECTION_EVENT
            BEGIN SELECT RAISE(ABORT, 'QMS inspection history is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_V2_EVENT_BD
            BEFORE DELETE ON QMS_INSPECTION_EVENT
            BEGIN SELECT RAISE(ABORT, 'QMS inspection history is append-only'); END;
            """);
    }

    /// <summary>
    /// Keeps AI model, inference, and human-review evidence append-only and restores the
    /// inspection/model/reviewer links when SQLite is intentionally opened with FK checks off.
    /// </summary>
    private static void EnsureQmsAiEvidenceIntegrity(SqliteConnection conn)
    {
        if (!HasTable(conn, "QMS_AI_MODEL_VERSION") || !HasTable(conn, "QMS_AI_INFERENCE")
            || !HasTable(conn, "QMS_AI_REVIEW") || !HasTable(conn, "QMS_INSPECTION"))
            return;

        Exec(conn, """
            DROP TRIGGER IF EXISTS TR_QMS_AI_INFERENCE_BI;
            CREATE TRIGGER TR_QMS_AI_INFERENCE_BI
            BEFORE INSERT ON QMS_AI_INFERENCE
            BEGIN
              SELECT RAISE(ABORT, 'QMS AI inference inspection does not exist')
                WHERE NOT EXISTS (SELECT 1 FROM QMS_INSPECTION I
                                  WHERE I.INSPECTION_ID = NEW.INSPECTION_ID);
              SELECT RAISE(ABORT, 'QMS AI inference model version does not exist')
                WHERE NOT EXISTS (SELECT 1 FROM QMS_AI_MODEL_VERSION M
                                  WHERE M.MODEL_VERSION_ID = NEW.MODEL_VERSION_ID);
              SELECT RAISE(ABORT, 'QMS AI model version is not effective at inference time')
                WHERE EXISTS (SELECT 1 FROM QMS_AI_MODEL_VERSION M
                              WHERE M.MODEL_VERSION_ID = NEW.MODEL_VERSION_ID
                                AND M.EFFECTIVE_FROM > NEW.INFERRED_AT);
            END;
            DROP TRIGGER IF EXISTS TR_QMS_AI_REVIEW_BI;
            CREATE TRIGGER TR_QMS_AI_REVIEW_BI
            BEFORE INSERT ON QMS_AI_REVIEW
            BEGIN
              SELECT RAISE(ABORT, 'QMS AI review inference does not exist')
                WHERE NOT EXISTS (SELECT 1 FROM QMS_AI_INFERENCE I
                                  WHERE I.INFERENCE_ID = NEW.INFERENCE_ID);
              SELECT RAISE(ABORT, 'QMS AI review actor does not exist')
                WHERE EXISTS (SELECT 1 FROM SYS_USER)
                  AND NOT EXISTS (SELECT 1 FROM SYS_USER U WHERE U.USER_ID = NEW.REVIEWER_ID);
            END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_AI_MODEL_VERSION_BU
            BEFORE UPDATE ON QMS_AI_MODEL_VERSION
            BEGIN SELECT RAISE(ABORT, 'QMS AI model evidence is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_AI_MODEL_VERSION_BD
            BEFORE DELETE ON QMS_AI_MODEL_VERSION
            BEGIN SELECT RAISE(ABORT, 'QMS AI model evidence is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_AI_INFERENCE_BU
            BEFORE UPDATE ON QMS_AI_INFERENCE
            BEGIN SELECT RAISE(ABORT, 'QMS AI inference evidence is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_AI_INFERENCE_BD
            BEFORE DELETE ON QMS_AI_INFERENCE
            BEGIN SELECT RAISE(ABORT, 'QMS AI inference evidence is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_AI_REVIEW_BU
            BEFORE UPDATE ON QMS_AI_REVIEW
            BEGIN SELECT RAISE(ABORT, 'QMS AI review evidence is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS TR_QMS_AI_REVIEW_BD
            BEFORE DELETE ON QMS_AI_REVIEW
            BEGIN SELECT RAISE(ABORT, 'QMS AI review evidence is append-only'); END;
            """);
    }

    private static bool HasUniqueIndexOnColumn(
        SqliteConnection conn, string table, string column)
    {
        using var indexes = conn.CreateCommand();
        indexes.CommandText = $"PRAGMA index_list([{table.Replace("]", "]]", StringComparison.Ordinal)}]);";
        using var reader = indexes.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            if (reader.GetInt32(2) == 1) names.Add(reader.GetString(1));
        reader.Close();

        foreach (var name in names)
        {
            using var columns = conn.CreateCommand();
            columns.CommandText = $"PRAGMA index_info([{name.Replace("]", "]]", StringComparison.Ordinal)}]);";
            using var columnReader = columns.ExecuteReader();
            var indexed = new List<string>();
            while (columnReader.Read()) indexed.Add(columnReader.GetString(2));
            if (indexed.Count == 1
                && string.Equals(indexed[0], column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void EnsurePomBoundaryTriggers(SqliteConnection conn)
    {
        if (HasTable(conn, "POM_WORK_ORDER") && HasTable(conn, "POM_PRODUCTION_ORDER")
            && HasTable(conn, "POM_PRODUCTION_PLAN"))
        {
            Exec(conn, """
                UPDATE POM_WORK_ORDER
                SET PRODUCT_ID = (
                    SELECT O.PRODUCT_ID FROM POM_PRODUCTION_ORDER O
                    WHERE O.ORDER_ID = POM_WORK_ORDER.PRODUCTION_ORDER_ID
                )
                WHERE COALESCE(TRIM(PRODUCT_ID), '') = ''
                  AND EXISTS (
                      SELECT 1 FROM POM_PRODUCTION_ORDER O
                      WHERE O.ORDER_ID = POM_WORK_ORDER.PRODUCTION_ORDER_ID
                  );
                """);

            const string workOrderChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_WORK_ORDER requires a matching production order, product, and plant')
                    WHERE COALESCE(TRIM(NEW.PRODUCTION_ORDER_ID), '') = ''
                       OR COALESCE(TRIM(NEW.PRODUCT_ID), '') = ''
                       OR COALESCE(TRIM(NEW.PLANT_ID), '') = ''
                       OR NOT EXISTS (
                           SELECT 1
                           FROM POM_PRODUCTION_ORDER O
                           JOIN POM_PRODUCTION_PLAN P ON P.PLAN_ID = O.PLAN_ID
                           WHERE O.ORDER_ID = NEW.PRODUCTION_ORDER_ID
                             AND O.PRODUCT_ID = NEW.PRODUCT_ID
                             AND P.PLANT_ID = NEW.PLANT_ID
                       );
                  SELECT RAISE(ABORT, 'POM_WORK_ORDER has an invalid status or hold flag')
                    WHERE NEW.STATUS NOT IN ('Created', 'Released', 'Started', 'Completed', 'Cancelled')
                       OR NEW.IS_HOLD NOT IN ('Y', 'N');
                  SELECT RAISE(ABORT, 'POM_WORK_ORDER has invalid quantities or version')
                    WHERE NEW.PLAN_QTY IS NULL OR NEW.PLAN_QTY <= 0
                       OR NEW.START_QTY IS NULL OR NEW.START_QTY < 0 OR NEW.START_QTY > NEW.PLAN_QTY
                       OR NEW.COMPLETE_QTY IS NULL OR NEW.COMPLETE_QTY < 0
                       OR NEW.SCRAP_QTY IS NULL OR NEW.SCRAP_QTY < 0
                       OR NEW.COMPLETE_QTY + NEW.SCRAP_QTY >
                          CASE WHEN NEW.START_QTY > 0 THEN NEW.START_QTY ELSE NEW.PLAN_QTY END
                       OR NEW.VERSION_NO IS NULL OR NEW.VERSION_NO < 1;
                END;
                """;

            Exec(conn, $"CREATE TRIGGER IF NOT EXISTS TR_POM_WORK_ORDER_BOUNDARY_BI BEFORE INSERT ON POM_WORK_ORDER {workOrderChecks}");
            Exec(conn, $"CREATE TRIGGER IF NOT EXISTS TR_POM_WORK_ORDER_BOUNDARY_BU BEFORE UPDATE ON POM_WORK_ORDER {workOrderChecks}");
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_PRODUCTION_ORDER_CHILD_BD
                BEFORE DELETE ON POM_PRODUCTION_ORDER
                WHEN EXISTS (SELECT 1 FROM POM_WORK_ORDER W WHERE W.PRODUCTION_ORDER_ID = OLD.ORDER_ID)
                BEGIN
                  SELECT RAISE(ABORT, 'POM_PRODUCTION_ORDER has child POM_WORK_ORDER rows');
                END;
                """);
        }

        // SQLite drops ALTER TABLE ... ADD CONSTRAINT during dialect conversion. Recreate the
        // V106 routing-scope invariant as INSERT/UPDATE triggers for fresh and upgraded DBs.
        // Incremental schema creation intentionally skips migration UPDATE statements, so perform
        // the one-time-compatible inference here before installing the guards.
        if (HasTable(conn, "POM_WORK_ORDER") &&
            HasColumn(conn, "POM_WORK_ORDER", "ROUTING_ID") &&
            HasColumn(conn, "POM_WORK_ORDER", "ROUTING_STEP_NO") &&
            HasColumn(conn, "POM_WORK_ORDER", "ROUTING_SCOPE"))
        {
            Exec(conn, """
                UPDATE POM_WORK_ORDER
                   SET ROUTING_SCOPE = 'Operation'
                 WHERE ROUTING_SCOPE = 'Unbound'
                   AND ROUTING_ID IS NOT NULL
                   AND COALESCE(TRIM(ROUTING_ID), '') <> ''
                   AND ROUTING_STEP_NO IS NOT NULL;
                """);
            const string workOrderRoutingChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_WORK_ORDER has an invalid routing scope or binding')
                    WHERE NEW.ROUTING_SCOPE IS NULL
                       OR NEW.ROUTING_SCOPE NOT IN ('Unbound', 'Operation', 'SerialRoute')
                       OR (NEW.ROUTING_SCOPE = 'Unbound'
                           AND (NEW.ROUTING_ID IS NOT NULL OR NEW.ROUTING_STEP_NO IS NOT NULL))
                       OR (NEW.ROUTING_SCOPE = 'Operation'
                           AND (COALESCE(TRIM(NEW.ROUTING_ID), '') = ''
                                OR NEW.ROUTING_STEP_NO IS NULL OR NEW.ROUTING_STEP_NO <= 0))
                       OR (NEW.ROUTING_SCOPE = 'SerialRoute'
                           AND (COALESCE(TRIM(NEW.ROUTING_ID), '') = ''
                                OR NEW.ROUTING_STEP_NO IS NOT NULL
                                OR NEW.PROCESS_ID IS NOT NULL));
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_ROUTING_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_ROUTING_BU;");
            Exec(conn, $"CREATE TRIGGER TR_POM_WORK_ORDER_ROUTING_BI BEFORE INSERT ON POM_WORK_ORDER {workOrderRoutingChecks}");
            Exec(conn, $"CREATE TRIGGER TR_POM_WORK_ORDER_ROUTING_BU BEFORE UPDATE ON POM_WORK_ORDER {workOrderRoutingChecks}");
        }

        if (HasTable(conn, "POM_LOT") && HasTable(conn, "POM_WORK_ORDER"))
        {
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_WORK_ORDER_BI
                BEFORE INSERT ON POM_LOT
                WHEN NEW.WORK_ORDER_ID IS NOT NULL
                 AND (COALESCE(TRIM(NEW.WORK_ORDER_ID), '') = ''
                      OR NOT EXISTS (SELECT 1 FROM POM_WORK_ORDER W WHERE W.WORK_ORDER_ID = NEW.WORK_ORDER_ID))
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT.WORK_ORDER_ID must reference POM_WORK_ORDER');
                END;
                """);
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_WORK_ORDER_BU
                BEFORE UPDATE OF WORK_ORDER_ID ON POM_LOT
                WHEN NEW.WORK_ORDER_ID IS NOT NULL
                 AND (COALESCE(TRIM(NEW.WORK_ORDER_ID), '') = ''
                      OR NOT EXISTS (SELECT 1 FROM POM_WORK_ORDER W WHERE W.WORK_ORDER_ID = NEW.WORK_ORDER_ID))
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT.WORK_ORDER_ID must reference POM_WORK_ORDER');
                END;
                """);
        }


        if (HasTable(conn, "POM_LOT"))
        {
            const string lotChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT has invalid quantities, state, hold flag, or version')
                    WHERE NEW.QTY IS NULL OR NEW.QTY <= 0
                       OR NEW.DEFECT_QTY IS NULL OR NEW.DEFECT_QTY < 0 OR NEW.DEFECT_QTY > NEW.QTY
                       OR NEW.VERSION_NO IS NULL OR NEW.VERSION_NO < 1
                       OR NEW.LOT_STATE NOT IN ('Created', 'Queued', 'Processing', 'Completed', 'Consumed')
                       OR NEW.PROCESS_STATE NOT IN ('Idle', 'Run')
                       OR NEW.IS_HOLD NOT IN ('Y', 'N');
                END;
                """;
            Exec(conn, $"CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_BOUNDARY_BI BEFORE INSERT ON POM_LOT {lotChecks}");
            Exec(conn, $"CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_BOUNDARY_BU BEFORE UPDATE ON POM_LOT {lotChecks}");
        }

        if (HasTable(conn, "POM_LOT") &&
            HasColumn(conn, "POM_LOT", "CONTROL_MODE") &&
            HasColumn(conn, "POM_LOT", "RETURN_STEP"))
        {
            const string lotRoutingChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT has invalid routing control values')
                    WHERE NEW.CONTROL_MODE IS NULL
                       OR NEW.CONTROL_MODE NOT IN ('Strict', 'Flexible', 'NoControl')
                       OR NEW.CURRENT_STEP IS NULL OR NEW.CURRENT_STEP < 0
                       OR (NEW.RETURN_STEP IS NOT NULL AND NEW.RETURN_STEP < 0);
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_ROUTING_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_ROUTING_BU;");
            Exec(conn, $"CREATE TRIGGER TR_POM_LOT_ROUTING_BI BEFORE INSERT ON POM_LOT {lotRoutingChecks}");
            Exec(conn, $"CREATE TRIGGER TR_POM_LOT_ROUTING_BU BEFORE UPDATE ON POM_LOT {lotRoutingChecks}");
        }

        if (HasTable(conn, "POM_LOT") && HasTable(conn, "POM_WORK_ORDER"))
        {
            const string lotWorkOrderBoundary = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT work-order plant/product mismatch')
                    WHERE NEW.WORK_ORDER_ID IS NOT NULL
                      AND NOT EXISTS (
                        SELECT 1 FROM POM_WORK_ORDER W
                        WHERE W.WORK_ORDER_ID = NEW.WORK_ORDER_ID
                          AND W.PLANT_ID = NEW.PLANT_ID
                          AND W.PRODUCT_ID = NEW.PRODUCT_ID
                      );
                END;
                """;
            Exec(conn, $"CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_WO_BOUNDARY_BI BEFORE INSERT ON POM_LOT {lotWorkOrderBoundary}");
            Exec(conn, $"CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_WO_BOUNDARY_BU BEFORE UPDATE ON POM_LOT {lotWorkOrderBoundary}");
        }

        if (HasTable(conn, "POM_LOT_HISTORY") && HasTable(conn, "POM_LOT"))
        {
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_HISTORY_BOUNDARY_BI
                BEFORE INSERT ON POM_LOT_HISTORY
                WHEN NEW.QTY IS NULL OR NEW.QTY <= 0
                  OR NEW.DEFECT_QTY IS NULL OR NEW.DEFECT_QTY < 0 OR NEW.DEFECT_QTY > NEW.QTY
                  OR NOT EXISTS (
                    SELECT 1 FROM POM_LOT L
                    WHERE L.LOT_ID = NEW.LOT_ID AND L.PLANT_ID = NEW.PLANT_ID
                  )
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT_HISTORY requires a matching lot and valid quantities');
                END;
                """);
        }

        if (HasTable(conn, "POM_LOT_MIXING_RELATION") && HasTable(conn, "POM_LOT"))
        {
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_MIXING_BOUNDARY_BI
                BEFORE INSERT ON POM_LOT_MIXING_RELATION
                WHEN NEW.INPUT_QTY IS NULL OR NEW.INPUT_QTY <= 0
                  OR (NEW.MIXING_RATE IS NOT NULL AND (NEW.MIXING_RATE <= 0 OR NEW.MIXING_RATE > 1))
                  OR NOT EXISTS (
                    SELECT 1 FROM POM_LOT L
                    WHERE L.LOT_ID = NEW.INPUT_LOT_ID AND L.PLANT_ID = NEW.PLANT_ID
                  )
                  OR NOT EXISTS (
                    SELECT 1 FROM POM_LOT L
                    WHERE L.LOT_ID = NEW.OUTPUT_LOT_ID AND L.PLANT_ID = NEW.PLANT_ID
                  )
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT_MIXING_RELATION requires matching lots and positive quantity');
                END;
                """);
        }

        if (HasTable(conn, "POM_LOT_EXECUTION") && HasTable(conn, "POM_LOT"))
        {
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_LOT_EXECUTION_BOUNDARY_BI
                BEFORE INSERT ON POM_LOT_EXECUTION
                WHEN COALESCE(TRIM(NEW.IDEMPOTENCY_KEY), '') = ''
                  OR COALESCE(TRIM(NEW.REQUEST_HASH), '') = ''
                  OR NEW.EXPECTED_VERSION IS NULL OR NEW.EXPECTED_VERSION < 1
                  OR NEW.RESULT_VERSION <> NEW.EXPECTED_VERSION + 1
                  OR NOT EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID)
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT_EXECUTION requires a lot, idempotency key, and consecutive version');
                END;
                """);

            if (HasColumn(conn, "POM_LOT_EXECUTION", "FROM_STEP") &&
                HasColumn(conn, "POM_LOT_EXECUTION", "CONTROL_MODE") &&
                HasColumn(conn, "POM_LOT_EXECUTION", "CLIENT_CHANNEL"))
            {
                const string executionRoutingChecks = """
                    BEGIN
                      SELECT RAISE(ABORT, 'POM_LOT_EXECUTION has invalid routing audit values')
                        WHERE (NEW.FROM_STEP IS NOT NULL AND NEW.FROM_STEP < 0)
                           OR (NEW.TO_STEP IS NOT NULL AND NEW.TO_STEP < 0)
                           OR (NEW.CONTROL_MODE IS NOT NULL
                               AND NEW.CONTROL_MODE NOT IN ('Strict', 'Flexible', 'NoControl'))
                           OR (NEW.CLIENT_CHANNEL IS NOT NULL
                               AND NEW.CLIENT_CHANNEL NOT IN ('MES', 'MOBILE', 'POP'));
                    END;
                    """;
                Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_ROUTING_BI;");
                Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_ROUTING_BU;");
                Exec(conn, $"CREATE TRIGGER TR_POM_LOT_EXECUTION_ROUTING_BI BEFORE INSERT ON POM_LOT_EXECUTION {executionRoutingChecks}");
                Exec(conn, $"CREATE TRIGGER TR_POM_LOT_EXECUTION_ROUTING_BU BEFORE UPDATE ON POM_LOT_EXECUTION {executionRoutingChecks}");
            }
        }

        if (HasTable(conn, "POM_ROUTE_EXCEPTION") &&
            HasTable(conn, "POM_LOT") &&
            HasTable(conn, "POM_LOT_HISTORY") &&
            HasTable(conn, "POM_LOT_EXECUTION") &&
            HasTable(conn, "POM_LOT_DEFECT_EXECUTION") &&
            HasTable(conn, "POM_LOT_MIXING_RELATION"))
        {
            const string routeExceptionLotChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_ROUTE_EXCEPTION requires a matching lot and plant')
                    WHERE NOT EXISTS (
                      SELECT 1 FROM POM_LOT L
                      WHERE L.LOT_ID = NEW.LOT_ID AND L.PLANT_ID = NEW.PLANT_ID
                    );
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_LOT_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_LOT_BU;");
            Exec(conn, $"CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_LOT_BI BEFORE INSERT ON POM_ROUTE_EXCEPTION {routeExceptionLotChecks}");
            Exec(conn, $"CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_LOT_BU BEFORE UPDATE OF LOT_ID, PLANT_ID ON POM_ROUTE_EXCEPTION {routeExceptionLotChecks}");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_ROUTE_EXCEPTION_BD;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_CHILD_BD;");
            Exec(conn, """
                CREATE TRIGGER TR_POM_LOT_CHILD_BD
                BEFORE DELETE ON POM_LOT
                WHEN EXISTS (
                  SELECT 1 FROM POM_ROUTE_EXCEPTION R
                  WHERE R.LOT_ID = OLD.LOT_ID AND R.PLANT_ID = OLD.PLANT_ID
                )
                OR EXISTS (
                  SELECT 1 FROM POM_LOT_HISTORY H
                  WHERE H.LOT_ID = OLD.LOT_ID AND H.PLANT_ID = OLD.PLANT_ID
                )
                OR EXISTS (SELECT 1 FROM POM_LOT_EXECUTION E WHERE E.LOT_ID = OLD.LOT_ID)
                OR EXISTS (
                  SELECT 1 FROM POM_LOT_DEFECT_EXECUTION D
                  WHERE D.LOT_ID = OLD.LOT_ID AND D.PLANT_ID = OLD.PLANT_ID
                )
                OR EXISTS (
                  SELECT 1 FROM POM_LOT_MIXING_RELATION M
                  WHERE M.PLANT_ID = OLD.PLANT_ID
                    AND (M.INPUT_LOT_ID = OLD.LOT_ID OR M.OUTPUT_LOT_ID = OLD.LOT_ID)
                )
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT has child tracking rows');
                END;
                """);
        }

        if (HasTable(conn, "POM_LOT_EXECUTION") &&
            HasTable(conn, "POM_ROUTE_EXCEPTION") &&
            HasColumn(conn, "POM_LOT_EXECUTION", "ROUTE_EXCEPTION_ID"))
        {
            const string lotExecutionExceptionChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT_EXECUTION route exception does not exist')
                    WHERE NEW.ROUTE_EXCEPTION_ID IS NOT NULL
                      AND (COALESCE(TRIM(NEW.ROUTE_EXCEPTION_ID), '') = ''
                           OR NOT EXISTS (
                             SELECT 1 FROM POM_ROUTE_EXCEPTION R
                             WHERE R.EXCEPTION_ID = NEW.ROUTE_EXCEPTION_ID
                               AND R.LOT_ID = NEW.LOT_ID
                           ));
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_EXCEPTION_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_EXCEPTION_BU;");
            Exec(conn, $"CREATE TRIGGER TR_POM_LOT_EXECUTION_EXCEPTION_BI BEFORE INSERT ON POM_LOT_EXECUTION {lotExecutionExceptionChecks}");
            Exec(conn, $"CREATE TRIGGER TR_POM_LOT_EXECUTION_EXCEPTION_BU BEFORE UPDATE OF ROUTE_EXCEPTION_ID, LOT_ID ON POM_LOT_EXECUTION {lotExecutionExceptionChecks}");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_EXECUTION_BU;");
            Exec(conn, """
                CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_EXECUTION_BU
                BEFORE UPDATE OF EXCEPTION_ID, LOT_ID ON POM_ROUTE_EXCEPTION
                WHEN EXISTS (
                  SELECT 1 FROM POM_LOT_EXECUTION E
                  WHERE E.ROUTE_EXCEPTION_ID = OLD.EXCEPTION_ID
                    AND (E.ROUTE_EXCEPTION_ID <> NEW.EXCEPTION_ID OR E.LOT_ID <> NEW.LOT_ID)
                )
                BEGIN
                  SELECT RAISE(ABORT, 'POM_ROUTE_EXCEPTION cannot detach child lot execution rows');
                END;
                """);
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_EXECUTION_BD;");
            Exec(conn, """
                CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_EXECUTION_BD
                BEFORE DELETE ON POM_ROUTE_EXCEPTION
                WHEN EXISTS (
                  SELECT 1 FROM POM_LOT_EXECUTION E
                  WHERE E.ROUTE_EXCEPTION_ID = OLD.EXCEPTION_ID
                )
                BEGIN
                  SELECT RAISE(ABORT, 'POM_ROUTE_EXCEPTION has child lot execution rows');
                END;
                """);
        }

        if (HasTable(conn, "POM_LOT_DEFECT_EXECUTION") &&
            HasTable(conn, "POM_LOT_EXECUTION") &&
            HasTable(conn, "POM_LOT") &&
            HasTable(conn, "POM_ROUTE_EXCEPTION"))
        {
            const string lotDefectExecutionParentChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT_DEFECT_EXECUTION requires a matching LOT execution and plant')
                    WHERE NOT EXISTS (
                      SELECT 1 FROM POM_LOT_EXECUTION E
                      WHERE E.EXECUTION_ID = NEW.EXECUTION_ID
                        AND E.LOT_ID = NEW.LOT_ID
                    )
                    OR NOT EXISTS (
                      SELECT 1 FROM POM_LOT L
                      WHERE L.LOT_ID = NEW.LOT_ID
                        AND L.PLANT_ID = NEW.PLANT_ID
                    );
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_DEFECT_EXECUTION_PARENT_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_DEFECT_EXECUTION_PARENT_BU;");
            Exec(conn, $"CREATE TRIGGER TR_POM_LOT_DEFECT_EXECUTION_PARENT_BI BEFORE INSERT ON POM_LOT_DEFECT_EXECUTION {lotDefectExecutionParentChecks}");
            Exec(conn, $"CREATE TRIGGER TR_POM_LOT_DEFECT_EXECUTION_PARENT_BU BEFORE UPDATE OF EXECUTION_ID, LOT_ID, PLANT_ID ON POM_LOT_DEFECT_EXECUTION {lotDefectExecutionParentChecks}");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_DEFECT_BD;");
            Exec(conn, """
                CREATE TRIGGER TR_POM_LOT_EXECUTION_DEFECT_BD
                BEFORE DELETE ON POM_LOT_EXECUTION
                WHEN EXISTS (
                  SELECT 1 FROM POM_LOT_DEFECT_EXECUTION D
                  WHERE D.EXECUTION_ID = OLD.EXECUTION_ID
                )
                OR EXISTS (
                  SELECT 1 FROM POM_ROUTE_EXCEPTION R
                  WHERE R.APPLIED_EXECUTION_ID = OLD.EXECUTION_ID
                )
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT_EXECUTION has child defect or approval rows');
                END;
                """);
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_CHILD_BU;");
            Exec(conn, """
                CREATE TRIGGER TR_POM_LOT_EXECUTION_CHILD_BU
                BEFORE UPDATE OF EXECUTION_ID, LOT_ID ON POM_LOT_EXECUTION
                WHEN EXISTS (
                  SELECT 1 FROM POM_LOT_DEFECT_EXECUTION D
                  WHERE D.EXECUTION_ID = OLD.EXECUTION_ID
                    AND (D.EXECUTION_ID <> NEW.EXECUTION_ID OR D.LOT_ID <> NEW.LOT_ID)
                )
                OR EXISTS (
                  SELECT 1 FROM POM_ROUTE_EXCEPTION R
                  WHERE R.APPLIED_EXECUTION_ID = OLD.EXECUTION_ID
                    AND (R.APPLIED_EXECUTION_ID <> NEW.EXECUTION_ID OR R.LOT_ID <> NEW.LOT_ID)
                )
                BEGIN
                  SELECT RAISE(ABORT, 'POM_LOT_EXECUTION cannot detach child audit rows');
                END;
                """);
        }

        if (HasTable(conn, "POM_ROUTE_EXCEPTION") && HasTable(conn, "POM_LOT_EXECUTION"))
        {
            const string appliedExecutionChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_ROUTE_EXCEPTION applied execution must belong to the same LOT')
                    WHERE NEW.APPLIED_EXECUTION_ID IS NOT NULL
                      AND (COALESCE(TRIM(NEW.APPLIED_EXECUTION_ID), '') = ''
                           OR NOT EXISTS (
                             SELECT 1 FROM POM_LOT_EXECUTION E
                             WHERE E.EXECUTION_ID = NEW.APPLIED_EXECUTION_ID
                               AND E.LOT_ID = NEW.LOT_ID
                           ));
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_APPLIED_EXECUTION_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_APPLIED_EXECUTION_BU;");
            Exec(conn, $"CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_APPLIED_EXECUTION_BI BEFORE INSERT ON POM_ROUTE_EXCEPTION {appliedExecutionChecks}");
            Exec(conn, $"CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_APPLIED_EXECUTION_BU BEFORE UPDATE OF APPLIED_EXECUTION_ID, LOT_ID ON POM_ROUTE_EXCEPTION {appliedExecutionChecks}");
        }

        if (HasTable(conn, "POM_ROUTE_EXCEPTION") &&
            HasColumn(conn, "POM_ROUTE_EXCEPTION", "REVIEW_CLIENT_CHANNEL") &&
            HasColumn(conn, "POM_ROUTE_EXCEPTION", "REVIEW_DEVICE_ID"))
        {
            // V104 legacy rows predate separate reviewer provenance. Preserve their known request
            // channel so later Apply updates do not erase or violate the new review boundary.
            Exec(conn, """
                UPDATE POM_ROUTE_EXCEPTION
                   SET REVIEW_CLIENT_CHANNEL = CLIENT_CHANNEL
                 WHERE REVIEWED_BY IS NOT NULL AND REVIEW_CLIENT_CHANNEL IS NULL;
                """);
            const string reviewProvenanceChecks = """
                BEGIN
                  SELECT RAISE(ABORT, 'POM_ROUTE_EXCEPTION has invalid review provenance')
                    WHERE (NEW.REVIEW_CLIENT_CHANNEL IS NOT NULL
                           AND NEW.REVIEW_CLIENT_CHANNEL NOT IN ('MES', 'MOBILE', 'POP'))
                       OR (NEW.REVIEWED_BY IS NULL
                           AND (NEW.REVIEW_CLIENT_CHANNEL IS NOT NULL OR NEW.REVIEW_DEVICE_ID IS NOT NULL))
                       OR (NEW.REVIEWED_BY IS NOT NULL
                           AND (NEW.REVIEWED_AT IS NULL OR NEW.REVIEW_CLIENT_CHANNEL IS NULL))
                       OR (NEW.STATUS IN ('Approved', 'Rejected', 'Applied')
                           AND (NEW.REVIEWED_BY IS NULL
                                OR NEW.REVIEWED_AT IS NULL
                                OR NEW.REVIEW_CLIENT_CHANNEL IS NULL))
                       OR (NEW.REVIEW_DEVICE_ID IS NOT NULL AND LENGTH(NEW.REVIEW_DEVICE_ID) > 100);
                END;
                """;
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_REVIEW_BI;");
            Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_REVIEW_BU;");
            Exec(conn, $"CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_REVIEW_BI BEFORE INSERT ON POM_ROUTE_EXCEPTION {reviewProvenanceChecks}");
            Exec(conn, $"CREATE TRIGGER TR_POM_ROUTE_EXCEPTION_REVIEW_BU BEFORE UPDATE ON POM_ROUTE_EXCEPTION {reviewProvenanceChecks}");
        }

        if (HasTable(conn, "POM_WORK_ORDER_EXECUTION") && HasTable(conn, "POM_WORK_ORDER"))
        {
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_WORK_ORDER_EXECUTION_PARENT_BI
                BEFORE INSERT ON POM_WORK_ORDER_EXECUTION
                WHEN NOT EXISTS (SELECT 1 FROM POM_WORK_ORDER W WHERE W.WORK_ORDER_ID = NEW.WORK_ORDER_ID)
                BEGIN
                  SELECT RAISE(ABORT, 'POM_WORK_ORDER_EXECUTION requires POM_WORK_ORDER');
                END;
                """);
            if (HasColumn(conn, "POM_WORK_ORDER_EXECUTION", "EXPECTED_VERSION") &&
                HasColumn(conn, "POM_WORK_ORDER_EXECUTION", "RESULT_VERSION"))
            {
                const string workOrderExecutionVersionChecks = """
                    BEGIN
                      SELECT RAISE(ABORT, 'POM_WORK_ORDER_EXECUTION has invalid version identity')
                        WHERE (NEW.EXPECTED_VERSION IS NULL AND NEW.RESULT_VERSION IS NOT NULL)
                           OR (NEW.EXPECTED_VERSION IS NOT NULL AND NEW.RESULT_VERSION IS NULL)
                           OR (NEW.EXPECTED_VERSION IS NOT NULL
                               AND (NEW.EXPECTED_VERSION < 1
                                    OR NEW.RESULT_VERSION <> NEW.EXPECTED_VERSION + 1));
                    END;
                    """;
                Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_EXECUTION_VERSION_BI;");
                Exec(conn, "DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_EXECUTION_VERSION_BU;");
                Exec(conn, $"CREATE TRIGGER TR_POM_WORK_ORDER_EXECUTION_VERSION_BI BEFORE INSERT ON POM_WORK_ORDER_EXECUTION {workOrderExecutionVersionChecks}");
                Exec(conn, $"CREATE TRIGGER TR_POM_WORK_ORDER_EXECUTION_VERSION_BU BEFORE UPDATE ON POM_WORK_ORDER_EXECUTION {workOrderExecutionVersionChecks}");
            }
            Exec(conn, """
                CREATE TRIGGER IF NOT EXISTS TR_POM_WORK_ORDER_CHILD_BD
                BEFORE DELETE ON POM_WORK_ORDER
                WHEN EXISTS (SELECT 1 FROM POM_LOT L WHERE L.WORK_ORDER_ID = OLD.WORK_ORDER_ID)
                  OR EXISTS (SELECT 1 FROM POM_WORK_ORDER_EXECUTION E WHERE E.WORK_ORDER_ID = OLD.WORK_ORDER_ID)
                BEGIN
                  SELECT RAISE(ABORT, 'POM_WORK_ORDER has child lot or execution rows');
                END;
                """);
        }
    }

    private static void Exec(
        SqliteConnection conn,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// SQLite 증분 실행용 SQL을 문자열 리터럴과 행 주석을 인식하면서 문장 단위로 분리한다.
    /// 주석이나 문자열 안의 세미콜론이 실제 SQL 구분자로 오인되는 것을 방지한다.
    /// </summary>
    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
        var statement = new StringBuilder();
        var inString = false;
        var inLineComment = false;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];

            if (inLineComment)
            {
                if (current is '\r' or '\n')
                {
                    inLineComment = false;
                    statement.Append(current);
                }
                continue;
            }

            if (!inString && current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                inLineComment = true;
                index++;
                continue;
            }

            if (current == '\'')
            {
                statement.Append(current);
                if (inString && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    statement.Append(sql[++index]);
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (!inString && current == ';')
            {
                var completed = statement.ToString().Trim();
                if (completed.Length > 0) yield return completed;
                statement.Clear();
                continue;
            }

            statement.Append(current);
        }

        var remainder = statement.ToString().Trim();
        if (remainder.Length > 0) yield return remainder;
    }

    /// <summary>
    /// Keeps SQLite bootstrap identity identical to the MSSQL runner: strict V### names, one file
    /// per numeric version, and numeric ordering. Validation completes before the SQLite connection
    /// is opened so a malformed release bundle cannot partially mutate a local database.
    /// </summary>
    internal static IReadOnlyList<string> GetOrderedMigrationFiles(string migrationsDirectory)
    {
        var candidates = Directory.EnumerateFiles(migrationsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException($"No migrations found at '{migrationsDirectory}'.");

        var migrations = new List<(int Version, string Name, string Path)>(candidates.Length);
        var versions = new Dictionary<int, string>();
        foreach (var path in candidates)
        {
            var name = Path.GetFileName(path);
            var match = MigrationFileNamePattern.Match(name);
            if (!match.Success)
            {
                throw new InvalidDataException(
                    $"Invalid migration file '{name}': expected V###__UPPER_SNAKE_DESCRIPTION.sql.");
            }

            if (!int.TryParse(
                    match.Groups["version"].Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var version)
                || version <= 0)
            {
                throw new InvalidDataException(
                    $"Invalid migration version in '{name}': version must be greater than zero.");
            }

            if (!versions.TryAdd(version, name))
            {
                throw new InvalidDataException(
                    $"Duplicate migration version {version}: '{versions[version]}' and '{name}'.");
            }

            migrations.Add((version, name, path));
        }

        return migrations
            .OrderBy(migration => migration.Version)
            .ThenBy(migration => migration.Name, StringComparer.Ordinal)
            .Select(migration => migration.Path)
            .ToArray();
    }

    private static string FindMigrationsDir()
    {
        // 출력 디렉터리에 복사된 사본(db/migrations)을 우선 탐지하고, 없으면 상위로 올라가며 리포 루트를 찾는다.
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var p = Path.Combine(d.FullName, "db", "migrations");
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException($"db/migrations를 {AppContext.BaseDirectory}에서 상위로 찾지 못함");
    }

    private static string ToSqlite(string s)
    {
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // 운영 SQL Server 전용 트리거/구문은 명시 마커로 제외하고 SQLite 동등 트리거는 위에서 생성한다.
        s = Regex.Replace(s, @"--\s*SQLITE-OMIT-BEGIN.*?--\s*SQLITE-OMIT-END", "",
            O | RegexOptions.Singleline);

        // 1단계 — 타입/함수 '토큰' 치환은 문자열 리터럴('...') 밖에서만 수행한다. 시드 값이 타입 키워드와
        // 겹치면 데이터가 오염된다(V079 실사고: UOM_TYPE 'Time'이 TIME→TEXT 치환에 물려 'TEXT'로 저장).
        // 홑따옴표 분할 시 짝수 인덱스가 코드 구간이고, ''(이스케이프)는 빈 코드 구간으로 떨어져 무해하다.
        var parts = s.Split('\'');
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = ReplaceTypeTokens(parts[i]);
        s = string.Join("'", parts);

        // 2단계 — '문장' 구조 재작성은 전체 텍스트에서 수행한다. 다중컬럼 ALTER가 리터럴을 품으면
        // (V011: DEFAULT 'Normal', ...) 문장 전체를 봐야 끝(;)까지 매치해 분해할 수 있다.
        // 1단계에서 DECIMAL(9,4)→NUMERIC이 끝난 뒤라 콤마 분해가 타입 인자를 자르지 않는다.
        // (한계: DEFAULT 리터럴 '안'의 콤마는 분해를 오염시킨다 — 마이그레이션 관례상 금지.)
        // MSSQL ALTER COLUMN(타입/길이 변경)은 SQLite 미지원 + TEXT는 길이 무개념이라 무의미 → 문장 제거(무해).
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+\w+\s+ALTER\s+COLUMN\s+.+?;", "", O | RegexOptions.Singleline);
        // SQLite cannot add a table constraint after creation. MSSQL keeps the real FK/CHECK constraints.
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+\w+\s+ADD\s+CONSTRAINT\s+.+?;", "", O | RegexOptions.Singleline);
        // SQLite cannot drop a named table constraint. QMS v2 rebuilds the affected table explicitly.
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+\w+\s+DROP\s+CONSTRAINT\s+\w+\s*;", "", O);
        // MSSQL 다중컬럼 ALTER TABLE t ADD c1 ..., c2 ...; → SQLite 단일 ADD COLUMN 반복
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+(\w+)\s+ADD\s+(.+?);", m =>
        {
            var tbl = m.Groups[1].Value;
            var cols = m.Groups[2].Value.Split(',');
            return string.Join("\n", cols.Select(c => $"ALTER TABLE {tbl} ADD COLUMN {c.Trim()};"));
        }, O | RegexOptions.Singleline);
        // 멱등 생성 — CREATE TABLE/INDEX에 IF NOT EXISTS 주입(증분 생성·반복 기동 안전, 빈 DB 적용엔 무해).
        s = Regex.Replace(s, @"\bCREATE\s+TABLE\s+(?!IF\s+NOT\s+EXISTS\b)", "CREATE TABLE IF NOT EXISTS ", O);
        s = Regex.Replace(s, @"\bCREATE\s+(UNIQUE\s+)?INDEX\s+(?!IF\s+NOT\s+EXISTS\b)", "CREATE $1INDEX IF NOT EXISTS ", O);
        return s;
    }

    private static string ReplaceTypeTokens(string s)
    {
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        // 문자열 타입
        s = Regex.Replace(s, @"\bN?VARCHAR\s*\(\s*\w+\s*\)", "TEXT", O);
        s = Regex.Replace(s, @"\bN?CHAR\s*\(\s*\w+\s*\)", "TEXT", O);
        s = Regex.Replace(s, @"\bN?TEXT\b", "TEXT", O);
        // 날짜/시간 (정밀도 인자 포함)
        s = Regex.Replace(s, @"\b(DATETIMEOFFSET|DATETIME2|SMALLDATETIME|DATETIME|DATE|TIME)\b(\s*\(\s*\d+\s*\))?", "TEXT", O);
        // 불리언/정수
        s = Regex.Replace(s, @"\bBIT\b", "INTEGER", O);
        s = Regex.Replace(s, @"\b(BIGINT|SMALLINT|TINYINT|INT)\b", "INTEGER", O);
        // 소수/실수
        s = Regex.Replace(s, @"\b(DECIMAL|NUMERIC|MONEY|SMALLMONEY)\b(\s*\(\s*\d+\s*(,\s*\d+\s*)?\))?", "NUMERIC", O);
        s = Regex.Replace(s, @"\b(FLOAT|REAL)\b(\s*\(\s*\d+\s*\))?", "REAL", O);
        s = Regex.Replace(s, @"\bUNIQUEIDENTIFIER\b", "TEXT", O);
        s = Regex.Replace(s, @"\bVARBINARY\s*\(\s*\w+\s*\)", "BLOB", O);
        // IDENTITY 제거
        s = Regex.Replace(s, @"\bIDENTITY\s*\(\s*\d+\s*,\s*\d+\s*\)", "", O);
        s = Regex.Replace(s, @"\bIDENTITY\b", "", O);
        // 시각 함수 → SQLite
        s = Regex.Replace(s, @"\b(GETUTCDATE|SYSUTCDATETIME|SYSDATETIME|GETDATE)\s*\(\s*\)", "CURRENT_TIMESTAMP", O);
        // SQL Server 문자열 길이 함수 → SQLite 동등 함수.
        s = Regex.Replace(s, @"\bLEN\s*\(", "LENGTH(", O);
        // 명명된 DEFAULT 제약(CONSTRAINT DF_x DEFAULT ...) → 단순 DEFAULT (SQLite ALTER ADD에서 명명 제약 미지원)
        s = Regex.Replace(s, @"CONSTRAINT\s+\w+\s+DEFAULT\b", "DEFAULT", O);
        return s;
    }
}
