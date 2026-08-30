using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Infrastructure;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// SqliteSchemaInitializer 증분 생성 경로 — 기존(빈 DB가 아닌) DB에 신규 마이그레이션 테이블이 누락돼도
/// EnsureSchema가 재기동 시 자동 생성한다(과거엔 테이블이 하나라도 있으면 전부 건너뛰어 신규 테이블이 영영 생성 안 됨).
/// 동시에 1회성 시드(INSERT)는 증분 패스에서 재실행하지 않아 시드 행이 중복되지 않음을 보장한다.
/// </summary>
public sealed class SqliteSchemaIncrementalTests
{
    private static string NewDb()
        => $"Data Source={Path.Combine(Path.GetTempPath(), $"nexa-incr-{Guid.NewGuid():N}.db")};Foreign Keys=False";

    private static string FileOf(string cs) => cs.Replace("Data Source=", "").Split(';')[0];

    private static string MigrationSql(string fileName)
        => File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations", fileName));

    private static string NamedQuerySql(string dialect, string module, string queryId)
    {
        var document = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "queries", dialect, $"{module}.xml"));
        return document.Root!
            .Elements("query")
            .Single(element => string.Equals((string?)element.Attribute("id"), queryId, StringComparison.Ordinal))
            .Element("statement")!
            .Value
            .Trim();
    }

    private static bool TableExists(string cs, string name)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static long Count(string cs, string table)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string ScalarString(string cs, string sql)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    private static void ExecSql(string cs, string sql)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static void ExecSqlWithForeignKeys(string cs, string sql)
    {
        using var c = new SqliteConnection(cs.Replace("Foreign Keys=False", "Foreign Keys=True"));
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> Columns(string cs, string table)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static bool IndexExists(string cs, string index)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name";
        cmd.Parameters.AddWithValue("@name", index);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static IReadOnlyList<string> IndexKeys(string cs, string index)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA index_xinfo([{index.Replace("]", "]]", StringComparison.Ordinal)}])";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            if (reader.GetInt64(5) != 1 || reader.IsDBNull(2)) continue;
            result.Add($"{reader.GetString(2)}:{(reader.GetInt64(3) == 1 ? "DESC" : "ASC")}");
        }
        return result;
    }

    private static string IndexSql(string cs, string index)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=@name";
        cmd.Parameters.AddWithValue("@name", index);
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static int CountIndexesWithKeys(string cs, string table, params string[] expectedKeys)
    {
        using var c = new SqliteConnection(cs); c.Open();
        var names = new List<string>();
        using (var list = c.CreateCommand())
        {
            list.CommandText = $"PRAGMA index_list([{table.Replace("]", "]]", StringComparison.Ordinal)}])";
            using var reader = list.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(1));
        }

        var matches = 0;
        foreach (var name in names)
        {
            using var info = c.CreateCommand();
            info.CommandText = $"PRAGMA index_xinfo([{name.Replace("]", "]]", StringComparison.Ordinal)}])";
            using var reader = info.ExecuteReader();
            var keys = new List<string>();
            while (reader.Read())
            {
                if (reader.GetInt64(5) == 1 && !reader.IsDBNull(2)) keys.Add(reader.GetString(2));
            }
            if (keys.SequenceEqual(expectedKeys, StringComparer.OrdinalIgnoreCase)) matches++;
        }
        return matches;
    }

    private static string QueryPlan(string cs, string sql)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        using var reader = cmd.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        return string.Join(Environment.NewLine, details);
    }

    private static void CreatePreV115EmsTables(string cs)
    {
        ExecSql(cs, """
            CREATE TABLE EMS_WORK_ORDER (
                WO_ID TEXT NOT NULL PRIMARY KEY,
                PLAN_ID TEXT NULL,
                EQUIPMENT_ID TEXT NOT NULL,
                WO_TYPE TEXT NOT NULL,
                DESCRIPTION TEXT NULL,
                ASSIGNEE_ID TEXT NOT NULL,
                ISSUED_AT TEXT NOT NULL,
                STARTED_AT TEXT NULL,
                COMPLETED_AT TEXT NULL,
                STATUS TEXT NOT NULL DEFAULT 'Issued',
                FAILURE_CODE_ID TEXT NULL,
                REMARK TEXT NULL,
                CREATED_BY TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UPDATED_BY TEXT NOT NULL,
                UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE EMS_SPARE_PART_INOUT (
                INOUT_ID TEXT NOT NULL PRIMARY KEY,
                PART_ID TEXT NOT NULL,
                TRANSACTION_TYPE TEXT NOT NULL,
                QUANTITY NUMERIC NULL,
                FROM_LOCATION TEXT NULL,
                TO_LOCATION TEXT NULL,
                TRANSACTION_AT TEXT NOT NULL,
                PROCESSED_BY TEXT NULL,
                REMARK TEXT NULL,
                CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO EMS_SPARE_PART_INOUT
              (INOUT_ID, PART_ID, TRANSACTION_TYPE, QUANTITY, TRANSACTION_AT, PROCESSED_BY)
            VALUES ('LEGACY_INOUT', 'ORPHAN_SOFT_PART', 'Incoming', NULL, CURRENT_TIMESTAMP, NULL);
            """);
    }

    private static void CreatePreV117EquipmentOutputTable(string cs)
    {
        ExecSql(cs, """
            CREATE TABLE EST_EQUIPMENT_OUTPUT_EVENT (
                OUTPUT_EVENT_ID TEXT NOT NULL PRIMARY KEY,
                IDEMPOTENCY_KEY TEXT NOT NULL,
                REQUEST_HASH TEXT NOT NULL,
                PLANT_ID TEXT NOT NULL,
                EQUIPMENT_ID TEXT NOT NULL,
                OUTPUT_TYPE TEXT NOT NULL,
                CARRIER_ID TEXT NULL,
                PROCESS_LOT_ID TEXT NULL,
                WORK_ORDER_ID TEXT NULL,
                PROCESS_ID TEXT NULL,
                RECIPE_ID TEXT NULL,
                RECIPE_VERSION INTEGER NULL,
                TOTAL_QTY NUMERIC NOT NULL,
                GOOD_QTY NUMERIC NOT NULL,
                DEFECT_QTY NUMERIC NOT NULL,
                UNIT TEXT NOT NULL,
                SOURCE TEXT NOT NULL,
                SOURCE_EVENT_ID TEXT NULL,
                ACTOR_ID TEXT NOT NULL,
                CORRELATION_ID TEXT NULL,
                METADATA_JSON TEXT NULL,
                OCCURRED_AT TEXT NOT NULL,
                CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO EST_EQUIPMENT_OUTPUT_EVENT
              (OUTPUT_EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
               OUTPUT_TYPE, CARRIER_ID, PROCESS_LOT_ID, TOTAL_QTY, GOOD_QTY, DEFECT_QTY,
               UNIT, SOURCE, ACTOR_ID, OCCURRED_AT)
            VALUES
              ('OUT_NON_LOT', 'IDEM_NON_LOT', 'HASH_NON_LOT', 'P1', 'EQ1',
               'CarrierCleaned', 'C1', NULL, 1, 1, 0, 'EA', 'TEST', 'tester', CURRENT_TIMESTAMP),
              ('OUT_LOT', 'IDEM_LOT', 'HASH_LOT', 'P1', 'EQ1',
               'TrackOut', NULL, 'LOT1', 1, 1, 0, 'EA', 'TEST', 'tester', CURRENT_TIMESTAMP);
            """);
    }

    private static void CreatePreV127UtilityTables(string cs)
    {
        ExecSql(cs, """
            CREATE TABLE EST_UTILITY_METER (
                METER_ID TEXT NOT NULL PRIMARY KEY,
                METER_NAME TEXT NOT NULL,
                PLANT_ID TEXT NOT NULL,
                EQUIPMENT_ID TEXT NULL,
                UTILITY_TYPE TEXT NOT NULL,
                UNIT TEXT NOT NULL,
                FDC_PARAMETER_ID TEXT NULL,
                READING_MODE TEXT NOT NULL DEFAULT 'Cumulative',
                SCALE_FACTOR NUMERIC NOT NULL DEFAULT 1,
                COST_PER_UNIT NUMERIC NULL,
                CARBON_PER_UNIT NUMERIC NULL,
                IS_ACTIVE INTEGER NOT NULL DEFAULT 1,
                CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE EST_UTILITY_READING (
                READING_ID TEXT NOT NULL PRIMARY KEY,
                METER_ID TEXT NOT NULL,
                EQUIPMENT_ID TEXT NULL,
                PROCESS_LOT_ID TEXT NULL,
                WORK_ORDER_ID TEXT NULL,
                RECIPE_ID TEXT NULL,
                RECIPE_VERSION INTEGER NULL,
                RAW_VALUE NUMERIC NOT NULL,
                NORMALIZED_VALUE NUMERIC NOT NULL,
                UNIT TEXT NOT NULL,
                SOURCE TEXT NOT NULL,
                SOURCE_EVENT_ID TEXT NOT NULL,
                REQUEST_HASH TEXT NOT NULL,
                QUALITY TEXT NOT NULL DEFAULT 'Good',
                RECORDED_AT TEXT NOT NULL,
                RECORDED_BY TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE EST_UTILITY_METER_EVENT (
                EVENT_ID TEXT NOT NULL PRIMARY KEY,
                IDEMPOTENCY_KEY TEXT NOT NULL,
                REQUEST_HASH TEXT NOT NULL,
                METER_ID TEXT NOT NULL,
                PLANT_ID TEXT NOT NULL,
                EQUIPMENT_ID TEXT NULL,
                EVENT_TYPE TEXT NOT NULL,
                OCCURRED_AT TEXT NOT NULL,
                REASON TEXT NOT NULL,
                PREVIOUS_VALUE NUMERIC NULL,
                AFTER_VALUE NUMERIC NULL,
                BASELINE_VALUE NUMERIC NULL,
                UNIT TEXT NOT NULL,
                ACTOR_USER_ID TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO EST_UTILITY_METER
              (METER_ID, METER_NAME, PLANT_ID, EQUIPMENT_ID, UTILITY_TYPE, UNIT,
               READING_MODE, SCALE_FACTOR, COST_PER_UNIT, CARBON_PER_UNIT,
               UPDATED_BY, UPDATED_AT)
            VALUES ('M_PRE127', 'Legacy power', 'P_LEGACY', 'EQ_LEGACY', 'Electricity', 'kWh',
                    'Delta', 2.5, 11.25, 0.42, 'legacy-operator', '2025-01-02 03:04:05');
            INSERT INTO EST_UTILITY_READING
              (READING_ID, METER_ID, RAW_VALUE, NORMALIZED_VALUE, UNIT, SOURCE,
               SOURCE_EVENT_ID, REQUEST_HASH, RECORDED_AT, RECORDED_BY)
            VALUES ('R_PRE127', 'M_PRE127', 4, 10, 'kWh', 'PLC', 'PLC-1', 'HASH-1',
                    '2025-01-02 04:00:00', 'legacy-operator');
            """);
    }

    private static void SeedEmsMaintenancePlan(string cs, string planId)
    {
        ExecSql(cs, $"""
            INSERT INTO EMS_MAINTENANCE_PLAN
              (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE, SCHEDULED_DATE,
               ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS)
            VALUES ('{planId}', '{planId}', 'EQ1', 'Preventive', 'Manual', CURRENT_TIMESTAMP,
                    1, 'operator', 'Planned');
            """);
    }

    private static void SeedEmsWorkOrder(
        string cs, string workOrderId, string? legacyPlanId = null, string? maintenancePlanId = null)
    {
        var legacyPlan = legacyPlanId is null ? "NULL" : $"'{legacyPlanId}'";
        var maintenancePlan = maintenancePlanId is null ? "NULL" : $"'{maintenancePlanId}'";
        ExecSql(cs, $"""
            INSERT INTO EMS_WORK_ORDER
              (WO_ID, PLAN_ID, MAINTENANCE_PLAN_ID, EQUIPMENT_ID, WO_TYPE, ASSIGNEE_ID,
               ISSUED_AT, STATUS, CREATED_BY, UPDATED_BY)
            VALUES ('{workOrderId}', {legacyPlan}, {maintenancePlan}, 'EQ1', 'Preventive', 'operator',
                    CURRENT_TIMESTAMP, 'Issued', 'TEST', 'TEST');
            """);
    }

    private static void InsertEmsSparePartExecution(
        string cs,
        string inoutId,
        decimal? quantity,
        decimal? balanceBefore,
        decimal? balanceAfter,
        string? clientChannel,
        string? workOrderId,
        string? processedBy = "operator")
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO EMS_SPARE_PART_INOUT
              (INOUT_ID, PART_ID, TRANSACTION_TYPE, QUANTITY, TRANSACTION_AT, PROCESSED_BY,
               IDEMPOTENCY_KEY, BALANCE_BEFORE, BALANCE_AFTER, CLIENT_CHANNEL, WO_ID)
            VALUES (@id, 'ORPHAN_SOFT_PART', 'Incoming', @quantity, CURRENT_TIMESTAMP, @processedBy,
                    @idempotencyKey, @balanceBefore, @balanceAfter, @clientChannel, @workOrderId);
            """;
        cmd.Parameters.AddWithValue("@id", inoutId);
        cmd.Parameters.AddWithValue("@quantity", (object?)quantity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@processedBy", (object?)processedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@idempotencyKey", $"IDEMPOTENCY_{inoutId}");
        cmd.Parameters.AddWithValue("@balanceBefore", (object?)balanceBefore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@balanceAfter", (object?)balanceAfter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@clientChannel", (object?)clientChannel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@workOrderId", (object?)workOrderId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void SeedPomRoutingRows(string cs)
    {
        ExecSql(cs, """
            INSERT INTO POM_PRODUCTION_PLAN
              (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY, PLANNED_START_DATE,
               PLANNED_END_DATE, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('RP_PLAN', 'RP_PLAN', 'P1', 'ITEM1', 10, CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP, 'Released', 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO POM_PRODUCTION_ORDER
              (ORDER_ID, PLAN_ID, EQUIPMENT_ID, PRODUCT_ID, ORDER_QTY, SCHEDULED_START,
               SCHEDULED_END, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('RP_ORDER', 'RP_PLAN', 'EQ1', 'ITEM1', 10, CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP, 'Issued', 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO POM_WORK_ORDER
              (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCTION_ORDER_ID, EQUIPMENT_ID,
               PRODUCT_ID, PROCESS_ID, PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY,
               STATUS, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('RP_WO', 'P1', 'RP_WO', 'RP_ORDER', 'EQ1',
                    'ITEM1', 'OP10', 10, 0, 0, 0,
                    'Released', 'N', 1, 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO POM_LOT
              (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE,
               PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, CONTROL_MODE, RETURN_STEP,
               IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT)
            VALUES ('RP_LOT', 'P1', 'RP_WO', 'ITEM1', 10, 0, 'Queued',
                    'Idle', 'OP10>OP20', 0, 'Strict', NULL,
                    'N', 1, 'TEST', CURRENT_TIMESTAMP);
            """);
    }

    private static void AssertPomRoutingConstraints(string cs)
    {
        ExecSql(cs, """
            INSERT INTO POM_WORK_ORDER
              (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCTION_ORDER_ID, EQUIPMENT_ID,
               PRODUCT_ID, ROUTING_SCOPE, ROUTING_ID, ROUTING_STEP_NO,
               PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY,
               STATUS, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('RP_WO_SERIAL', 'P1', 'RP_WO_SERIAL', 'RP_ORDER', 'EQ1',
                    'ITEM1', 'SerialRoute', 'RT1', NULL,
                    10, 0, 0, 0, 'Released', 'N', 1,
                    'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO POM_WORK_ORDER
              (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCTION_ORDER_ID, EQUIPMENT_ID,
               PRODUCT_ID, PROCESS_ID, ROUTING_SCOPE, ROUTING_ID, ROUTING_STEP_NO,
               PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY,
               STATUS, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('RP_WO_OPERATION', 'P1', 'RP_WO_OPERATION', 'RP_ORDER', 'EQ1',
                    'ITEM1', 'OP10', 'Operation', 'RT1', 10,
                    10, 0, 0, 0, 'Released', 'N', 1,
                    'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            """);
        ScalarString(cs, "SELECT ROUTING_SCOPE FROM POM_WORK_ORDER WHERE WORK_ORDER_ID='RP_WO_SERIAL'")
            .Should().Be("SerialRoute");
        ScalarString(cs, "SELECT ROUTING_SCOPE FROM POM_WORK_ORDER WHERE WORK_ORDER_ID='RP_WO_OPERATION'")
            .Should().Be("Operation");

        Action invalidWorkOrderInsert = () => ExecSql(cs, """
            INSERT INTO POM_WORK_ORDER
              (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCTION_ORDER_ID, EQUIPMENT_ID,
               PRODUCT_ID, PROCESS_ID, ROUTING_ID, ROUTING_STEP_NO,
               PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY,
               STATUS, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('RP_WO_BAD', 'P1', 'RP_WO_BAD', 'RP_ORDER', 'EQ1',
                    'ITEM1', 'OP20', 'RT1', NULL,
                    10, 0, 0, 0, 'Released', 'N', 1,
                    'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            """);
        invalidWorkOrderInsert.Should().Throw<SqliteException>();

        Action invalidWorkOrderUpdate = () => ExecSql(cs,
            "UPDATE POM_WORK_ORDER SET ROUTING_ID='RT1', ROUTING_STEP_NO=NULL WHERE WORK_ORDER_ID='RP_WO';");
        invalidWorkOrderUpdate.Should().Throw<SqliteException>();
        Action invalidSerialStep = () => ExecSql(cs,
            "UPDATE POM_WORK_ORDER SET ROUTING_STEP_NO=10 WHERE WORK_ORDER_ID='RP_WO_SERIAL';");
        invalidSerialStep.Should().Throw<SqliteException>();
        Action invalidSerialProcess = () => ExecSql(cs,
            "UPDATE POM_WORK_ORDER SET PROCESS_ID='OP10' WHERE WORK_ORDER_ID='RP_WO_SERIAL';");
        invalidSerialProcess.Should().Throw<SqliteException>();
        Action invalidOperationStep = () => ExecSql(cs,
            "UPDATE POM_WORK_ORDER SET ROUTING_STEP_NO=NULL WHERE WORK_ORDER_ID='RP_WO_OPERATION';");
        invalidOperationStep.Should().Throw<SqliteException>();
        Action invalidRoutingScope = () => ExecSql(cs,
            "UPDATE POM_WORK_ORDER SET ROUTING_SCOPE='Unknown' WHERE WORK_ORDER_ID='RP_WO';");
        invalidRoutingScope.Should().Throw<SqliteException>();

        Action invalidLotInsert = () => ExecSql(cs, """
            INSERT INTO POM_LOT
              (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
               ROUTE_STEPS, CURRENT_STEP, CONTROL_MODE, RETURN_STEP,
               IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT)
            VALUES ('RP_LOT_BAD', 'P1', 'ITEM1', 1, 0, 'Queued', 'Idle',
                    'OP10', 0, 'Unsafe', NULL, 'N', 1, 'TEST', CURRENT_TIMESTAMP);
            """);
        invalidLotInsert.Should().Throw<SqliteException>();

        Action invalidLotUpdate = () => ExecSql(cs,
            "UPDATE POM_LOT SET RETURN_STEP=-1 WHERE LOT_ID='RP_LOT';");
        invalidLotUpdate.Should().Throw<SqliteException>();

        Action invalidExecutionInsert = () => ExecSql(cs, """
            INSERT INTO POM_LOT_EXECUTION
              (EXECUTION_ID, LOT_ID, ACTION, IDEMPOTENCY_KEY, REQUEST_HASH,
               EXPECTED_VERSION, RESULT_VERSION, FROM_STEP, CONTROL_MODE, CLIENT_CHANNEL,
               CREATED_BY, CREATED_AT)
            VALUES ('RP_EXEC_BAD', 'RP_LOT', 'Bypass', 'RP_EXEC_BAD', 'HASH',
                    1, 2, -1, 'Strict', 'MES', 'TEST', CURRENT_TIMESTAMP);
            """);
        invalidExecutionInsert.Should().Throw<SqliteException>();

        ExecSql(cs, """
            INSERT INTO POM_LOT_EXECUTION
              (EXECUTION_ID, LOT_ID, ACTION, IDEMPOTENCY_KEY, REQUEST_HASH,
               EXPECTED_VERSION, RESULT_VERSION, FROM_STEP, CONTROL_MODE, CLIENT_CHANNEL,
               CREATED_BY, CREATED_AT)
            VALUES ('RP_EXEC', 'RP_LOT', 'TrackIn', 'RP_EXEC', 'HASH',
                    1, 2, 0, 'Strict', 'MES', 'TEST', CURRENT_TIMESTAMP);
            """);
        Action invalidExecutionUpdate = () => ExecSql(cs,
            "UPDATE POM_LOT_EXECUTION SET CLIENT_CHANNEL='UNKNOWN' WHERE EXECUTION_ID='RP_EXEC';");
        invalidExecutionUpdate.Should().Throw<SqliteException>();

        ExecSql(cs, """
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, DEVICE_ID, OCCURRED_AT, CREATED_AT)
            VALUES ('RP_EXEC', 'RP_LOT', 'P1', 'OP10', 'D_VALID', 1,
                    'operator', 'POP', 'KIOSK-01', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        Action invalidDefectQty = () => ExecSql(cs, """
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, OCCURRED_AT, CREATED_AT)
            VALUES ('RP_EXEC', 'RP_LOT', 'P1', 'OP10', 'D_ZERO', 0,
                    'operator', 'MES', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        invalidDefectQty.Should().Throw<SqliteException>();
        Action invalidDefectChannel = () => ExecSql(cs, """
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, OCCURRED_AT, CREATED_AT)
            VALUES ('RP_EXEC', 'RP_LOT', 'P1', 'OP10', 'D_CHANNEL', 1,
                    'operator', 'UNKNOWN', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        invalidDefectChannel.Should().Throw<SqliteException>();
        Action duplicateDefectCode = () => ExecSql(cs, """
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, OCCURRED_AT, CREATED_AT)
            VALUES ('RP_EXEC', 'RP_LOT', 'P1', 'OP10', 'D_VALID', 2,
                    'operator', 'MES', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        duplicateDefectCode.Should().Throw<SqliteException>();
        Action updateDefectPlantOnly = () => ExecSql(cs, """
            UPDATE POM_LOT_DEFECT_EXECUTION
               SET PLANT_ID='OTHER_PLANT'
             WHERE EXECUTION_ID='RP_EXEC' AND DEFECT_CODE='D_VALID';
            """);
        updateDefectPlantOnly.Should().Throw<SqliteException>();
        Action missingExecution = () => ExecSqlWithForeignKeys(cs, """
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, OCCURRED_AT, CREATED_AT)
            VALUES ('RP_EXEC_MISSING', 'RP_LOT', 'P1', 'OP10', 'D_ORPHAN', 1,
                    'operator', 'MES', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        missingExecution.Should().Throw<SqliteException>();

        ExecSql(cs, """
            INSERT INTO POM_LOT
              (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE,
               PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, CONTROL_MODE, RETURN_STEP,
               IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT)
            VALUES ('RP_LOT_OTHER', 'P1', 'RP_WO', 'ITEM1', 10, 0, 'Queued',
                    'Idle', 'OP10>OP20', 0, 'Strict', NULL,
                    'N', 1, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO POM_LOT_EXECUTION
              (EXECUTION_ID, LOT_ID, ACTION, IDEMPOTENCY_KEY, REQUEST_HASH,
               EXPECTED_VERSION, RESULT_VERSION, FROM_STEP, CONTROL_MODE, CLIENT_CHANNEL,
               CREATED_BY, CREATED_AT)
            VALUES ('RP_EXEC_OTHER', 'RP_LOT_OTHER', 'TrackIn', 'RP_EXEC_OTHER', 'HASH',
                    1, 2, 0, 'Strict', 'MES', 'TEST', CURRENT_TIMESTAMP);
            """);
        Action crossLotDefect = () => ExecSql(cs, """
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, OCCURRED_AT, CREATED_AT)
            VALUES ('RP_EXEC', 'RP_LOT_OTHER', 'P1', 'OP10', 'D_CROSS_LOT', 1,
                    'operator', 'MES', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        crossLotDefect.Should().Throw<SqliteException>();
        Action wrongPlantDefect = () => ExecSql(cs, """
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, OCCURRED_AT, CREATED_AT)
            VALUES ('RP_EXEC', 'RP_LOT', 'OTHER_PLANT', 'OP10', 'D_WRONG_PLANT', 1,
                    'operator', 'MES', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        wrongPlantDefect.Should().Throw<SqliteException>();

        ExecSql(cs, """
            INSERT INTO POM_ROUTE_EXCEPTION
              (EXCEPTION_ID, LOT_ID, PLANT_ID, DEVIATION_TYPE, FROM_STEP, TO_STEP,
               FROM_PROCESS_ID, TO_PROCESS_ID, BOUND_LOT_VERSION, REASON, STATUS,
               REQUESTED_BY, REQUESTED_AT, EXPIRES_AT, CLIENT_CHANNEL, CREATED_AT, UPDATED_AT)
            VALUES ('RP_REVIEW', 'RP_LOT', 'P1', 'Bypass', 0, 1,
                    'OP10', 'OP20', 1, 'review audit', 'Requested',
                    'operator', CURRENT_TIMESTAMP, DATETIME(CURRENT_TIMESTAMP, '+1 hour'),
                    'MES', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            UPDATE POM_ROUTE_EXCEPTION
               SET STATUS='Approved', REVIEWED_BY='supervisor', REVIEWED_AT=CURRENT_TIMESTAMP,
                   REVIEW_CLIENT_CHANNEL='MOBILE', REVIEW_DEVICE_ID='PDA-01'
             WHERE EXCEPTION_ID='RP_REVIEW';
            """);
        Action invalidReviewChannel = () => ExecSql(cs,
            "UPDATE POM_ROUTE_EXCEPTION SET REVIEW_CLIENT_CHANNEL='UNKNOWN' WHERE EXCEPTION_ID='RP_REVIEW';");
        invalidReviewChannel.Should().Throw<SqliteException>();
        Action invalidReviewDevice = () => ExecSql(cs,
            $"UPDATE POM_ROUTE_EXCEPTION SET REVIEW_DEVICE_ID='{new string('D', 101)}' WHERE EXCEPTION_ID='RP_REVIEW';");
        invalidReviewDevice.Should().Throw<SqliteException>();

        ExecSql(cs, """
            INSERT INTO POM_ROUTE_EXCEPTION
              (EXCEPTION_ID, LOT_ID, PLANT_ID, DEVIATION_TYPE, FROM_STEP, TO_STEP,
               FROM_PROCESS_ID, TO_PROCESS_ID, BOUND_LOT_VERSION, REASON, STATUS,
               REQUESTED_BY, REQUESTED_AT, EXPIRES_AT, CLIENT_CHANNEL, CREATED_AT, UPDATED_AT)
            VALUES ('RP_REVIEW_OTHER', 'RP_LOT_OTHER', 'P1', 'Bypass', 0, 1,
                    'OP10', 'OP20', 1, 'cross-lot audit', 'Requested',
                    'operator', CURRENT_TIMESTAMP, DATETIME(CURRENT_TIMESTAMP, '+1 hour'),
                    'MES', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        Action crossLotExecutionException = () => ExecSql(cs,
            "UPDATE POM_LOT_EXECUTION SET ROUTE_EXCEPTION_ID='RP_REVIEW_OTHER' WHERE EXECUTION_ID='RP_EXEC';");
        crossLotExecutionException.Should().Throw<SqliteException>();
        Action crossLotAppliedExecution = () => ExecSql(cs, """
            UPDATE POM_ROUTE_EXCEPTION
               SET STATUS='Applied', REVIEWED_BY='supervisor', REVIEWED_AT=CURRENT_TIMESTAMP,
                   REVIEW_CLIENT_CHANNEL='MES', APPLIED_BY='supervisor',
                   APPLIED_AT=CURRENT_TIMESTAMP, APPLIED_EXECUTION_ID='RP_EXEC'
             WHERE EXCEPTION_ID='RP_REVIEW_OTHER';
            """);
        crossLotAppliedExecution.Should().Throw<SqliteException>();

        ExecSql(cs, """
            INSERT INTO POM_WORK_ORDER_EXECUTION
              (EXECUTION_ID, WORK_ORDER_ID, IDEMPOTENCY_KEY, ACTION, FROM_STATUS, TO_STATUS,
               USER_ID, CLIENT_CHANNEL, OCCURRED_AT, EXPECTED_VERSION, RESULT_VERSION,
               CREATED_BY, CREATED_AT)
            VALUES ('RP_WO_EXEC', 'RP_WO', 'RP_WO_EXEC', 'Start', 'Released', 'Started',
                    'operator', 'MES', CURRENT_TIMESTAMP, 1, 2, 'operator', CURRENT_TIMESTAMP);
            """);
        Action invalidWorkOrderExecutionVersion = () => ExecSql(cs, """
            INSERT INTO POM_WORK_ORDER_EXECUTION
              (EXECUTION_ID, WORK_ORDER_ID, IDEMPOTENCY_KEY, ACTION, FROM_STATUS, TO_STATUS,
               USER_ID, CLIENT_CHANNEL, OCCURRED_AT, EXPECTED_VERSION, RESULT_VERSION,
               CREATED_BY, CREATED_AT)
            VALUES ('RP_WO_EXEC_BAD', 'RP_WO', 'RP_WO_EXEC_BAD', 'Start', 'Released', 'Started',
                    'operator', 'MES', CURRENT_TIMESTAMP, 1, 3, 'operator', CURRENT_TIMESTAMP);
            """);
        invalidWorkOrderExecutionVersion.Should().Throw<SqliteException>();
    }

    [Fact]
    public void EnsureSchema_ExistingDbMissingTable_AutoCreatesIt()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);                 // 전체 스키마 1회 적용(현 코드 기준 전 테이블)
            TableExists(cs, "MDM_BOM").Should().BeTrue();
            ExecSql(cs, "DROP TABLE MDM_BOM;");                 // 신규 마이그레이션 테이블이 아직 없는 구 DB 모사
            TableExists(cs, "MDM_BOM").Should().BeFalse();

            SqliteSchemaInitializer.EnsureSchema(cs);          // 기존 DB → 증분 생성 경로

            TableExists(cs, "MDM_BOM").Should().BeTrue();      // 누락 테이블이 자동 생성됨
            TableExists(cs, "MDM_QTIME_ACTION").Should().BeTrue();
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Fact]
    public void V152_work_scope_member_schema_supports_carrierless_batch_execution_and_guards_membership()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);

            TableExists(cs, "POM_WORK_SCOPE").Should().BeTrue();
            TableExists(cs, "POM_WORK_SCOPE_MEMBER").Should().BeTrue();
            IndexExists(cs, "UX_POM_WORK_SCOPE_MEMBER_SEQUENCE").Should().BeTrue(
                "member sequence allocation must be unique under concurrent parent inserts");
            Columns(cs, "POM_WORK_SCOPE").Should().Contain("CREATE_IDEMPOTENCY_KEY");
            Columns(cs, "POM_WORK_SCOPE_EXECUTION").Should().Contain("RESULT_CODE");
            Columns(cs, "POM_WORK_SCOPE_EXECUTION").Should().Contain("RESULT_METADATA_JSON");
            Columns(cs, "EST_EQUIPMENT_OUTPUT_EVENT").Should().Contain("WORK_SCOPE_ID");
            Columns(cs, "IVT_MATERIAL_CONSUMPTION_HISTORY").Should().Contain("CARRIER_ID");

            ExecSql(cs, """
                INSERT INTO POM_WORK_SCOPE
                  (WORK_SCOPE_ID, PLANT_ID, SCOPE_TYPE, TARGET_ID, NAME, PLAN_QTY,
                   STATUS, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT,
                   CREATE_IDEMPOTENCY_KEY, CREATE_REQUEST_HASH)
                VALUES ('V152-CAMP', 'P1', 'Campaign', 'CAMP-01', 'Campaign', 10,
                        'Created', 'N', 1, 'tester', CURRENT_TIMESTAMP, 'tester', CURRENT_TIMESTAMP,
                        'v152-create-camp', lower(hex(randomblob(32))));
                INSERT INTO POM_WORK_SCOPE
                  (WORK_SCOPE_ID, PLANT_ID, SCOPE_TYPE, TARGET_ID, NAME, PARENT_SCOPE_ID,
                   PLAN_QTY, STATUS, IS_HOLD, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT,
                   CREATE_IDEMPOTENCY_KEY, CREATE_REQUEST_HASH)
                VALUES ('V152-BATCH', 'P1', 'Batch', 'BATCH-01', 'Batch', 'V152-CAMP', 10,
                        'Created', 'N', 1, 'tester', CURRENT_TIMESTAMP, 'tester', CURRENT_TIMESTAMP,
                        'v152-create-batch', lower(hex(randomblob(32))));
                INSERT INTO POM_WORK_SCOPE_MEMBER
                  (MEMBER_ID, WORK_SCOPE_ID, MEMBER_SCOPE_ID, MEMBER_TYPE, MEMBER_TARGET_ID,
                   SEQUENCE_NO, IDEMPOTENCY_KEY, CREATED_BY, CREATED_AT)
                VALUES ('V152-MEMBER', 'V152-CAMP', 'V152-BATCH', 'Batch', 'BATCH-01',
                        1, 'v152-member-batch', 'tester', CURRENT_TIMESTAMP);
                """);
            Count(cs, "POM_WORK_SCOPE_MEMBER").Should().Be(1);

            Action invalidMember = () => ExecSql(cs, """
                INSERT INTO POM_WORK_SCOPE_MEMBER
                  (MEMBER_ID, WORK_SCOPE_ID, MEMBER_SCOPE_ID, MEMBER_TYPE, MEMBER_TARGET_ID,
                   SEQUENCE_NO, IDEMPOTENCY_KEY, CREATED_BY, CREATED_AT)
                VALUES ('V152-BAD', 'V152-CAMP', 'V152-BATCH', 'Batch', 'WRONG-TARGET',
                        2, 'v152-member-bad', 'tester', CURRENT_TIMESTAMP);
                """);
            invalidMember.Should().Throw<SqliteException>();

            Action deleteMember = () => ExecSql(cs,
                "DELETE FROM POM_WORK_SCOPE_MEMBER WHERE MEMBER_ID='V152-MEMBER';");
            deleteMember.Should().Throw<SqliteException>();
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Fact]
    public void EnsureSchema_ExistingDb_DoesNotReRunSeedInserts()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);                 // V031이 SYS_ROLE 시드(INSERT)를 1회 수행
            var before = Count(cs, "SYS_ROLE");
            before.Should().BeGreaterThan(0);

            SqliteSchemaInitializer.EnsureSchema(cs);          // 증분 패스는 INSERT를 실행하지 않음

            Count(cs, "SYS_ROLE").Should().Be(before);         // 시드 행 중복 없음
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Fact]
    public void Apply_seeds_standard_roles_with_named_query_read_permissions()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);

            ScalarString(cs, "SELECT PERMISSIONS FROM SYS_ROLE WHERE ROLE_ID = 'OPERATOR'")
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Should().BeEquivalentTo(new[]
                {
                    "fdc:control", "fdc:read", "mdm:read", "est:read", "pom:read", "pom:execute",
                    "pom:routing.request", "rms:read",
                });
            ScalarString(cs, "SELECT PERMISSIONS FROM SYS_ROLE WHERE ROLE_ID = 'MAINTENANCE'")
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Should().BeEquivalentTo(new[]
                {
                    "fdc:read", "mdm:read", "ems:read", "ems:manage", "est:read", "pom:read", "rms:read",
                });
            ScalarString(cs, "SELECT PERMISSIONS FROM SYS_ROLE WHERE ROLE_ID = 'VIEWER'")
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Should().Equal("fdc:read");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Fact]
    public void EnsureSchema_ExistingDb_UpgradesOnlyUntouchedOperatorReadPermissions()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, "UPDATE SYS_ROLE SET PERMISSIONS = 'fdc:control|fdc:read' WHERE ROLE_ID = 'OPERATOR'");
            ExecSql(cs, "INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT) " +
                        "VALUES ('CUSTOM_OPS', 'Custom', '', 'fdc:read|qms:read', 0, 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP)");

            SqliteSchemaInitializer.EnsureSchema(cs);

            ScalarString(cs, "SELECT PERMISSIONS FROM SYS_ROLE WHERE ROLE_ID = 'OPERATOR'")
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Should().BeEquivalentTo(new[]
                {
                    "fdc:control", "fdc:read", "mdm:read", "est:read", "pom:read", "pom:execute",
                    "pom:routing.request", "rms:read",
                });
            ScalarString(cs, "SELECT PERMISSIONS FROM SYS_ROLE WHERE ROLE_ID = 'CUSTOM_OPS'")
                .Should().Be("fdc:read|qms:read", "사용자가 만든 역할 권한은 증분 보정이 변경하면 안 된다");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Fact]
    public void EnsureSchema_existing_output_ledger_backfills_and_enforces_v117_scope()
    {
        var cs = NewDb();
        try
        {
            CreatePreV117EquipmentOutputTable(cs);

            SqliteSchemaInitializer.EnsureSchema(cs);

            Columns(cs, "EST_EQUIPMENT_OUTPUT_EVENT").Should().Contain("IS_LOT_OUTPUT");
            ScalarString(cs, "SELECT IS_LOT_OUTPUT FROM EST_EQUIPMENT_OUTPUT_EVENT WHERE OUTPUT_EVENT_ID='OUT_NON_LOT'")
                .Should().Be("0", "legacy equipment/carrier output has no process-lot identity");
            ScalarString(cs, "SELECT IS_LOT_OUTPUT FROM EST_EQUIPMENT_OUTPUT_EVENT WHERE OUTPUT_EVENT_ID='OUT_LOT'")
                .Should().Be("1", "legacy process-lot identity is the V117 one-time classification evidence");

            Action nullScope = () => ExecSql(cs, """
                INSERT INTO EST_EQUIPMENT_OUTPUT_EVENT
                  (OUTPUT_EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                   OUTPUT_TYPE, TOTAL_QTY, GOOD_QTY, DEFECT_QTY, UNIT, SOURCE, ACTOR_ID,
                   OCCURRED_AT, IS_LOT_OUTPUT)
                VALUES ('OUT_NULL_SCOPE', 'IDEM_NULL_SCOPE', 'HASH_NULL_SCOPE', 'P1', 'EQ1',
                        'CarrierCleaned', 1, 1, 0, 'EA', 'TEST', 'tester', CURRENT_TIMESTAMP, NULL);
                """);
            nullScope.Should().Throw<SqliteException>("SQLite must emulate V117 NOT NULL for upgraded tables");

            Action lotScopeWithoutLot = () => ExecSql(cs, """
                INSERT INTO EST_EQUIPMENT_OUTPUT_EVENT
                  (OUTPUT_EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                   OUTPUT_TYPE, TOTAL_QTY, GOOD_QTY, DEFECT_QTY, UNIT, SOURCE, ACTOR_ID,
                   OCCURRED_AT, IS_LOT_OUTPUT)
                VALUES ('OUT_BAD_LOT_SCOPE', 'IDEM_BAD_LOT_SCOPE', 'HASH_BAD_LOT_SCOPE', 'P1', 'EQ1',
                        'TrackOut', 1, 1, 0, 'EA', 'TEST', 'tester', CURRENT_TIMESTAMP, 1);
                """);
            lotScopeWithoutLot.Should().Throw<SqliteException>(
                "IS_LOT_OUTPUT=1 requires durable PROCESS_LOT_ID evidence");

            Action invalidScopeUpdate = () => ExecSql(cs,
                "UPDATE EST_EQUIPMENT_OUTPUT_EVENT SET IS_LOT_OUTPUT=2 WHERE OUTPUT_EVENT_ID='OUT_NON_LOT';");
            invalidScopeUpdate.Should().Throw<SqliteException>("SQLite must emulate the V117 0/1 CHECK on updates");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_existing_utility_rows_seed_v1_history_and_backfill_immutable_snapshots_idempotently()
    {
        var cs = NewDb();
        try
        {
            CreatePreV127UtilityTables(cs);

            SqliteSchemaInitializer.EnsureSchema(cs);
            SqliteSchemaInitializer.EnsureSchema(cs);

            Count(cs, "EST_UTILITY_METER_CONFIG_HISTORY").Should().Be(1,
                "the V127 upgrade seed must be durable and idempotent");
            ScalarString(cs, "SELECT CONFIG_VERSION FROM EST_UTILITY_METER_CONFIG_HISTORY WHERE METER_ID='M_PRE127'")
                .Should().Be("1");
            ScalarString(cs, "SELECT CHANGED_BY FROM EST_UTILITY_METER_CONFIG_HISTORY WHERE METER_ID='M_PRE127'")
                .Should().Be("legacy-operator");
            ScalarString(cs, "SELECT PLANT_ID FROM EST_UTILITY_READING WHERE READING_ID='R_PRE127'")
                .Should().Be("P_LEGACY");
            ScalarString(cs, "SELECT READING_MODE FROM EST_UTILITY_READING WHERE READING_ID='R_PRE127'")
                .Should().Be("Delta");
            ScalarString(cs, "SELECT COST_PER_UNIT FROM EST_UTILITY_READING WHERE READING_ID='R_PRE127'")
                .Should().Be("11.25");
            ScalarString(cs, "SELECT CARBON_PER_UNIT FROM EST_UTILITY_READING WHERE READING_ID='R_PRE127'")
                .Should().Be("0.42");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void Apply_fresh_utility_schema_creates_exactly_one_v1_history_for_each_seeded_meter()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);

            ScalarString(cs, """
                SELECT CASE WHEN COUNT(*) =
                    (SELECT COUNT(*) FROM EST_UTILITY_METER)
                    THEN 'true' ELSE 'false' END
                FROM EST_UTILITY_METER_CONFIG_HISTORY
                WHERE CONFIG_VERSION = 1
                """).Should().Be("true");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_rejects_legacy_utility_rows_without_an_immutable_meter_snapshot()
    {
        var cs = NewDb();
        try
        {
            CreatePreV127UtilityTables(cs);
            ExecSql(cs, """
                INSERT INTO EST_UTILITY_READING
                  (READING_ID, METER_ID, RAW_VALUE, NORMALIZED_VALUE, UNIT, SOURCE,
                   SOURCE_EVENT_ID, REQUEST_HASH, RECORDED_AT, RECORDED_BY)
                VALUES ('R_ORPHAN', 'M_MISSING', 1, 1, 'kWh', 'PLC',
                        'PLC-ORPHAN', 'HASH-ORPHAN', CURRENT_TIMESTAMP, 'legacy-operator');
                """);

            Action upgrade = () => SqliteSchemaInitializer.EnsureSchema(cs);

            upgrade.Should().Throw<InvalidOperationException>()
                .WithMessage("*V128*objectType='READING'*objectId='R_ORPHAN'*configVersion=1*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_incrementally_applies_V143_Fdc_endpoint_mapping_columns_and_index()
    {
        var cs = NewDb();
        try
        {
            ExecSql(cs, """
                CREATE TABLE FDC_EQUIPMENT_ENDPOINT (
                    ENDPOINT_ID TEXT NOT NULL PRIMARY KEY,
                    EQUIPMENT_ID TEXT NOT NULL,
                    IS_ACTIVE INTEGER NOT NULL DEFAULT 1
                );
                CREATE TABLE FDC_PARAMETER (
                    PARAMETER_ID TEXT NOT NULL PRIMARY KEY,
                    EQUIPMENT_ID TEXT NOT NULL,
                    IS_ACTIVE INTEGER NOT NULL DEFAULT 1
                );
                """);

            SqliteSchemaInitializer.EnsureSchema(cs);

            Columns(cs, "FDC_EQUIPMENT_ENDPOINT").Should().Contain("TAG_MAP_PATH");
            Columns(cs, "FDC_PARAMETER").Should().Contain("ENDPOINT_ID");
            IndexExists(cs, "IX_FDC_PARAMETER_ENDPOINT_ACTIVE").Should().BeTrue();
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_incrementally_applies_V146_Fdc_effect_lifecycle()
    {
        var cs = NewDb();
        try
        {
            ExecSql(cs, """
                CREATE TABLE FDC_INTERLOCK_HISTORY (
                    HISTORY_ID TEXT NOT NULL PRIMARY KEY,
                    RULE_ID TEXT NOT NULL,
                    EQUIPMENT_ID TEXT NOT NULL,
                    PARAMETER_ID TEXT NOT NULL,
                    TRIGGER_VALUE NUMERIC NOT NULL,
                    ACTION TEXT NOT NULL,
                    MESSAGE TEXT NOT NULL,
                    TRIGGERED_AT TEXT NOT NULL,
                    RESOLVED_AT TEXT NULL,
                    IS_RESOLVED INTEGER NOT NULL DEFAULT 0,
                    CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                    CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UPDATED_BY TEXT NULL,
                    UPDATED_AT TEXT NULL
                );
                INSERT INTO FDC_INTERLOCK_HISTORY
                  (HISTORY_ID, RULE_ID, EQUIPMENT_ID, PARAMETER_ID, TRIGGER_VALUE,
                   ACTION, MESSAGE, TRIGGERED_AT)
                VALUES ('FX-LEGACY', 'R1', 'EQ1', 'P1', 90, 'STOP', 'legacy', CURRENT_TIMESTAMP);
                INSERT INTO FDC_INTERLOCK_HISTORY
                  (HISTORY_ID, RULE_ID, EQUIPMENT_ID, PARAMETER_ID, TRIGGER_VALUE,
                   ACTION, MESSAGE, TRIGGERED_AT, RESOLVED_AT, IS_RESOLVED)
                VALUES ('FX-RESOLVED', 'R1', 'EQ1', 'P1', 90, 'STOP', 'legacy resolved',
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 1);
                """);

            SqliteSchemaInitializer.EnsureSchema(cs);

            Columns(cs, "FDC_INTERLOCK_HISTORY").Should().Contain([
                "EFFECT_STATE", "APPLY_ACK_ID", "APPLY_CONFIRMED_AT",
                "CONDITION_NORMALIZED_AT", "CONDITION_NORMALIZED_VALUE", "RELEASE_ACK_ID",
                "RELEASE_CONFIRMED_AT", "LAST_ERROR", "VERSION"]);
            ScalarString(cs,
                "SELECT EFFECT_STATE FROM FDC_INTERLOCK_HISTORY WHERE HISTORY_ID='FX-LEGACY'")
                .Should().Be("Prepared", "pre-V146 open effects require action reconciliation and fresh ack evidence");
            ScalarString(cs,
                "SELECT EFFECT_STATE FROM FDC_INTERLOCK_HISTORY WHERE HISTORY_ID='FX-RESOLVED'")
                .Should().Be("Resolved", "pre-V146 terminal evidence must map to the terminal lifecycle state");
            ScalarString(cs,
                "SELECT LAST_ERROR FROM FDC_INTERLOCK_HISTORY WHERE HISTORY_ID='FX-RESOLVED'")
                .Should().Be("LegacyResolvedBeforeV146", "legacy terminal rows cannot invent missing action evidence");
            ScalarString(cs, """
                SELECT COUNT(*) FROM SYS_SQLITE_RECONCILIATION
                 WHERE RECONCILIATION_ID='V146__FDC_INTERLOCK_EFFECT_LIFECYCLE'
                """).Should().Be("1");
            IndexExists(cs, "IX_FDC_INTERLOCK_EFFECT_LIFECYCLE").Should().BeFalse(
                "no runtime query filters or orders by the removed state/update key combination");

            // A stale preview trigger is replaced, but the durable data marker is not duplicated.
            ExecSql(cs, """
                DROP TRIGGER TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BU;
                CREATE TRIGGER TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_BU
                BEFORE UPDATE OF IS_RESOLVED, EFFECT_STATE, VERSION ON FDC_INTERLOCK_HISTORY
                BEGIN SELECT 1; END;
                """);
            SqliteSchemaInitializer.EnsureSchema(cs);

            Action inconsistentResolution = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY SET EFFECT_STATE='Applied'
                 WHERE HISTORY_ID='FX-RESOLVED';
                """);
            inconsistentResolution.Should().Throw<SqliteException>()
                .WithMessage("*lifecycle state*invalid*");

            Action missingApplyEvidence = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY SET EFFECT_STATE='Applied', VERSION=2
                 WHERE HISTORY_ID='FX-LEGACY';
                """);
            missingApplyEvidence.Should().Throw<SqliteException>()
                .WithMessage("*lifecycle state*invalid*");

            Action invalidVersion = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY SET VERSION=0
                 WHERE HISTORY_ID='FX-LEGACY';
                """);
            invalidVersion.Should().Throw<SqliteException>()
                .WithMessage("*lifecycle state*invalid*");

            ExecSql(cs, """
                INSERT INTO FDC_INTERLOCK_HISTORY
                    (HISTORY_ID, RULE_ID, EQUIPMENT_ID, PARAMETER_ID, TRIGGER_VALUE,
                     ACTION, MESSAGE, TRIGGERED_AT, IS_RESOLVED, EFFECT_STATE,
                     APPLY_ACK_ID, APPLY_CONFIRMED_AT, VERSION)
                VALUES
                    ('FX-APPLIED', 'R1', 'EQ1', 'P1', 90,
                     'STOP', 'applied', '2026-01-01 00:00:00', 0, 'Applied',
                     'apply-ack', '2026-01-01 00:00:01', 2);
                """);
            Action acknowledgementOnlyBypass = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY SET APPLY_ACK_ID=NULL
                 WHERE HISTORY_ID='FX-APPLIED';
                """);
            acknowledgementOnlyBypass.Should().Throw<SqliteException>()
                .WithMessage("*lifecycle state*invalid*",
                    "ACK-only updates must not bypass the SQLite equivalent of the MSSQL CHECK constraint");

            Action sameVersion = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY SET LAST_ERROR='retry without version'
                 WHERE HISTORY_ID='FX-APPLIED';
                """);
            sameVersion.Should().Throw<SqliteException>()
                .WithMessage("*transition is invalid*",
                    "every durable direct-writer mutation must advance the optimistic version");

            Action replaceEffectIdentity = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY
                   SET HISTORY_ID='FX-RENAMED', VERSION=3
                 WHERE HISTORY_ID='FX-APPLIED';
                """);
            replaceEffectIdentity.Should().Throw<SqliteException>()
                .WithMessage("*transition is invalid*",
                    "SQLite must preserve the same stable EffectId just like the SQL Server delete/update guard");

            // The runtime is allowed to normalize and then reassert the same STOP as Applied on
            // restart before it trusts a fresh PLC snapshot.
            ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY
                   SET EFFECT_STATE='ConditionNormalized', VERSION=3,
                       CONDITION_NORMALIZED_AT='2026-01-01 00:00:02',
                       CONDITION_NORMALIZED_VALUE=50
                 WHERE HISTORY_ID='FX-APPLIED';
                UPDATE FDC_INTERLOCK_HISTORY
                   SET EFFECT_STATE='Applied', VERSION=4,
                       CONDITION_NORMALIZED_AT=NULL, CONDITION_NORMALIZED_VALUE=NULL
                 WHERE HISTORY_ID='FX-APPLIED';
                """);

            Action illegalBackwardJump = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY
                   SET EFFECT_STATE='Prepared', VERSION=5,
                       APPLY_ACK_ID=NULL, APPLY_CONFIRMED_AT=NULL
                 WHERE HISTORY_ID='FX-APPLIED';
                """);
            illegalBackwardJump.Should().Throw<SqliteException>()
                .WithMessage("*transition is invalid*");

            Action mutateTerminal = () => ExecSql(cs, """
                UPDATE FDC_INTERLOCK_HISTORY SET VERSION=2, UPDATED_AT=CURRENT_TIMESTAMP
                 WHERE HISTORY_ID='FX-RESOLVED';
                """);
            mutateTerminal.Should().Throw<SqliteException>()
                .WithMessage("*transition is invalid*",
                    "a resolved physical-effect ledger row is terminal evidence");

            Action deleteEvidence = () => ExecSql(cs, """
                DELETE FROM FDC_INTERLOCK_HISTORY WHERE HISTORY_ID='FX-APPLIED';
                """);
            deleteEvidence.Should().Throw<SqliteException>()
                .WithMessage("*effect history is append-only*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_incrementally_seeds_exactly_one_V149_global_runtime_owner()
    {
        var cs = NewDb();
        try
        {
            ExecSql(cs, "CREATE TABLE LEGACY_BOOT_MARKER (ID INTEGER NOT NULL PRIMARY KEY);");

            SqliteSchemaInitializer.EnsureSchema(cs);
            SqliteSchemaInitializer.EnsureSchema(cs);

            Count(cs, "FDC_RUNTIME_OWNERSHIP").Should().Be(1);
            ScalarString(cs, "SELECT LEASE_SCOPE FROM FDC_RUNTIME_OWNERSHIP").Should().Be("GLOBAL");
            ScalarString(cs, "SELECT FENCE_TOKEN FROM FDC_RUNTIME_OWNERSHIP").Should().Be("0");
            ScalarString(cs, """
                SELECT COUNT(*) FROM pragma_table_info('FDC_RUNTIME_OWNERSHIP')
                 WHERE name='LEASE_SECRET_HASH'
                """).Should().Be("1");
            ScalarString(cs, """
                SELECT COUNT(*) FROM SYS_SQLITE_RECONCILIATION
                 WHERE RECONCILIATION_ID='V149__FDC_RUNTIME_OWNERSHIP_FENCE'
                """).Should().Be("1");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_adds_secret_hash_to_an_unowned_pre_hardening_V149_row_without_resetting_fence()
    {
        var cs = NewDb();
        try
        {
            ExecSql(cs, """
                CREATE TABLE LEGACY_BOOT_MARKER (ID INTEGER NOT NULL PRIMARY KEY);
                CREATE TABLE FDC_RUNTIME_OWNERSHIP (
                    LEASE_SCOPE TEXT NOT NULL PRIMARY KEY,
                    OWNER_ID TEXT NULL,
                    FENCE_TOKEN INTEGER NOT NULL,
                    LEASE_EXPIRES_AT TEXT NULL,
                    HEARTBEAT_AT TEXT NULL,
                    CONFIG_REVISION TEXT NULL,
                    CREATED_BY TEXT NOT NULL,
                    CREATED_AT TEXT NOT NULL,
                    UPDATED_BY TEXT NOT NULL,
                    UPDATED_AT TEXT NOT NULL);
                INSERT INTO FDC_RUNTIME_OWNERSHIP
                    (LEASE_SCOPE, OWNER_ID, FENCE_TOKEN, LEASE_EXPIRES_AT, HEARTBEAT_AT,
                     CONFIG_REVISION, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES
                    ('GLOBAL', NULL, 17, NULL, NULL, NULL,
                     'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                """);

            SqliteSchemaInitializer.EnsureSchema(cs);

            ScalarString(cs, "SELECT FENCE_TOKEN FROM FDC_RUNTIME_OWNERSHIP").Should().Be("17");
            ScalarString(cs, """
                SELECT COUNT(*) FROM pragma_table_info('FDC_RUNTIME_OWNERSHIP')
                 WHERE name='LEASE_SECRET_HASH'
                """).Should().Be("1");
            ScalarString(cs, "SELECT LEASE_SECRET_HASH FROM FDC_RUNTIME_OWNERSHIP")
                .Should().BeEmpty();
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_hardens_legacy_V149_scope_insert_and_release_tuple_transitions()
    {
        var cs = NewDb();
        try
        {
            ExecSql(cs, """
                CREATE TABLE LEGACY_BOOT_MARKER (ID INTEGER NOT NULL PRIMARY KEY);
                CREATE TABLE FDC_RUNTIME_OWNERSHIP (
                    LEASE_SCOPE TEXT NOT NULL PRIMARY KEY,
                    OWNER_ID TEXT NULL,
                    FENCE_TOKEN INTEGER NOT NULL,
                    LEASE_EXPIRES_AT TEXT NULL,
                    HEARTBEAT_AT TEXT NULL,
                    CONFIG_REVISION TEXT NULL,
                    LEASE_SECRET_HASH TEXT NULL,
                    CREATED_BY TEXT NOT NULL,
                    CREATED_AT TEXT NOT NULL,
                    UPDATED_BY TEXT NOT NULL,
                    UPDATED_AT TEXT NOT NULL);
                INSERT INTO FDC_RUNTIME_OWNERSHIP VALUES
                    ('GLOBAL', NULL, 17, NULL, NULL, NULL, NULL,
                     'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                """);

            SqliteSchemaInitializer.EnsureSchema(cs);

            Action replaceFence = () => ExecSql(cs, """
                PRAGMA recursive_triggers=OFF;
                INSERT OR REPLACE INTO FDC_RUNTIME_OWNERSHIP VALUES
                    ('GLOBAL', NULL, 0, NULL, NULL, NULL, NULL,
                     'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                """);
            replaceFence.Should().Throw<SqliteException>()
                .WithMessage("*ownership row is invalid*");
            ScalarString(cs, "SELECT FENCE_TOKEN FROM FDC_RUNTIME_OWNERSHIP")
                .Should().Be("17", "INSERT OR REPLACE must not reset a durable fence counter");

            Action invalidInsert = () => ExecSql(cs, """
                INSERT INTO FDC_RUNTIME_OWNERSHIP VALUES
                    ('OTHER', NULL, 0, NULL, NULL, NULL, NULL,
                     'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                """);
            invalidInsert.Should().Throw<SqliteException>()
                .WithMessage("*ownership row is invalid*");

            ExecSql(cs, """
                UPDATE FDC_RUNTIME_OWNERSHIP SET
                    OWNER_ID='owner-1',
                    FENCE_TOKEN=18,
                    HEARTBEAT_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now'),
                    LEASE_EXPIRES_AT=STRFTIME('%Y-%m-%d %H:%M:%f', 'now', '+1 minute'),
                    CONFIG_REVISION=PRINTF('%064d', 0),
                    LEASE_SECRET_HASH=PRINTF('%064d', 0)
                 WHERE LEASE_SCOPE='GLOBAL';
                """);

            Action partialRelease = () => ExecSql(cs, """
                UPDATE FDC_RUNTIME_OWNERSHIP SET OWNER_ID=NULL
                 WHERE LEASE_SCOPE='GLOBAL';
                """);
            partialRelease.Should().Throw<SqliteException>()
                .WithMessage("*transition or fence token is invalid*");

            Action renameScope = () => ExecSql(cs, """
                UPDATE FDC_RUNTIME_OWNERSHIP SET LEASE_SCOPE='OTHER'
                 WHERE LEASE_SCOPE='GLOBAL';
                """);
            renameScope.Should().Throw<SqliteException>()
                .WithMessage("*transition or fence token is invalid*");

            ExecSql(cs, """
                UPDATE FDC_RUNTIME_OWNERSHIP SET
                    OWNER_ID=NULL, LEASE_EXPIRES_AT=NULL, HEARTBEAT_AT=NULL,
                    CONFIG_REVISION=NULL, LEASE_SECRET_HASH=NULL
                 WHERE LEASE_SCOPE='GLOBAL';
                """);
            ScalarString(cs, "SELECT OWNER_ID FROM FDC_RUNTIME_OWNERSHIP")
                .Should().BeEmpty();
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void V149_runtime_owner_rejects_offset_timestamps_that_can_reverse_text_order()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, """
                DROP TRIGGER TR_FDC_RUNTIME_OWNERSHIP_FENCE_BD;
                DELETE FROM FDC_RUNTIME_OWNERSHIP WHERE LEASE_SCOPE='GLOBAL';
                """);

            Action offsetLease = () => ExecSql(cs, """
                INSERT INTO FDC_RUNTIME_OWNERSHIP
                    (LEASE_SCOPE, OWNER_ID, FENCE_TOKEN, LEASE_EXPIRES_AT, HEARTBEAT_AT,
                     CONFIG_REVISION, LEASE_SECRET_HASH,
                     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES
                    ('GLOBAL', 'owner-offset', 1,
                     '2026-01-01T00:00:00+10:00', '2026-01-01 00:00:00',
                     PRINTF('%064d', 0), PRINTF('%064d', 0),
                     'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
                """);

            offsetLease.Should().Throw<SqliteException>()
                .WithMessage("*ownership row is invalid*");

            Action invalidCalendarLease = () => ExecSql(cs, """
                INSERT INTO FDC_RUNTIME_OWNERSHIP
                    (LEASE_SCOPE, OWNER_ID, FENCE_TOKEN, LEASE_EXPIRES_AT, HEARTBEAT_AT,
                     CONFIG_REVISION, LEASE_SECRET_HASH,
                     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES
                    ('GLOBAL', 'owner-invalid-date', 1,
                     '2026-03-01 00:00:00.000', '2026-02-30 00:00:00.000',
                     PRINTF('%064d', 0), PRINTF('%064d', 0),
                     'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
                """);
            invalidCalendarLease.Should().Throw<SqliteException>()
                .WithMessage("*ownership row is invalid*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_never_recreates_a_missing_V149_fence_counter_after_marker()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, """
                DROP TRIGGER TR_FDC_RUNTIME_OWNERSHIP_FENCE_BD;
                DELETE FROM FDC_RUNTIME_OWNERSHIP WHERE LEASE_SCOPE='GLOBAL';
                """);

            Action restart = () => SqliteSchemaInitializer.EnsureSchema(cs);

            restart.Should().Throw<InvalidOperationException>()
                .WithMessage("*durable marker exists*recreating fence token 0 would reuse issued tokens*");
            Count(cs, "FDC_RUNTIME_OWNERSHIP").Should().Be(0);
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V150_retention_state_is_singleton_monotonic_and_canonical(bool incremental)
    {
        var cs = NewDb();
        try
        {
            if (incremental)
            {
                ExecSql(cs, "CREATE TABLE LEGACY_BOOT_MARKER (ID INTEGER NOT NULL PRIMARY KEY);");
                SqliteSchemaInitializer.EnsureSchema(cs);
            }
            else
            {
                SqliteSchemaInitializer.Apply(cs);
            }

            Count(cs, "FDC_TRACE_RETENTION_STATE").Should().Be(1);
            ScalarString(cs, "SELECT STATE_ID FROM FDC_TRACE_RETENTION_STATE").Should().Be("GLOBAL");
            ScalarString(cs, "SELECT COMPLETENESS_BOUNDARY FROM FDC_TRACE_RETENTION_STATE")
                .Should().NotBeEmpty("an empty database is only provably complete from V150 initialization time");
            ScalarString(cs, """
                SELECT COUNT(*) FROM SYS_SQLITE_RECONCILIATION
                 WHERE RECONCILIATION_ID='V150__FDC_TRACE_RETENTION_STATE'
                """).Should().Be("1");

            ExecSql(cs, """
                UPDATE FDC_TRACE_RETENTION_STATE
                   SET COMPLETENESS_BOUNDARY='2099-01-02 03:04:05.1234567'
                 WHERE STATE_ID='GLOBAL';
                """);
            Action backward = () => ExecSql(cs, """
                UPDATE FDC_TRACE_RETENTION_STATE
                   SET COMPLETENESS_BOUNDARY='2099-01-02 03:04:05.1234566'
                 WHERE STATE_ID='GLOBAL';
                """);
            backward.Should().Throw<SqliteException>()
                .WithMessage("*boundary cannot move backward*");

            Action offsetBackward = () => ExecSql(cs, """
                UPDATE FDC_TRACE_RETENTION_STATE
                   SET COMPLETENESS_BOUNDARY='2099-01-02T03:04:05+10:00'
                 WHERE STATE_ID='GLOBAL';
                """);
            offsetBackward.Should().Throw<SqliteException>()
                .WithMessage("*retention completeness boundary*");

            Action invalidCalendarBoundary = () => ExecSql(cs, """
                UPDATE FDC_TRACE_RETENTION_STATE
                   SET COMPLETENESS_BOUNDARY='2099-02-30 03:04:05.1234567'
                 WHERE STATE_ID='GLOBAL';
                """);
            invalidCalendarBoundary.Should().Throw<SqliteException>()
                .WithMessage("*retention completeness boundary*");

            Action replaceBoundary = () => ExecSql(cs, """
                PRAGMA recursive_triggers=OFF;
                INSERT OR REPLACE INTO FDC_TRACE_RETENTION_STATE
                    (STATE_ID, COMPLETENESS_BOUNDARY, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES
                    ('GLOBAL', '2020-01-01 00:00:00',
                     'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                """);
            replaceBoundary.Should().Throw<SqliteException>()
                .WithMessage("*retention state is invalid*");
            ScalarString(cs, "SELECT COMPLETENESS_BOUNDARY FROM FDC_TRACE_RETENTION_STATE")
                .Should().Be("2099-01-02 03:04:05.1234567",
                    "INSERT OR REPLACE must not move the durable boundary backward");

            Action offsetInsert = () => ExecSql(cs, """
                INSERT INTO FDC_COLLECT_DATA
                    (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT,
                     QUALITY, LOWER_LIMIT, UPPER_LIMIT)
                VALUES
                    ('OFFSET-DIRECT-INSERT', 'EQ-1', 'P-1', 1,
                     '2099-01-01T23:30:00-10:00', 'Good', 0, 100);
                """);
            offsetInsert.Should().Throw<SqliteException>()
                .WithMessage("*timestamp is invalid or older than its completeness boundary*");

            Action backdatedInsert = () => ExecSql(cs, """
                INSERT INTO FDC_COLLECT_DATA
                    (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT,
                     QUALITY, LOWER_LIMIT, UPPER_LIMIT)
                VALUES
                    ('BACKDATED-DIRECT-INSERT', 'EQ-1', 'P-1', 1,
                     '2099-01-02 03:04:05.1234566', 'Good', 0, 100);
                """);
            backdatedInsert.Should().Throw<SqliteException>()
                .WithMessage("*older than its completeness boundary*");

            Action delete = () => ExecSql(cs,
                "DELETE FROM FDC_TRACE_RETENTION_STATE WHERE STATE_ID='GLOBAL';");
            delete.Should().Throw<SqliteException>()
                .WithMessage("*completeness state is not deletable*");

            Action secondRow = () => ExecSql(cs, """
                INSERT INTO FDC_TRACE_RETENTION_STATE
                    (STATE_ID, COMPLETENESS_BOUNDARY, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('OTHER', NULL, 'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                """);
            secondRow.Should().Throw<SqliteException>()
                .WithMessage("*retention state is invalid*");

            ExecSql(cs, """
                INSERT INTO FDC_COLLECT_DATA
                    (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT,
                     QUALITY, LOWER_LIMIT, UPPER_LIMIT)
                VALUES
                    ('DIRECT-DELETE', 'EQ-1', 'P-1', 1,
                     '2100-01-01 00:00:00', 'Good', 0, 100);
                """);
            Action rawUpdate = () => ExecSql(cs, """
                UPDATE FDC_COLLECT_DATA
                   SET COLLECTED_AT='2100-01-02 00:00:00'
                 WHERE COLLECT_ID='DIRECT-DELETE';
                """);
            rawUpdate.Should().Throw<SqliteException>()
                .WithMessage("*raw TRACE is append-only*");

            Action rawReplace = () => ExecSql(cs, """
                PRAGMA recursive_triggers=OFF;
                INSERT OR REPLACE INTO FDC_COLLECT_DATA
                    (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT,
                     QUALITY, LOWER_LIMIT, UPPER_LIMIT)
                VALUES
                    ('DIRECT-DELETE', 'EQ-1', 'P-1', 999,
                     '2100-01-03 00:00:00', 'Bad', 0, 100);
                """);
            rawReplace.Should().Throw<SqliteException>()
                .WithMessage("*timestamp is invalid or older than its completeness boundary*");
            ScalarString(cs, "SELECT VALUE FROM FDC_COLLECT_DATA WHERE COLLECT_ID='DIRECT-DELETE'")
                .Should().Be("1", "INSERT OR REPLACE must not mutate an append-only TRACE row");

            ExecSql(cs, """
                DROP TRIGGER TR_FDC_COLLECT_RETENTION_DELETE_GUARD;
                CREATE TRIGGER TR_FDC_COLLECT_RETENTION_DELETE_GUARD
                BEFORE DELETE ON FDC_COLLECT_DATA
                BEGIN SELECT 1; END;
                """);
            SqliteSchemaInitializer.EnsureSchema(cs);

            Action downlevelDelete = () => ExecSql(cs,
                "DELETE FROM FDC_COLLECT_DATA WHERE COLLECT_ID='DIRECT-DELETE';");
            downlevelDelete.Should().Throw<SqliteException>()
                .WithMessage("*before advancing its completeness boundary*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_revalidates_raw_TRACE_after_the_V150_marker()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, """
                DROP TRIGGER TR_FDC_COLLECT_COMPLETENESS_BI;
                DROP INDEX IX_FDC_COLLECT_INVALID_TIMESTAMP;
                INSERT INTO FDC_COLLECT_DATA
                    (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT,
                     QUALITY, LOWER_LIMIT, UPPER_LIMIT)
                VALUES
                    ('POST-MARKER-OFFSET', 'EQ-1', 'P-1', 1,
                     '2100-01-01T00:00:00Z', 'Good', 0, 100);
                """);

            Action restart = () => SqliteSchemaInitializer.EnsureSchema(cs);

            restart.Should().Throw<InvalidOperationException>()
                .WithMessage("*raw TRACE row(s)*canonical UTC COLLECTED_AT*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void V150_incremental_seed_is_one_tick_after_the_earliest_retained_TRACE_timestamp()
    {
        var cs = NewDb();
        try
        {
            ExecSql(cs, """
                CREATE TABLE LEGACY_BOOT_MARKER (ID INTEGER NOT NULL PRIMARY KEY);
                CREATE TABLE FDC_COLLECT_DATA (
                    COLLECT_ID TEXT NOT NULL PRIMARY KEY,
                    EQUIPMENT_ID TEXT NOT NULL,
                    PARAMETER_ID TEXT NOT NULL,
                    VALUE NUMERIC NOT NULL,
                    COLLECTED_AT TEXT NOT NULL,
                    QUALITY TEXT NOT NULL,
                    LOWER_LIMIT NUMERIC NOT NULL,
                    UPPER_LIMIT NUMERIC NOT NULL);
                INSERT INTO FDC_COLLECT_DATA VALUES
                    ('EARLIEST-A', 'EQ-1', 'P-1', 1, '2025-01-01 00:00:00.1234567', 'Good', 0, 100),
                    ('EARLIEST-B', 'EQ-1', 'P-1', 2, '2025-01-01 00:00:00.1234567', 'Good', 0, 100),
                    ('LATER', 'EQ-1', 'P-1', 3, '2025-01-02 00:00:00', 'Good', 0, 100);
                """);

            SqliteSchemaInitializer.EnsureSchema(cs);

            ScalarString(cs, "SELECT COMPLETENESS_BOUNDARY FROM FDC_TRACE_RETENTION_STATE")
                .Should().Be("2025-01-01 00:00:00.1234568");
            ExecSql(cs, """
                DELETE FROM FDC_COLLECT_DATA
                 WHERE COLLECT_ID IN ('EARLIEST-A', 'EARLIEST-B');
                """);
            Action deleteLater = () => ExecSql(cs,
                "DELETE FROM FDC_COLLECT_DATA WHERE COLLECT_ID='LATER';");
            deleteLater.Should().Throw<SqliteException>()
                .WithMessage("*before advancing its completeness boundary*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_never_recreates_a_missing_V150_completeness_boundary_after_marker()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, """
                DROP TRIGGER TR_FDC_TRACE_RETENTION_STATE_BD;
                DELETE FROM FDC_TRACE_RETENTION_STATE WHERE STATE_ID='GLOBAL';
                """);

            Action restart = () => SqliteSchemaInitializer.EnsureSchema(cs);

            restart.Should().Throw<InvalidOperationException>()
                .WithMessage("*durable marker exists*forget a prior deletion boundary*");
            Count(cs, "FDC_TRACE_RETENTION_STATE").Should().Be(0);
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Performance_indexes_match_hot_query_contracts_for_fresh_and_incremental_schema(bool incremental)
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            if (incremental)
            {
                ExecSql(cs, """
                    DROP TRIGGER IF EXISTS TR_IVT_TRACE_INBOX_WORK_STATE_BI;
                    DROP TRIGGER IF EXISTS TR_IVT_TRACE_INBOX_WORK_STATE_BU;
                    INSERT INTO IVT_TRACE_CONSUMPTION_BINDING
                        (BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
                         CALCULATION_MODE, SCALE_FACTOR, OUTPUT_UNIT)
                    VALUES ('B_CURSOR', 'P1', 'EQ1', 'FLOW', 'FEED1', 'Direct', 1, 'L');
                    INSERT INTO IVT_TRACE_PROJECTION_INBOX
                        (BINDING_ID, COLLECT_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID,
                         FEED_POINT_ID, CALCULATION_MODE, SCALE_FACTOR, OUTPUT_UNIT,
                         RAW_VALUE, QUALITY, COLLECTED_AT, STATUS)
                    VALUES
                        ('B_CURSOR', 'C1', 'P1', 'EQ1', 'FLOW', 'FEED1', 'Direct', 1, 'L', 1, 'Good', '2030-01-01 00:00:00', 'Pending'),
                        ('B_CURSOR', 'C2', 'P1', 'EQ1', 'FLOW', 'FEED1', 'Direct', 1, 'L', 2, 'Good', '2030-01-01 00:01:00', 'Ignored'),
                        ('B_CURSOR', 'C3', 'P1', 'EQ1', 'FLOW', 'FEED1', 'Direct', 1, 'L', 3, 'Good', '2030-01-01 00:01:00', 'Applied');
                    """);
                // Simulate a pre-V142 database: remove the durable cursor/new filtered work path,
                // including the work-set column, restore the old inbox-derived indexes, and restore
                // V115's redundant checklist.
                ExecSql(cs, """
                    DROP INDEX IX_IVT_TRACE_INBOX_READY;
                    DROP TABLE IVT_TRACE_INGESTION_CURSOR;
                    DROP TRIGGER IF EXISTS TR_IVT_TRACE_INBOX_WORK_STATE_BI;
                    DROP TRIGGER IF EXISTS TR_IVT_TRACE_INBOX_WORK_STATE_BU;
                    ALTER TABLE IVT_TRACE_PROJECTION_INBOX DROP COLUMN IS_WORK_ITEM;
                    CREATE INDEX IX_IVT_TRACE_INBOX_BINDING_CURSOR
                        ON IVT_TRACE_PROJECTION_INBOX
                           (BINDING_ID, COLLECTED_AT DESC, COLLECT_ID DESC);
                    CREATE INDEX IX_IVT_TRACE_INBOX_WORK
                        ON IVT_TRACE_PROJECTION_INBOX (STATUS, COLLECTED_AT, BINDING_ID);
                    DROP INDEX IX_EMS_TOOL_USAGE_MOUNT;
                    DROP INDEX IX_EMS_WO_EQUIPMENT_ISSUED;
                    DROP INDEX IX_EMS_WO_ISSUED;
                    CREATE INDEX IX_EMS_WORK_ORDER_CHECK_RESULT_WO
                        ON EMS_WORK_ORDER_CHECK_RESULT (WO_ID, ITEM_SEQUENCE);
                    DROP INDEX IX_FDC_COLLECT_RETENTION;
                    CREATE INDEX IX_FDC_COLLECT_RETENTION
                        ON FDC_COLLECT_DATA (COLLECT_ID, COLLECTED_AT DESC);
                    DROP INDEX IX_FDC_TRACE_SOURCE;
                    CREATE INDEX ix_fdc_trace_source
                        ON FDC_COLLECT_DATA
                           (EQUIPMENT_ID, PARAMETER_ID, COLLECTED_AT, COLLECT_ID);
                    DELETE FROM SYS_SQLITE_RECONCILIATION
                     WHERE RECONCILIATION_ID = 'V142__IVT_TRACE_INGESTION_CURSOR';
                    """);

                SqliteSchemaInitializer.EnsureSchema(cs);

                // V142 data reconciliation is a migration, not a boot-time repair loop. A durable
                // marker must prevent later EnsureSchema calls from rebuilding a deleted cursor.
                // A stale trigger definition is nevertheless repaired in the same startup.
                ExecSql(cs, """
                    DELETE FROM IVT_TRACE_INGESTION_CURSOR WHERE BINDING_ID = 'B_CURSOR';
                    DROP TRIGGER TR_IVT_TRACE_INBOX_WORK_STATE_BU;
                    CREATE TRIGGER TR_IVT_TRACE_INBOX_WORK_STATE_BU
                    BEFORE UPDATE OF STATUS, IS_WORK_ITEM ON IVT_TRACE_PROJECTION_INBOX
                    BEGIN
                        SELECT 1;
                    END;
                    """);
                SqliteSchemaInitializer.EnsureSchema(cs);
                using (var markerConnection = new SqliteConnection(cs))
                {
                    markerConnection.Open();
                    Scalar(markerConnection, """
                        SELECT COUNT(*) FROM SYS_SQLITE_RECONCILIATION
                         WHERE RECONCILIATION_ID='V142__IVT_TRACE_INGESTION_CURSOR'
                        """).Should().Be("1");
                    Scalar(markerConnection, """
                        SELECT COUNT(*) FROM IVT_TRACE_INGESTION_CURSOR
                         WHERE BINDING_ID='B_CURSOR'
                        """).Should().Be("0", "completed V142 cursor backfill must not rerun during normal boot");
                }
                ExecSql(cs, """
                    INSERT INTO IVT_TRACE_INGESTION_CURSOR
                        (BINDING_ID, LAST_COLLECT_ID, LAST_COLLECTED_AT)
                    VALUES ('B_CURSOR', 'C3', '2030-01-01 00:01:00');
                    """);

                Action hidePendingWork = () => ExecSql(cs, """
                    UPDATE IVT_TRACE_PROJECTION_INBOX SET IS_WORK_ITEM = 0
                     WHERE BINDING_ID = 'B_CURSOR' AND COLLECT_ID = 'C1';
                    """);
                hidePendingWork.Should().Throw<SqliteException>()
                    .WithMessage("*STATUS and IS_WORK_ITEM must agree*");
            }

            TableExists(cs, "IVT_TRACE_INGESTION_CURSOR").Should().BeTrue();
            if (incremental)
            {
                using var cursorConnection = new SqliteConnection(cs);
                cursorConnection.Open();
                Scalar(cursorConnection, "SELECT LAST_COLLECT_ID FROM IVT_TRACE_INGESTION_CURSOR WHERE BINDING_ID='B_CURSOR'")
                    .Should().Be("C3", "the latest timestamp and collect-id tie-breaker define the restart cursor");
                Scalar(cursorConnection, "SELECT IS_WORK_ITEM FROM IVT_TRACE_PROJECTION_INBOX WHERE COLLECT_ID='C1'")
                    .Should().Be("1");
                Scalar(cursorConnection, "SELECT IS_WORK_ITEM FROM IVT_TRACE_PROJECTION_INBOX WHERE COLLECT_ID='C2'")
                    .Should().Be("0");
                Scalar(cursorConnection, "SELECT IS_WORK_ITEM FROM IVT_TRACE_PROJECTION_INBOX WHERE COLLECT_ID='C3'")
                    .Should().Be("0");
            }
            Columns(cs, "IVT_TRACE_PROJECTION_INBOX").Should().Contain("IS_WORK_ITEM");
            IndexExists(cs, "IX_IVT_TRACE_INBOX_BINDING_CURSOR").Should().BeFalse();
            IndexExists(cs, "IX_IVT_TRACE_INBOX_WORK").Should().BeFalse();
            IndexExists(cs, "IX_IVT_TRACE_INBOX_CURSOR_BACKFILL").Should().BeFalse(
                "the upgrade-only ordering index must not add permanent write amplification");
            IndexKeys(cs, "IX_IVT_TRACE_INBOX_READY").Should().Equal(
                "COLLECTED_AT:ASC", "COLLECT_ID:ASC", "BINDING_ID:ASC");
            IndexSql(cs, "IX_IVT_TRACE_INBOX_READY")
                .Should().Contain("WHERE IS_WORK_ITEM = 1");
            IndexKeys(cs, "IX_EMS_TOOL_USAGE_MOUNT").Should().Equal(
                "MOUNT_ID:ASC", "USED_AT:DESC");
            IndexKeys(cs, "IX_EMS_WO_EQUIPMENT_ISSUED").Should().Equal(
                "EQUIPMENT_ID:ASC", "ISSUED_AT:DESC");
            IndexKeys(cs, "IX_EMS_WO_ISSUED").Should().Equal(
                "ISSUED_AT:DESC", "WO_ID:DESC");

            IndexExists(cs, "IX_EMS_WORK_ORDER_CHECK_RESULT_WO").Should().BeFalse(
                "UQ_EMS_WORK_ORDER_CHECK_SEQUENCE already provides this exact access path");
            CountIndexesWithKeys(cs, "EMS_WORK_ORDER_CHECK_RESULT", "WO_ID", "ITEM_SEQUENCE")
                .Should().Be(1, "removing the explicit duplicate must preserve the UNIQUE constraint index");

            QueryPlan(cs, """
                SELECT B.BINDING_ID, C.LAST_COLLECT_ID, C.LAST_COLLECTED_AT
                FROM IVT_TRACE_CONSUMPTION_BINDING B
                LEFT JOIN IVT_TRACE_INGESTION_CURSOR C ON C.BINDING_ID=B.BINDING_ID
                WHERE B.IS_ACTIVE=1
                """).Should().Contain("sqlite_autoindex_IVT_TRACE_INGESTION_CURSOR_1",
                    "source progress must be read from one PK row per binding, not the inbox");
            QueryPlan(cs, """
                SELECT BINDING_ID, COLLECT_ID FROM IVT_TRACE_PROJECTION_INBOX
                WHERE IS_WORK_ITEM=1 AND STATUS IN ('Pending', 'Error')
                ORDER BY COLLECTED_AT, COLLECT_ID, BINDING_ID LIMIT 100
                """).Should().Contain("IX_IVT_TRACE_INBOX_READY");
            IndexKeys(cs, "IX_FDC_INTERLOCK_OPEN_EQUIPMENT_PARAMETER").Should().Equal(
                "EQUIPMENT_ID:ASC", "PARAMETER_ID:ASC", "TRIGGERED_AT:DESC");
            IndexSql(cs, "IX_FDC_INTERLOCK_OPEN_EQUIPMENT_PARAMETER")
                .Should().Contain("WHERE IS_RESOLVED = 0");
            IndexKeys(cs, "IX_FDC_ALARM_OPEN_EQUIPMENT_PARAMETER").Should().Equal(
                "EQUIPMENT_ID:ASC", "PARAMETER_ID:ASC", "OCCURRED_AT:DESC");
            IndexSql(cs, "IX_FDC_ALARM_OPEN_EQUIPMENT_PARAMETER")
                .Should().Contain("WHERE IS_CLEARED = 0");
            IndexKeys(cs, "IX_FDC_COLLECT_RETENTION").Should().Equal(
                "COLLECTED_AT:ASC", "COLLECT_ID:ASC");
            IndexSql(cs, "IX_FDC_TRACE_SOURCE").Should().Contain(
                "CASE", "the cursor index must use the same normalized timestamp key as TRACE paging");
            IndexSql(cs, "IX_FDC_COLLECT_INVALID_TIMESTAMP").Should().Contain(
                "WHERE NOT",
                "normal boots must validate an empty partial index instead of rescanning all TRACE rows");
            var traceCursorPlan = QueryPlan(cs, """
                SELECT COLLECT_ID FROM FDC_COLLECT_DATA
                WHERE EQUIPMENT_ID='EQ1' AND PARAMETER_ID='P1'
                  AND CASE
                          WHEN LENGTH(COLLECTED_AT)=19 THEN COLLECTED_AT || '.0000000'
                          ELSE SUBSTR(COLLECTED_AT || '0000000', 1, 27)
                      END >= '2030-01-01 00:00:00.0000000'
                  AND (CASE
                           WHEN LENGTH(COLLECTED_AT)=19 THEN COLLECTED_AT || '.0000000'
                           ELSE SUBSTR(COLLECTED_AT || '0000000', 1, 27)
                       END > '2030-01-01 00:00:00.0000000'
                       OR (CASE
                               WHEN LENGTH(COLLECTED_AT)=19 THEN COLLECTED_AT || '.0000000'
                               ELSE SUBSTR(COLLECTED_AT || '0000000', 1, 27)
                           END = '2030-01-01 00:00:00.0000000'
                           AND COLLECT_ID > 'C0'))
                ORDER BY CASE
                             WHEN LENGTH(COLLECTED_AT)=19 THEN COLLECTED_AT || '.0000000'
                             ELSE SUBSTR(COLLECTED_AT || '0000000', 1, 27)
                         END,
                         COLLECT_ID
                LIMIT 100
                """);
            traceCursorPlan.Should().Contain("IX_FDC_TRACE_SOURCE");
            traceCursorPlan.Should().NotContain("USE TEMP B-TREE",
                "bounded TRACE paging must not sort the entire effective range before LIMIT");
            traceCursorPlan.Should().Contain(">?",
                "a resumed page must seek the normalized index from its cursor, not rescan effectiveFrom");
            QueryPlan(cs, """
                SELECT COLLECT_ID FROM FDC_COLLECT_DATA
                WHERE COLLECTED_AT < '2030-01-01'
                ORDER BY COLLECTED_AT, COLLECT_ID LIMIT 1000
                """).Should().Contain("IX_FDC_COLLECT_RETENTION",
                    "bounded retention must seek the time-leading index instead of scanning TRACE history");
            QueryPlan(cs, """
                SELECT HISTORY_ID FROM FDC_INTERLOCK_HISTORY
                WHERE EQUIPMENT_ID='EQ1' AND PARAMETER_ID='P1' AND IS_RESOLVED=0
                ORDER BY TRIGGERED_AT DESC
                """).Should().Contain("IX_FDC_INTERLOCK_OPEN_EQUIPMENT_PARAMETER");
            QueryPlan(cs, """
                SELECT ALARM_ID FROM FDC_ALARM_HISTORY
                WHERE EQUIPMENT_ID='EQ1' AND PARAMETER_ID='P1' AND IS_CLEARED=0
                ORDER BY OCCURRED_AT DESC
                """).Should().Contain("IX_FDC_ALARM_OPEN_EQUIPMENT_PARAMETER");
            QueryPlan(cs, """
                SELECT MAX(USED_AT) FROM EMS_TOOL_USAGE_HISTORY WHERE MOUNT_ID='M1'
                """).Should().Contain("IX_EMS_TOOL_USAGE_MOUNT");
            QueryPlan(cs, """
                SELECT WO_ID FROM EMS_WORK_ORDER
                WHERE EQUIPMENT_ID='EQ1' AND ISSUED_AT >= '2025-01-01'
                ORDER BY ISSUED_AT DESC
                """).Should().Contain("IX_EMS_WO_EQUIPMENT_ISSUED");
            var emsWorkOrderListSql = NamedQuerySql("sqlite", "EMS", "EMS.WorkOrderList")
                .Replace("@equipmentId", "NULL", StringComparison.Ordinal)
                .Replace("@status", "NULL", StringComparison.Ordinal);
            emsWorkOrderListSql.Should().Contain("ORDER BY ISSUED_AT DESC, WO_ID DESC");
            emsWorkOrderListSql.Should().Contain("LIMIT 500");
            QueryPlan(cs, emsWorkOrderListSql).Should().Contain("IX_EMS_WO_ISSUED",
                "the unfiltered named query must scan only the newest bounded work-order path");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Second_pass_query_indexes_match_repository_contracts_for_fresh_and_incremental_schema(bool incremental)
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            if (incremental)
            {
                // Simulate a V130 database. A few names intentionally retain a stale definition
                // so reconciliation proves more than CREATE INDEX IF NOT EXISTS behavior.
                ExecSql(cs, """
                    DROP INDEX IX_EMS_SPARE_USAGE_WO_TIME;
                    CREATE INDEX IX_EMS_SPARE_USAGE_WO_TIME
                        ON EMS_SPARE_PART_USAGE (WO_ID, USED_AT DESC)
                        WHERE WO_ID IS NULL;
                    DROP INDEX IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE;
                    CREATE INDEX IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE
                        ON RMS_RECIPE_EQUIPMENT_ASSIGNMENT (EQUIPMENT_ID, EFFECTIVE_FROM);
                    DROP INDEX IX_RMS_RECIPE_ASSIGNMENT_CLASS_EFFECTIVE;
                    DROP INDEX IX_EST_TAKT_RECONCILIATION_DATE;
                    DROP INDEX IX_EST_OEE_LOSS_RECONCILIATION_DATE;
                    DROP INDEX IX_EST_OEE_SUMMARY_RECONCILIATION_DATE;
                    CREATE INDEX IX_EST_OEE_SUMMARY_RECONCILIATION_DATE
                        ON EST_OEE_SUMMARY (OEE_ID, OEE_DATE);
                    DROP INDEX IX_POM_LOT_PLANT_CREATED;
                    CREATE INDEX IX_POM_LOT_PLANT_CREATED
                        ON POM_LOT (PLANT_ID, LOT_ID);
                    DROP INDEX IX_POM_LOT_CREATED;
                    CREATE INDEX IX_POM_LOT_CREATED
                        ON POM_LOT (LOT_ID, CREATED_AT);
                    DROP INDEX IX_POM_LOT_HOLD_CREATED;
                    DROP INDEX IX_POM_LOT_DEFECT_QTY;
                    DROP INDEX IX_POM_LOT_HISTORY_OEE_TRACK_OUT;
                    CREATE INDEX IX_POM_LOT_HISTORY_OEE_TRACK_OUT
                        ON POM_LOT_HISTORY (TRACK_IN_TIME, TRACK_OUT_TIME);
                    DROP INDEX IX_POM_WORK_ORDER_PLAN_START;
                    DROP INDEX IX_POM_LOT_DISPOSITION_PLANT_DATE;
                    DROP INDEX IX_POM_WORK_SCOPE_SCOPE_TYPE;
                    CREATE INDEX IX_POM_LOT_MIXING_OUTPUT
                        ON POM_LOT_MIXING_RELATION (PLANT_ID, OUTPUT_LOT_ID, INPUT_LOT_ID);
                    """);

                SqliteSchemaInitializer.EnsureSchema(cs);
                SqliteSchemaInitializer.EnsureSchema(cs);
            }

            IndexKeys(cs, "IX_EMS_SPARE_USAGE_WO_TIME").Should().Equal(
                "WO_ID:ASC", "USED_AT:DESC");
            IndexSql(cs, "IX_EMS_SPARE_USAGE_WO_TIME").Should().Contain("WHERE WO_ID IS NOT NULL");

            IndexKeys(cs, "IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE").Should().Equal(
                "EQUIPMENT_ID:ASC", "EFFECTIVE_FROM:DESC", "ASSIGNMENT_ID:ASC", "EFFECTIVE_TO:ASC");
            IndexKeys(cs, "IX_RMS_RECIPE_ASSIGNMENT_CLASS_EFFECTIVE").Should().Equal(
                "EQUIPMENT_CLASS_ID:ASC", "EFFECTIVE_FROM:DESC", "ASSIGNMENT_ID:ASC", "EFFECTIVE_TO:ASC");
            IndexSql(cs, "IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE")
                .Should().Contain("WHERE EQUIPMENT_ID IS NOT NULL");
            IndexSql(cs, "IX_RMS_RECIPE_ASSIGNMENT_CLASS_EFFECTIVE")
                .Should().Contain("WHERE EQUIPMENT_CLASS_ID IS NOT NULL");

            IndexKeys(cs, "IX_EST_TAKT_RECONCILIATION_DATE").Should().Equal(
                "TAKT_DATE:ASC", "TAKT_SUMMARY_ID:ASC");
            IndexKeys(cs, "IX_EST_OEE_LOSS_RECONCILIATION_DATE").Should().Equal(
                "OEE_DATE:ASC", "LOSS_ID:ASC");
            IndexKeys(cs, "IX_EST_OEE_SUMMARY_RECONCILIATION_DATE").Should().Equal(
                "OEE_DATE:ASC", "OEE_ID:ASC");

            IndexKeys(cs, "IX_POM_LOT_PLANT_CREATED").Should().Equal(
                "PLANT_ID:ASC", "CREATED_AT:DESC", "LOT_ID:ASC");
            IndexKeys(cs, "IX_POM_LOT_CREATED").Should().Equal(
                "CREATED_AT:DESC", "LOT_ID:ASC");
            IndexKeys(cs, "IX_POM_LOT_HOLD_CREATED").Should().Equal(
                "CREATED_AT:DESC", "LOT_ID:ASC");
            IndexSql(cs, "IX_POM_LOT_HOLD_CREATED").Should().Contain("WHERE IS_HOLD = 'Y'");
            IndexKeys(cs, "IX_POM_LOT_DEFECT_QTY").Should().Equal(
                "DEFECT_QTY:DESC", "CREATED_AT:DESC", "LOT_ID:ASC");
            IndexSql(cs, "IX_POM_LOT_DEFECT_QTY").Should().Contain("WHERE DEFECT_QTY > 0");
            IndexKeys(cs, "IX_POM_LOT_HISTORY_OEE_TRACK_OUT").Should().Equal(
                "PLANT_ID:ASC", "EQUIPMENT_ID:ASC", "TRACK_OUT_TIME:ASC");
            IndexSql(cs, "IX_POM_LOT_HISTORY_OEE_TRACK_OUT")
                .Should().Contain("WHERE EXECUTION_ID = 'TrackOut' AND TRACK_OUT_TIME IS NOT NULL");
            IndexKeys(cs, "IX_POM_WORK_ORDER_PLAN_START").Should().Equal(
                "PLAN_START_DATE:DESC", "WORK_ORDER_ID:ASC");
            IndexKeys(cs, "IX_POM_WORK_SCOPE_SCOPE_TYPE").Should().Equal(
                "PLANT_ID:ASC", "SCOPE_TYPE:ASC", "CREATED_AT:DESC", "WORK_SCOPE_ID:ASC");
            var workScopeListSql = NamedQuerySql("sqlite", "POM", "POM.WorkScopeList")
                .Replace("@plantId", "'P1'", StringComparison.Ordinal)
                .Replace("@scopeType", "'Carrier'", StringComparison.Ordinal)
                .Replace("@targetId", "NULL", StringComparison.Ordinal)
                .Replace("@workScopeId", "NULL", StringComparison.Ordinal)
                .Replace("@parentScopeId", "NULL", StringComparison.Ordinal)
                .Replace("@workOrderId", "NULL", StringComparison.Ordinal)
                .Replace("@carrierId", "NULL", StringComparison.Ordinal)
                .Replace("@equipmentId", "NULL", StringComparison.Ordinal)
                .Replace("@status", "NULL", StringComparison.Ordinal);
            workScopeListSql.Should().Contain("SCOPE_TYPE = 'Carrier'");
            // SQLite is allowed to prefer a table scan for an empty fixture even when the
            // matching index exists. Validate the stable query contract and exact index shape;
            // production-sized tables will make the cost-based planner choose this path.
            IndexKeys(cs, "IX_POM_LOT_DISPOSITION_PLANT_DATE").Should().Equal(
                "PLANT_ID:ASC", "DECIDED_AT:DESC", "DISPOSITION_ID:DESC");
            IndexExists(cs, "IX_POM_LOT_MIXING_OUTPUT").Should().BeFalse(
                "the POM mixing primary key already owns this exact access path");
            CountIndexesWithKeys(cs, "POM_LOT_MIXING_RELATION",
                "PLANT_ID", "OUTPUT_LOT_ID", "INPUT_LOT_ID").Should().Be(1);

            // Material trace already has one narrow time-ordered path per selectable owner;
            // keep those V109 indexes instead of adding another overlapping write cost.
            IndexKeys(cs, "IX_IVT_MATERIAL_CONSUMPTION_LOT").Should().Equal(
                "MATERIAL_LOT_ID:ASC", "OCCURRED_AT:DESC");
            IndexKeys(cs, "IX_IVT_MATERIAL_CONSUMPTION_PROCESS_LOT").Should().Equal(
                "PROCESS_LOT_ID:ASC", "OCCURRED_AT:DESC");
            IndexKeys(cs, "IX_IVT_MATERIAL_CONSUMPTION_EQUIPMENT").Should().Equal(
                "EQUIPMENT_ID:ASC", "OCCURRED_AT:DESC");

            var spareUsageByWorkOrderSql = NamedQuerySql(
                "sqlite", "EMS", "EMS.SparePartUsageByWorkOrder");
            spareUsageByWorkOrderSql.Should().Contain("WHERE WO_ID=@workOrderId");
            spareUsageByWorkOrderSql.Should().Contain("(@from IS NULL OR USED_AT>=@from)");
            spareUsageByWorkOrderSql.Should().Contain("(@to IS NULL OR USED_AT<@to)");
            var boundSpareUsageSql = spareUsageByWorkOrderSql
                .Replace("@workOrderId", "'WO1'", StringComparison.Ordinal)
                .Replace("@from", "'2025-01-01'", StringComparison.Ordinal)
                .Replace("@to", "'2025-02-01'", StringComparison.Ordinal);
            QueryPlan(cs, boundSpareUsageSql).Should().Contain("IX_EMS_SPARE_USAGE_WO_TIME",
                "the exact SQLite named-query shape must use the V131 work-order index");
            QueryPlan(cs, """
                SELECT ASSIGNMENT_ID FROM RMS_RECIPE_EQUIPMENT_ASSIGNMENT
                WHERE EQUIPMENT_ID='EQ1' AND EFFECTIVE_FROM <= '2025-01-02'
                  AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO > '2025-01-02')
                ORDER BY EFFECTIVE_FROM DESC, ASSIGNMENT_ID LIMIT 1
                """).Should().Contain("IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE");
            QueryPlan(cs, """
                SELECT ASSIGNMENT_ID FROM RMS_RECIPE_EQUIPMENT_ASSIGNMENT
                WHERE EQUIPMENT_ID IS NULL AND EQUIPMENT_CLASS_ID='CLASS1'
                  AND EFFECTIVE_FROM <= '2025-01-02'
                  AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO > '2025-01-02')
                ORDER BY EFFECTIVE_FROM DESC, ASSIGNMENT_ID LIMIT 1
                """).Should().Contain("IX_RMS_RECIPE_ASSIGNMENT_CLASS_EFFECTIVE");
            var effectiveAssignmentPlan = QueryPlan(cs, """
                SELECT ASSIGNMENT_ID FROM RMS_RECIPE_EQUIPMENT_ASSIGNMENT
                WHERE EFFECTIVE_FROM <= '2025-01-02'
                  AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO > '2025-01-02')
                  AND (EQUIPMENT_ID='EQ1'
                    OR (EQUIPMENT_ID IS NULL AND EQUIPMENT_CLASS_ID='CLASS1'))
                ORDER BY CASE WHEN EQUIPMENT_ID IS NOT NULL THEN 0 ELSE 1 END,
                         EFFECTIVE_FROM DESC, ASSIGNMENT_ID LIMIT 1
                """);
            effectiveAssignmentPlan.Should().Contain("IX_RMS_RECIPE_ASSIGNMENT_EQUIPMENT_EFFECTIVE");
            effectiveAssignmentPlan.Should().Contain("IX_RMS_RECIPE_ASSIGNMENT_CLASS_EFFECTIVE");

            QueryPlan(cs, """
                SELECT TAKT_SUMMARY_ID FROM EST_TAKT_SUMMARY
                WHERE TAKT_SUMMARY_ID LIKE 'TKT_%'
                  AND TAKT_DATE >= '2025-01-01' AND TAKT_DATE < '2025-01-02'
                """).Should().Contain("IX_EST_TAKT_RECONCILIATION_DATE");
            QueryPlan(cs, """
                SELECT LOSS_ID FROM EST_OEE_LOSS
                WHERE LOSS_ID LIKE 'AGL_%'
                  AND OEE_DATE >= '2025-01-01' AND OEE_DATE < '2025-01-02'
                """).Should().Contain("IX_EST_OEE_LOSS_RECONCILIATION_DATE");
            QueryPlan(cs, """
                SELECT OEE_ID FROM EST_OEE_SUMMARY
                WHERE OEE_ID LIKE 'AGG_%'
                  AND OEE_DATE >= '2025-01-01' AND OEE_DATE < '2025-01-02'
                """).Should().Contain("IX_EST_OEE_SUMMARY_RECONCILIATION_DATE");

            QueryPlan(cs, """
                SELECT LOT_ID FROM POM_LOT
                WHERE PLANT_ID='P1' ORDER BY CREATED_AT DESC LIMIT 500
                """).Should().Contain("IX_POM_LOT_PLANT_CREATED");
            var lotListSql = NamedQuerySql("sqlite", "POM", "POM.LotList")
                .Replace("@plantId", "NULL", StringComparison.Ordinal)
                .Replace("@lotState", "NULL", StringComparison.Ordinal)
                .Replace("@isHold", "NULL", StringComparison.Ordinal);
            QueryPlan(cs, lotListSql).Should().Contain("IX_POM_LOT_CREATED");
            QueryPlan(cs, NamedQuerySql("sqlite", "POM", "POM.LotHoldList"))
                .Should().Contain("IX_POM_LOT_HOLD_CREATED");
            QueryPlan(cs, NamedQuerySql("sqlite", "POM", "POM.LotDefectList"))
                .Should().Contain("IX_POM_LOT_DEFECT_QTY");
            QueryPlan(cs, """
                SELECT LOT_HISTORY_ID, LOT_ID, PROCESS_ID, QTY, DEFECT_QTY,
                       TRACK_IN_TIME, TRACK_OUT_TIME
                FROM POM_LOT_HISTORY
                WHERE EXECUTION_ID='TrackOut' AND PLANT_ID='P1' AND EQUIPMENT_ID='EQ1'
                  AND TRACK_OUT_TIME >= '2025-01-01' AND TRACK_OUT_TIME < '2025-01-02'
                """).Should().Contain("IX_POM_LOT_HISTORY_OEE_TRACK_OUT");
            var workOrderListSql = NamedQuerySql("sqlite", "POM", "POM.WorkOrderList")
                .Replace("@plantId", "NULL", StringComparison.Ordinal)
                .Replace("@workOrderId", "NULL", StringComparison.Ordinal)
                .Replace("@productionOrderId", "NULL", StringComparison.Ordinal)
                .Replace("@routingScope", "NULL", StringComparison.Ordinal)
                .Replace("@processId", "NULL", StringComparison.Ordinal)
                .Replace("@equipmentId", "NULL", StringComparison.Ordinal)
                .Replace("@ownerId", "NULL", StringComparison.Ordinal)
                .Replace("@status", "NULL", StringComparison.Ordinal);
            QueryPlan(cs, workOrderListSql).Should().Contain("IX_POM_WORK_ORDER_PLAN_START");
            QueryPlan(cs, """
                SELECT DISPOSITION_ID FROM POM_LOT_DISPOSITION
                WHERE PLANT_ID='P1' AND DECIDED_AT >= '2025-01-01'
                ORDER BY DECIDED_AT DESC, DISPOSITION_ID DESC LIMIT 500
                """).Should().Contain("IX_POM_LOT_DISPOSITION_PLANT_DATE");
            var mixingPlan = QueryPlan(cs, """
                SELECT INPUT_LOT_ID FROM POM_LOT_MIXING_RELATION
                WHERE PLANT_ID='P1' AND OUTPUT_LOT_ID='OUT1'
                ORDER BY INPUT_LOT_ID
                """);
            mixingPlan.Should().Contain("sqlite_autoindex_POM_LOT_MIXING_RELATION_1");
            QueryPlan(cs, """
                SELECT CONSUMPTION_ID FROM IVT_MATERIAL_CONSUMPTION_HISTORY
                WHERE MATERIAL_LOT_ID='MAT1' AND OCCURRED_AT >= '2025-01-01'
                ORDER BY OCCURRED_AT DESC, CONSUMPTION_ID DESC LIMIT 500
                """).Should().Contain("IX_IVT_MATERIAL_CONSUMPTION_LOT");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_reports_deployed_duplicate_active_tool_positions_before_unique_index_creation()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, """
                DROP INDEX UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION;
                INSERT INTO EMS_TOOL_MOUNT_HISTORY
                  (MOUNT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, EQUIPMENT_ID,
                   POSITION_CODE, MOUNTED_AT, MOUNTED_BY, CREATED_BY, UPDATED_BY)
                VALUES
                  ('M_DUP_1', 'IDEM_DUP_1', 'HASH_DUP_1', 'TOOL_DUP_1', 'EQ_DUP',
                   'P01', CURRENT_TIMESTAMP, 'tester', 'tester', 'tester'),
                  ('M_DUP_2', 'IDEM_DUP_2', 'HASH_DUP_2', 'TOOL_DUP_2', 'EQ_DUP',
                   'P01', CURRENT_TIMESTAMP, 'tester', 'tester', 'tester');
                """);

            Action upgrade = () => SqliteSchemaInitializer.EnsureSchema(cs);

            upgrade.Should().Throw<InvalidOperationException>()
                .WithMessage("*V121*equipment='EQ_DUP'*position='P01'*M_DUP_1*M_DUP_2*");
            IndexExists(cs, "UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION").Should().BeFalse(
                "the initializer must not silently select one physical mount");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Theory]
    [InlineData("2025-01-01 00:00:00", "cannot precede its mount")]
    [InlineData("2025-01-04 00:00:00", "cannot follow its unmount")]
    public void Tool_usage_time_guard_reports_the_failed_mount_boundary(
        string usedAt,
        string expectedMessage)
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, """
                INSERT INTO EMS_TOOL_MOUNT_HISTORY
                  (MOUNT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, EQUIPMENT_ID,
                   POSITION_CODE, MOUNTED_AT, MOUNTED_BY, UNMOUNTED_AT, UNMOUNTED_BY,
                   UNMOUNT_IDEMPOTENCY_KEY, UNMOUNT_REQUEST_HASH, CREATED_BY, UPDATED_BY)
                VALUES
                  ('M_TIME', 'IDEM_M_TIME', 'HASH_M_TIME', 'TOOL_TIME', 'EQ_TIME',
                   'P01', '2025-01-02 00:00:00', 'tester', '2025-01-03 00:00:00', 'tester',
                   'IDEM_U_TIME', 'HASH_U_TIME', 'tester', 'tester');
                """);

            Action write = () => ExecSql(cs, $"""
                INSERT INTO EMS_TOOL_USAGE_HISTORY
                  (USAGE_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, MOUNT_ID, EQUIPMENT_ID,
                   USE_COUNT, USE_MINUTES, USED_AT, USED_BY, CREATED_BY, CREATED_AT)
                VALUES
                  ('U_TIME', 'IDEM_U_USAGE', 'HASH_U_USAGE', 'TOOL_TIME', 'M_TIME', 'EQ_TIME',
                   1, 0, '{usedAt}', 'tester', 'tester', CURRENT_TIMESTAMP);
                """);

            write.Should().Throw<SqliteException>().WithMessage($"*{expectedMessage}*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_ExistingDb_AddsScreenTargetTableWithoutAlterOrRecreate()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            TableExists(cs, "SYS_SCREEN_TARGET").Should().BeTrue();
            ExecSql(cs, "DROP TABLE SYS_SCREEN_TARGET;"); // V089 이전 DB 모사 — 기존 화면정의 테이블은 보존

            SqliteSchemaInitializer.EnsureSchema(cs);

            TableExists(cs, "SYS_SCREEN_TARGET").Should().BeTrue(
                "V089는 신규 테이블이라 SQLite 증분 CREATE 경로에서 자동 반영돼야 한다");
            Columns(cs, "SYS_SCREEN_TARGET").Should().Contain(
                new[] { "UI_ID", "TARGET_CHANNEL", "ENTRY_PATH", "CREATED_BY", "CREATED_AT", "UPDATED_BY", "UPDATED_AT" });
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ems_execution_integrity_is_enforced_for_fresh_and_incremental_schema(bool incremental)
    {
        var cs = NewDb();
        try
        {
            if (incremental)
            {
                CreatePreV115EmsTables(cs);
                SqliteSchemaInitializer.EnsureSchema(cs);
                Count(cs, "EMS_SPARE_PART_INOUT").Should().Be(1,
                    "the V115 upgrade must preserve legacy soft-reference ledger rows");
            }
            else
            {
                SqliteSchemaInitializer.Apply(cs);
            }

            Columns(cs, "EMS_WORK_ORDER").Should().Contain("MAINTENANCE_PLAN_ID");
            Columns(cs, "EMS_SPARE_PART_INOUT").Should().Contain(
                new[] { "IDEMPOTENCY_KEY", "BALANCE_BEFORE", "BALANCE_AFTER", "CLIENT_CHANNEL", "WO_ID" });
            TableExists(cs, "EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY").Should().BeTrue(
                "V115 must add the append-only manual due acknowledgement ledger to fresh and upgraded databases");

            SeedEmsMaintenancePlan(cs, "EMS_PLAN");
            SeedEmsWorkOrder(cs, "EMS_WO", maintenancePlanId: "EMS_PLAN");

            Action missingMaintenancePlan = () =>
                SeedEmsWorkOrder(cs, "EMS_WO_BAD_PLAN", maintenancePlanId: "MISSING_PLAN");
            missingMaintenancePlan.Should().Throw<SqliteException>();

            Action updateToMissingMaintenancePlan = () => ExecSql(cs,
                "UPDATE EMS_WORK_ORDER SET MAINTENANCE_PLAN_ID='MISSING_PLAN' WHERE WO_ID='EMS_WO';");
            updateToMissingMaintenancePlan.Should().Throw<SqliteException>();

            InsertEmsSparePartExecution(cs, "EMS_INOUT_VALID", 1, 3, 2, "MES", "EMS_WO");
            Count(cs, "EMS_SPARE_PART_INOUT").Should().Be(incremental ? 2 : 1,
                "a missing legacy PART_ID is intentionally a soft reference and must not reject a valid execution row");

            Action zeroQuantity = () =>
                InsertEmsSparePartExecution(cs, "EMS_INOUT_ZERO", 0, 3, 3, "MES", "EMS_WO");
            zeroQuantity.Should().Throw<SqliteException>();
            Action negativeBalanceBefore = () =>
                InsertEmsSparePartExecution(cs, "EMS_INOUT_NEG_BEFORE", 1, -1, 0, "MES", "EMS_WO");
            negativeBalanceBefore.Should().Throw<SqliteException>();
            Action negativeBalanceAfter = () =>
                InsertEmsSparePartExecution(cs, "EMS_INOUT_NEG_AFTER", 1, 1, -1, "MES", "EMS_WO");
            negativeBalanceAfter.Should().Throw<SqliteException>();
            Action missingChannel = () =>
                InsertEmsSparePartExecution(cs, "EMS_INOUT_NO_CHANNEL", 1, 1, 0, null, "EMS_WO");
            missingChannel.Should().Throw<SqliteException>();
            Action invalidChannel = () =>
                InsertEmsSparePartExecution(cs, "EMS_INOUT_BAD_CHANNEL", 1, 1, 0, "UNKNOWN", "EMS_WO");
            invalidChannel.Should().Throw<SqliteException>();
            Action missingProcessor = () =>
                InsertEmsSparePartExecution(cs, "EMS_INOUT_NO_PROCESSOR", 1, 1, 0, "POP", "EMS_WO", null);
            missingProcessor.Should().Throw<SqliteException>();
            Action missingWorkOrder = () =>
                InsertEmsSparePartExecution(cs, "EMS_INOUT_BAD_WO", 1, 1, 0, "MOBILE", "MISSING_WO");
            missingWorkOrder.Should().Throw<SqliteException>();

            Action invalidExecutionUpdate = () => ExecSql(cs,
                "UPDATE EMS_SPARE_PART_INOUT SET CLIENT_CHANNEL='UNKNOWN' WHERE INOUT_ID='EMS_INOUT_VALID';");
            invalidExecutionUpdate.Should().Throw<SqliteException>();

            ExecSql(cs, """
                INSERT INTO EMS_SPARE_PART_INOUT
                  (INOUT_ID, PART_ID, TRANSACTION_TYPE, QUANTITY, TRANSACTION_AT, PROCESSED_BY,
                   IDEMPOTENCY_KEY, BALANCE_BEFORE, BALANCE_AFTER, CLIENT_CHANNEL, WO_ID)
                VALUES ('EMS_INOUT_LEGACY', 'ORPHAN_SOFT_PART', 'Incoming', NULL,
                        CURRENT_TIMESTAMP, NULL, NULL, NULL, NULL, NULL, 'MISSING_WO');
                """);

            Action deleteReferencedPlan = () =>
                ExecSql(cs, "DELETE FROM EMS_MAINTENANCE_PLAN WHERE PLAN_ID='EMS_PLAN';");
            deleteReferencedPlan.Should().Throw<SqliteException>();
            Action renameReferencedPlan = () =>
                ExecSql(cs, "UPDATE EMS_MAINTENANCE_PLAN SET PLAN_ID='EMS_PLAN_RENAMED' WHERE PLAN_ID='EMS_PLAN';");
            renameReferencedPlan.Should().Throw<SqliteException>();
            Action deleteReferencedWorkOrder = () =>
                ExecSql(cs, "DELETE FROM EMS_WORK_ORDER WHERE WO_ID='EMS_WO';");
            deleteReferencedWorkOrder.Should().Throw<SqliteException>();
            Action renameReferencedWorkOrder = () =>
                ExecSql(cs, "UPDATE EMS_WORK_ORDER SET WO_ID='EMS_WO_RENAMED' WHERE WO_ID='EMS_WO';");
            renameReferencedWorkOrder.Should().Throw<SqliteException>();
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_backfills_only_unambiguous_ems_only_legacy_plan_ids()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            SeedEmsMaintenancePlan(cs, "EMS_ONLY");
            SeedEmsMaintenancePlan(cs, "BOTH");
            ExecSql(cs, """
                INSERT INTO POM_PRODUCTION_PLAN
                  (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY,
                   PLANNED_START_DATE, PLANNED_END_DATE, STATUS,
                   CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('POM_ONLY', 'POM_ONLY', 'P1', 'ITEM1', 1,
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'Draft',
                        'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
                INSERT INTO POM_PRODUCTION_PLAN
                  (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY,
                   PLANNED_START_DATE, PLANNED_END_DATE, STATUS,
                   CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('BOTH', 'BOTH', 'P1', 'ITEM1', 1,
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'Draft',
                        'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
                """);
            SeedEmsWorkOrder(cs, "WO_EMS_ONLY", legacyPlanId: "EMS_ONLY");
            SeedEmsWorkOrder(cs, "WO_POM_ONLY", legacyPlanId: "POM_ONLY");
            SeedEmsWorkOrder(cs, "WO_BOTH", legacyPlanId: "BOTH");
            SeedEmsWorkOrder(cs, "WO_ORPHAN", legacyPlanId: "ORPHAN");

            SqliteSchemaInitializer.EnsureSchema(cs);
            SqliteSchemaInitializer.EnsureSchema(cs);

            ScalarString(cs, "SELECT COALESCE(MAINTENANCE_PLAN_ID, '<NULL>') FROM EMS_WORK_ORDER WHERE WO_ID='WO_EMS_ONLY'")
                .Should().Be("EMS_ONLY");
            ScalarString(cs, "SELECT UPDATED_BY FROM EMS_WORK_ORDER WHERE WO_ID='WO_EMS_ONLY'")
                .Should().Be("SYSTEM_MIGRATION");
            ScalarString(cs, "SELECT COALESCE(MAINTENANCE_PLAN_ID, '<NULL>') FROM EMS_WORK_ORDER WHERE WO_ID='WO_POM_ONLY'")
                .Should().Be("<NULL>");
            ScalarString(cs, "SELECT COALESCE(MAINTENANCE_PLAN_ID, '<NULL>') FROM EMS_WORK_ORDER WHERE WO_ID='WO_BOTH'")
                .Should().Be("<NULL>");
            ScalarString(cs, "SELECT COALESCE(MAINTENANCE_PLAN_ID, '<NULL>') FROM EMS_WORK_ORDER WHERE WO_ID='WO_ORPHAN'")
                .Should().Be("<NULL>");
            ScalarString(cs, "SELECT GROUP_CONCAT(DISTINCT UPDATED_BY) FROM EMS_WORK_ORDER WHERE WO_ID <> 'WO_EMS_ONLY'")
                .Should().Be("TEST", "POM-only, ambiguous, and orphan plan ids require explicit operator review");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pom_routing_constraints_are_enforced_for_fresh_and_incremental_schema(bool incremental)
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            if (incremental)
            {
                ExecSql(cs, """
                    DROP TABLE POM_LOT_DEFECT_EXECUTION;
                    DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_ROUTING_BI;
                    DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_ROUTING_BU;
                    DROP TRIGGER IF EXISTS TR_POM_LOT_ROUTING_BI;
                    DROP TRIGGER IF EXISTS TR_POM_LOT_ROUTING_BU;
                    DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_ROUTING_BI;
                    DROP TRIGGER IF EXISTS TR_POM_LOT_EXECUTION_ROUTING_BU;
                    DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_REVIEW_BI;
                    DROP TRIGGER IF EXISTS TR_POM_ROUTE_EXCEPTION_REVIEW_BU;
                    DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_EXECUTION_VERSION_BI;
                    DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_EXECUTION_VERSION_BU;
                    """);
                SqliteSchemaInitializer.EnsureSchema(cs);
            }

            TableExists(cs, "POM_LOT_DEFECT_EXECUTION").Should().BeTrue();
            Columns(cs, "POM_LOT_DEFECT_EXECUTION").Should().Contain(
                new[] { "EXECUTION_ID", "LOT_ID", "PLANT_ID", "PROCESS_ID", "DEFECT_CODE",
                    "DEFECT_QTY", "EXECUTION_USER", "CLIENT_CHANNEL", "DEVICE_ID", "OCCURRED_AT" });
            Columns(cs, "POM_ROUTE_EXCEPTION").Should().Contain(
                new[] { "REVIEW_CLIENT_CHANNEL", "REVIEW_DEVICE_ID" });
            Columns(cs, "POM_WORK_ORDER_EXECUTION").Should().Contain(
                new[] { "EXPECTED_VERSION", "RESULT_VERSION" });
            Columns(cs, "POM_WORK_ORDER").Should().Contain("ROUTING_SCOPE");
            Columns(cs, "MDM_ROUTING_STEP").Should().Contain("PROCESS_ID");
            if (incremental)
            {
                SeedPomRoutingRows(cs);
                ExecSql(cs, """
                    DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_ROUTING_BI;
                    DROP TRIGGER IF EXISTS TR_POM_WORK_ORDER_ROUTING_BU;
                    UPDATE POM_WORK_ORDER
                       SET ROUTING_ID='RT1', ROUTING_STEP_NO=10, ROUTING_SCOPE='Unbound'
                     WHERE WORK_ORDER_ID='RP_WO';
                    """);
                SqliteSchemaInitializer.EnsureSchema(cs);
                ScalarString(cs, "SELECT ROUTING_SCOPE FROM POM_WORK_ORDER WHERE WORK_ORDER_ID='RP_WO'")
                    .Should().Be("Operation", "incremental upgrades infer the legacy routing-step binding");
            }
            else
            {
                SeedPomRoutingRows(cs);
            }
            AssertPomRoutingConstraints(cs);
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Fact]
    public void EnsureSchema_existing_database_adds_missing_routing_step_process_before_its_index()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);

            // V106 이전 운영 DB를 모사한다. PROCESS_ID와 해당 인덱스가 없는 상태에서도
            // 증분 초기화가 컬럼을 먼저 복구한 뒤 인덱스를 안전하게 생성해야 한다.
            ExecSql(cs, """
                DROP INDEX IF EXISTS IX_MDM_ROUTING_STEP_PROCESS;
                ALTER TABLE MDM_ROUTING_STEP DROP COLUMN PROCESS_ID;
                """);

            SqliteSchemaInitializer.EnsureSchema(cs);

            Columns(cs, "MDM_ROUTING_STEP").Should().Contain("PROCESS_ID");
            ScalarString(cs, """
                SELECT COUNT(*)
                  FROM sqlite_master
                 WHERE type='index' AND name='IX_MDM_ROUTING_STEP_PROCESS'
                """).Should().Be("1");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void Pom_cross_lot_mssql_migration_uses_composite_audit_foreign_keys()
    {
        var sql = MigrationSql("V107__POM_LOT_AUDIT_CROSS_LOT_INTEGRITY.sql");

        sql.Should().Contain("FOREIGN KEY (ROUTE_EXCEPTION_ID, LOT_ID)");
        sql.Should().Contain("REFERENCES POM_ROUTE_EXCEPTION (EXCEPTION_ID, LOT_ID)");
        sql.Should().Contain("FOREIGN KEY (EXECUTION_ID, LOT_ID)");
        sql.Should().Contain("REFERENCES POM_LOT_EXECUTION (EXECUTION_ID, LOT_ID)");
        sql.Should().Contain("FOREIGN KEY (LOT_ID, PLANT_ID)");
        sql.Should().Contain("FOREIGN KEY (APPLIED_EXECUTION_ID, LOT_ID)");
    }

    [Fact]
    public void Pom_routing_scope_mssql_migration_preserves_scope_and_master_process_contracts()
    {
        var sql = MigrationSql("V106__POM_WORK_ORDER_ROUTING_SCOPE.sql");

        sql.Should().Contain("ROUTING_SCOPE NVARCHAR(20) NOT NULL");
        sql.Should().Contain("ROUTING_SCOPE = 'SerialRoute'");
        sql.Should().Contain("ROUTING_STEP_NO IS NULL AND PROCESS_ID IS NULL");
        sql.Should().Contain("ALTER TABLE MDM_ROUTING_STEP ADD PROCESS_ID NVARCHAR(50) NULL");
        sql.Should().Contain("FOREIGN KEY (PROCESS_ID) REFERENCES MDM_PROCESS (PROCESS_ID)");
    }

    [Fact]
    public void V121_mssql_migration_reports_duplicate_active_tool_positions_before_unique_index()
    {
        var sql = MigrationSql("V121__EMS_TOOL_MOUNT_POSITION_GUARD.sql");

        var preflight = sql.IndexOf("HAVING COUNT_BIG(*) > 1", StringComparison.Ordinal);
        var message = sql.IndexOf("DECLARE @ConflictMessage NVARCHAR(2048)", StringComparison.Ordinal);
        var failure = sql.IndexOf("THROW 51221", StringComparison.Ordinal);
        var create = sql.IndexOf("CREATE UNIQUE INDEX UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION", StringComparison.Ordinal);

        preflight.Should().BeGreaterThanOrEqualTo(0);
        message.Should().BeGreaterThan(preflight);
        failure.Should().BeGreaterThan(message);
        create.Should().BeGreaterThan(failure,
            "deployed conflicts must stop the migration before index construction");
        sql.Should().Contain("@ConflictEquipmentId = EQUIPMENT_ID");
        sql.Should().Contain("@ConflictToolPosition = POSITION_CODE");
        sql.Should().Contain("@ConflictCount = COUNT_BIG(*)");
        sql.Should().Contain("@FirstMountId = MIN(MOUNT_ID)");
        sql.Should().Contain("EQUIPMENT_ID='");
        sql.Should().Contain("TOOL_POSITION='");
        sql.Should().Contain("ACTIVE_COUNT=");
        sql.Should().Contain("FIRST_MOUNT_ID='");
        sql.Should().Contain("THROW 51221, @ConflictMessage, 1",
            "ExecuteNonQuery must surface the actionable conflict in its exception message");
    }

    [Fact]
    public void V128_mssql_migration_reconciles_in_batches_and_verifies_all_utility_snapshot_references()
    {
        var sql = MigrationSql("V128__EST_UTILITY_CONFIG_BACKFILL_VERIFICATION.sql");

        sql.Should().Contain("SELECT TOP (5000) R.READING_ID");
        sql.Should().Contain("AND NOT EXISTS");
        sql.Should().Contain("WHERE M.CONFIG_VERSION = 1");
        sql.Should().Contain("H.CONFIG_VERSION BETWEEN 1 AND M.CONFIG_VERSION");
        sql.Should().Contain("METER_HISTORY_GAP");
        sql.Should().Contain("H.CONFIG_VERSION = M.CONFIG_VERSION");
        sql.Should().Contain("H.CONFIG_VERSION = R.METER_CONFIG_VERSION");
        sql.Should().Contain("H.CONFIG_VERSION = E.METER_CONFIG_VERSION");
        sql.Should().Contain("THROW 51228");
        sql.Should().Contain("-- SQLITE-OMIT-BEGIN");
        sql.Should().Contain("-- SQLITE-OMIT-END");
    }

    [Fact]
    public void Append_only_evidence_rejects_update_delete_and_replace_with_recursive_triggers_disabled()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            using var connection = new SqliteConnection(cs);
            connection.Open();

            Execute(connection, "PRAGMA recursive_triggers=OFF;");
            Scalar(connection, "PRAGMA recursive_triggers;").Should().Be("0");
            Execute(connection, """
                INSERT INTO MDM_EQUIPMENT_CHANGE_HISTORY
                  (CHANGE_ID, EQUIPMENT_ID, CHANGE_TYPE, ACTOR_ID, BEFORE_STATE_JSON,
                   AFTER_STATE_JSON, CHANGED_AT, CREATED_BY, CREATED_AT)
                VALUES ('CHG_APPEND', 'EQ_APPEND', 'Create', 'tester', NULL,
                        '{}', CURRENT_TIMESTAMP, 'tester', CURRENT_TIMESTAMP);

                INSERT INTO RMS_RECIPE_APPROVAL_HISTORY
                  (HISTORY_ID, IDEMPOTENCY_KEY, REQUEST_HASH, RECIPE_ID, FROM_STATE,
                   TO_STATE, CHANGED_BY, REASON, CHANGED_AT)
                VALUES ('RAH_APPEND', 'IDEM_RAH_APPEND', 'HASH_RAH_APPEND', 'RECIPE_APPEND',
                        'Draft', 'Pending', 'tester', 'test', CURRENT_TIMESTAMP);
                INSERT INTO RMS_RECIPE_COMMAND
                  (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH, RECIPE_ID,
                   SOURCE_RECIPE_ID, ACTOR_ID, CREATED_AT)
                VALUES ('RWC_APPEND', 'Create', 'IDEM_RWC_APPEND', 'HASH_RWC_APPEND',
                        'RECIPE_APPEND', NULL, 'tester', CURRENT_TIMESTAMP);
                INSERT INTO RMS_RECIPE_PARAM_COMMAND
                  (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH, PARAM_ID,
                   RECIPE_ID, PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER,
                   EXPECTED_VERSION, RESULT_VERSION, CHANGED_BY, CHANGED_AT)
                VALUES ('RPC_APPEND', 'Update', 'IDEM_RPC_APPEND', 'HASH_RPC_APPEND',
                        'PARAM_APPEND', 'RECIPE_APPEND', 'Temperature', '42', 'C', 1,
                        1, 2, 'tester', CURRENT_TIMESTAMP);

                INSERT INTO IVT_MATERIAL_CONSUMPTION_HISTORY
                  (CONSUMPTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                   MATERIAL_LOT_ID, MATERIAL_ID, CONSUMPTION_MODE, QUANTITY, UNIT,
                   SOURCE_EVENT_ID, SOURCE_SYSTEM, OPERATOR_ID, STATUS, OCCURRED_AT,
                   CREATED_BY, CREATED_AT)
                VALUES ('CON_APPEND', 'IDEM_CON_APPEND', 'HASH_CON_APPEND', 'P1', 'EQ1',
                        'LOT_APPEND', 'MAT1', 'Manual', 1, 'EA', 'SOURCE_CON_APPEND',
                        'TEST', 'tester', 'Committed', CURRENT_TIMESTAMP, 'tester', CURRENT_TIMESTAMP);
                INSERT INTO EMS_TOOL_SAVE_COMMAND
                  (COMMAND_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, EXPECTED_VERSION,
                   RESULT_VERSION, RESULT_JSON, ACTOR_ID, CREATED_AT)
                VALUES ('TSC_APPEND', 'IDEM_TSC_APPEND', 'HASH_TSC_APPEND', 'TOOL_APPEND',
                        0, 1, '{}', 'tester', CURRENT_TIMESTAMP);
                INSERT INTO EMS_SPARE_MASTER_COMMAND
                  (COMMAND_ID, ENTITY_TYPE, ENTITY_ID, IDEMPOTENCY_KEY, REQUEST_HASH,
                   EXPECTED_VERSION, RESULT_VERSION, RESULT_JSON, ACTOR_ID, CREATED_AT)
                VALUES ('SPC_APPEND', 'StockPolicy', 'PART_APPEND', 'IDEM_SPC_APPEND',
                        'HASH_SPC_APPEND', 0, 1, '{}', 'tester', CURRENT_TIMESTAMP);
                INSERT INTO EMS_WORK_ORDER_CREATE_COMMAND
                  (COMMAND_ID, IDEMPOTENCY_KEY, REQUEST_HASH, WO_ID, EQUIPMENT_ID, WO_TYPE,
                   DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, ACTOR_ID, SOURCE, CLIENT_CHANNEL, CREATED_AT)
                VALUES ('WOC_APPEND', 'IDEM_WOC_APPEND', 'HASH_WOC_APPEND', 'WO_APPEND',
                        'EQ_APPEND', 'PM', 'Append test', 'tech', CURRENT_TIMESTAMP,
                        'tester', 'Manual', 'MES', CURRENT_TIMESTAMP);

                INSERT INTO EST_UTILITY_METER
                  (METER_ID, METER_NAME, PLANT_ID, UTILITY_TYPE, UNIT, READING_MODE,
                   SCALE_FACTOR, UPDATED_BY, UPDATED_AT, CONFIG_VERSION)
                VALUES ('M_APPEND', 'Append meter', 'P1', 'Electricity', 'kWh', 'Cumulative',
                        1, 'tester', CURRENT_TIMESTAMP, 1);
                INSERT INTO EST_UTILITY_METER_EVENT
                  (EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, METER_ID, PLANT_ID, EVENT_TYPE,
                   OCCURRED_AT, REASON, PREVIOUS_VALUE, AFTER_VALUE, UNIT, ACTOR_USER_ID,
                   METER_CONFIG_VERSION)
                VALUES ('ME_APPEND', 'IDEM_ME_APPEND', 'HASH_ME_APPEND', 'M_APPEND', 'P1',
                        'Replacement', CURRENT_TIMESTAMP, 'test', 10, 1, 'kWh', 'tester', 1);
                INSERT INTO EST_UTILITY_METER_CONFIG_HISTORY
                  (HISTORY_ID, METER_ID, CONFIG_VERSION, METER_NAME, PLANT_ID, UTILITY_TYPE,
                   UNIT, READING_MODE, SCALE_FACTOR, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
                VALUES ('MCH_APPEND', 'M_APPEND', 1, 'Append meter', 'P1', 'Electricity',
                        'kWh', 'Cumulative', 1, 1, 'tester', CURRENT_TIMESTAMP);
                """);

            AssertAppendOnly(
                connection,
                "MDM_EQUIPMENT_CHANGE_HISTORY",
                "CHANGE_ID='CHG_APPEND'",
                "ACTOR_ID=ACTOR_ID",
                """
                INSERT OR REPLACE INTO MDM_EQUIPMENT_CHANGE_HISTORY
                  (CHANGE_ID, EQUIPMENT_ID, CHANGE_TYPE, ACTOR_ID, BEFORE_STATE_JSON,
                   AFTER_STATE_JSON, CHANGED_AT, CREATED_BY, CREATED_AT)
                VALUES ('CHG_APPEND', 'EQ_APPEND', 'Create', 'replacer', NULL,
                        '{}', CURRENT_TIMESTAMP, 'replacer', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "RMS_RECIPE_APPROVAL_HISTORY",
                "HISTORY_ID='RAH_APPEND'",
                "REASON=REASON",
                """
                INSERT OR REPLACE INTO RMS_RECIPE_APPROVAL_HISTORY
                  (HISTORY_ID, IDEMPOTENCY_KEY, REQUEST_HASH, RECIPE_ID, FROM_STATE,
                   TO_STATE, CHANGED_BY, REASON, CHANGED_AT)
                VALUES ('RAH_APPEND', 'IDEM_RAH_APPEND', 'HASH_CHANGED', 'RECIPE_APPEND',
                        'Draft', 'Pending', 'replacer', 'changed', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "RMS_RECIPE_COMMAND",
                "COMMAND_ID='RWC_APPEND'",
                "ACTOR_ID=ACTOR_ID",
                """
                INSERT OR REPLACE INTO RMS_RECIPE_COMMAND
                  (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH, RECIPE_ID,
                   SOURCE_RECIPE_ID, ACTOR_ID, CREATED_AT)
                VALUES ('RWC_APPEND', 'Create', 'IDEM_RWC_APPEND', 'HASH_CHANGED',
                        'RECIPE_APPEND', NULL, 'replacer', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "RMS_RECIPE_PARAM_COMMAND",
                "COMMAND_ID='RPC_APPEND'",
                "PARAM_VALUE=PARAM_VALUE",
                """
                INSERT OR REPLACE INTO RMS_RECIPE_PARAM_COMMAND
                  (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH, PARAM_ID,
                   RECIPE_ID, PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER,
                   EXPECTED_VERSION, RESULT_VERSION, CHANGED_BY, CHANGED_AT)
                VALUES ('RPC_APPEND', 'Update', 'IDEM_RPC_APPEND', 'HASH_CHANGED',
                        'PARAM_APPEND', 'RECIPE_APPEND', 'Temperature', '43', 'C', 1,
                        1, 2, 'replacer', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "IVT_MATERIAL_CONSUMPTION_HISTORY",
                "CONSUMPTION_ID='CON_APPEND'",
                "STATUS=STATUS",
                """
                INSERT OR REPLACE INTO IVT_MATERIAL_CONSUMPTION_HISTORY
                  (CONSUMPTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                   MATERIAL_LOT_ID, MATERIAL_ID, CONSUMPTION_MODE, QUANTITY, UNIT,
                   SOURCE_EVENT_ID, SOURCE_SYSTEM, OPERATOR_ID, STATUS, OCCURRED_AT,
                   CREATED_BY, CREATED_AT)
                VALUES ('CON_APPEND', 'IDEM_CON_APPEND', 'HASH_CHANGED', 'P1', 'EQ1',
                        'LOT_APPEND', 'MAT1', 'Manual', 1, 'EA', 'SOURCE_CON_APPEND',
                        'TEST', 'replacer', 'Committed', CURRENT_TIMESTAMP, 'replacer', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "EMS_TOOL_SAVE_COMMAND",
                "COMMAND_ID='TSC_APPEND'",
                "RESULT_JSON=RESULT_JSON",
                """
                INSERT OR REPLACE INTO EMS_TOOL_SAVE_COMMAND
                  (COMMAND_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, EXPECTED_VERSION,
                   RESULT_VERSION, RESULT_JSON, ACTOR_ID, CREATED_AT)
                VALUES ('TSC_APPEND', 'IDEM_TSC_APPEND', 'HASH_CHANGED', 'TOOL_APPEND',
                        0, 1, '{"changed":true}', 'replacer', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "EMS_SPARE_MASTER_COMMAND",
                "COMMAND_ID='SPC_APPEND'",
                "RESULT_JSON=RESULT_JSON",
                """
                INSERT OR REPLACE INTO EMS_SPARE_MASTER_COMMAND
                  (COMMAND_ID, ENTITY_TYPE, ENTITY_ID, IDEMPOTENCY_KEY, REQUEST_HASH,
                   EXPECTED_VERSION, RESULT_VERSION, RESULT_JSON, ACTOR_ID, CREATED_AT)
                VALUES ('SPC_APPEND', 'StockPolicy', 'PART_APPEND', 'IDEM_SPC_APPEND',
                        'HASH_CHANGED', 0, 1, '{"changed":true}', 'replacer', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "EMS_WORK_ORDER_CREATE_COMMAND",
                "COMMAND_ID='WOC_APPEND'",
                "DESCRIPTION=DESCRIPTION",
                """
                INSERT OR REPLACE INTO EMS_WORK_ORDER_CREATE_COMMAND
                  (COMMAND_ID, IDEMPOTENCY_KEY, REQUEST_HASH, WO_ID, EQUIPMENT_ID, WO_TYPE,
                   DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, ACTOR_ID, SOURCE, CLIENT_CHANNEL, CREATED_AT)
                VALUES ('WOC_APPEND', 'IDEM_WOC_APPEND', 'HASH_CHANGED', 'WO_APPEND',
                        'EQ_APPEND', 'PM', 'Changed', 'tech', CURRENT_TIMESTAMP,
                        'replacer', 'Manual', 'MES', CURRENT_TIMESTAMP);
                """);
            AssertAppendOnly(
                connection,
                "EST_UTILITY_METER_EVENT",
                "EVENT_ID='ME_APPEND'",
                "REASON=REASON",
                """
                INSERT OR REPLACE INTO EST_UTILITY_METER_EVENT
                  (EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, METER_ID, PLANT_ID, EVENT_TYPE,
                   OCCURRED_AT, REASON, PREVIOUS_VALUE, AFTER_VALUE, UNIT, ACTOR_USER_ID,
                   METER_CONFIG_VERSION)
                VALUES ('ME_APPEND', 'IDEM_ME_APPEND', 'HASH_CHANGED', 'M_APPEND', 'P1',
                        'Replacement', CURRENT_TIMESTAMP, 'changed', 10, 1, 'kWh', 'replacer', 1);
                """);
            AssertAppendOnly(
                connection,
                "EST_UTILITY_METER_CONFIG_HISTORY",
                "METER_ID='M_APPEND' AND CONFIG_VERSION=1",
                "METER_NAME=METER_NAME",
                """
                INSERT OR REPLACE INTO EST_UTILITY_METER_CONFIG_HISTORY
                  (HISTORY_ID, METER_ID, CONFIG_VERSION, METER_NAME, PLANT_ID, UTILITY_TYPE,
                   UNIT, READING_MODE, SCALE_FACTOR, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
                VALUES ('MCH_APPEND', 'M_APPEND', 1, 'Changed meter', 'P1', 'Electricity',
                        'kWh', 'Cumulative', 1, 1, 'replacer', CURRENT_TIMESTAMP);
                """);
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Fact]
    public void EnsureSchema_rejects_non_contiguous_utility_meter_configuration_history()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);
            ExecSql(cs, """
                INSERT INTO EST_UTILITY_METER
                  (METER_ID, METER_NAME, PLANT_ID, UTILITY_TYPE, UNIT, READING_MODE,
                   SCALE_FACTOR, UPDATED_BY, UPDATED_AT, CONFIG_VERSION)
                VALUES ('M_GAP', 'Gap meter', 'P1', 'Electricity', 'kWh', 'Cumulative',
                        1, 'tester', CURRENT_TIMESTAMP, 3);
                INSERT INTO EST_UTILITY_METER_CONFIG_HISTORY
                  (HISTORY_ID, METER_ID, CONFIG_VERSION, METER_NAME, PLANT_ID, UTILITY_TYPE,
                   UNIT, READING_MODE, SCALE_FACTOR, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
                VALUES
                  ('M_GAP_V1', 'M_GAP', 1, 'Gap meter v1', 'P1', 'Electricity',
                   'kWh', 'Cumulative', 1, 1, 'tester', CURRENT_TIMESTAMP),
                  ('M_GAP_V3', 'M_GAP', 3, 'Gap meter v3', 'P1', 'Electricity',
                   'kWh', 'Cumulative', 1, 1, 'tester', CURRENT_TIMESTAMP);
                """);

            Action restart = () => SqliteSchemaInitializer.EnsureSchema(cs);

            restart.Should().Throw<InvalidOperationException>()
                .WithMessage("*V128*objectType='METER_HISTORY_GAP'*objectId='M_GAP'*configVersion=3*");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort temporary file cleanup */ } }
    }

    [Theory]
    [InlineData("V124__MDM_EQUIPMENT_CHANGE_HISTORY.sql", "TR_MDM_EQUIPMENT_CHANGE_APPEND_ONLY", "PK_MDM_EQUIPMENT_CHANGE_HISTORY")]
    [InlineData("V126__RMS_RECIPE_APPROVAL_HISTORY.sql", "TR_RMS_RECIPE_APPROVAL_HISTORY_APPEND_ONLY", "UX_RMS_RECIPE_APPROVAL_HISTORY_IDEMPOTENCY")]
    [InlineData("V136__RMS_RECIPE_PARAMETER_CONCURRENCY.sql", "TR_RMS_RECIPE_COMMAND_APPEND_ONLY", "UX_RMS_RECIPE_COMMAND_IDEMPOTENCY")]
    [InlineData("V136__RMS_RECIPE_PARAMETER_CONCURRENCY.sql", "TR_RMS_RECIPE_PARAM_COMMAND_APPEND_ONLY", "UX_RMS_RECIPE_PARAM_COMMAND_IDEMPOTENCY")]
    [InlineData("V137__IVT_MATERIAL_CONSUMPTION_APPEND_ONLY.sql", "TR_IVT_MATERIAL_CONSUMPTION_APPEND_ONLY", "UX_IVT_MATERIAL_CONSUMPTION_KEY")]
    [InlineData("V138__EMS_TOOL_MASTER_CONCURRENCY.sql", "TR_EMS_TOOL_SAVE_COMMAND_APPEND_ONLY", "UX_EMS_TOOL_SAVE_COMMAND_IDEMPOTENCY")]
    [InlineData("V139__EMS_SPARE_MASTER_COMMAND_LEDGER.sql", "TR_EMS_SPARE_MASTER_COMMAND_APPEND_ONLY", "UX_EMS_SPARE_MASTER_COMMAND_IDEMPOTENCY")]
    [InlineData("V140__EMS_WORK_ORDER_CREATE_COMMAND.sql", "TR_EMS_WORK_ORDER_CREATE_COMMAND_APPEND_ONLY", "UX_EMS_WORK_ORDER_CREATE_COMMAND_IDEMPOTENCY")]
    [InlineData("V151__IVT_TRACE_MATERIAL_CONFIGURATION_COMMANDS.sql", "TR_IVT_TRACE_BINDING_COMMAND_APPEND_ONLY", "UX_IVT_TRACE_BINDING_COMMAND_IDEMPOTENCY")]
    [InlineData("V151__IVT_TRACE_MATERIAL_CONFIGURATION_COMMANDS.sql", "TR_IVT_FEED_SESSION_COMMAND_APPEND_ONLY", "UX_IVT_FEED_SESSION_COMMAND_IDEMPOTENCY")]
    public void Mssql_evidence_migrations_define_append_only_update_delete_guards(
        string migrationFile, string triggerName, string collisionConstraint)
    {
        var sql = MigrationSql(migrationFile);

        sql.Should().Contain($"CREATE TRIGGER {triggerName}");
        sql.Should().Contain("AFTER UPDATE, DELETE");
        sql.Should().Contain("is append-only");
        sql.Should().Contain(collisionConstraint,
            "SQL Server must reject an insert collision instead of replacing immutable evidence");
    }

    [Fact]
    public void V127_mssql_migration_guards_both_utility_evidence_ledgers()
    {
        var sql = MigrationSql("V127__EST_UTILITY_CONFIG_HISTORY.sql");
        var eventSql = MigrationSql("V122__EST_UTILITY_METER_EVENT.sql");

        sql.Should().Contain("CREATE TRIGGER TR_EST_UTILITY_METER_EVENT_APPEND_ONLY");
        sql.Should().Contain("CREATE TRIGGER TR_EST_UTILITY_CONFIG_HISTORY_APPEND_ONLY");
        Regex.Matches(sql, "AFTER UPDATE, DELETE", RegexOptions.CultureInvariant)
            .Should().HaveCount(2);
        sql.Should().Contain("PK_EST_UTILITY_METER_CONFIG_HISTORY");
        sql.Should().Contain("UX_EST_UTILITY_METER_CONFIG_HISTORY_ID");
        eventSql.Should().Contain("PK_EST_UTILITY_METER_EVENT");
        eventSql.Should().Contain("UX_EST_UTILITY_METER_EVENT_IDEMPOTENCY");
    }

    [Fact]
    public void V149_mssql_migration_preserves_singleton_fence_and_guards_transitions()
    {
        var sql = MigrationSql("V149__FDC_RUNTIME_OWNERSHIP_FENCE.sql");

        sql.Should().Contain("CONSTRAINT PK_FDC_RUNTIME_OWNERSHIP PRIMARY KEY (LEASE_SCOPE)");
        sql.Should().Contain("CONSTRAINT CK_FDC_RUNTIME_OWNERSHIP_SCOPE CHECK (LEASE_SCOPE = 'GLOBAL')");
        sql.Should().Contain("FENCE_TOKEN         BIGINT");
        sql.Should().Contain("CONFIG_REVISION     NVARCHAR(64)");
        sql.Should().Contain("LEASE_SECRET_HASH   NVARCHAR(64)");
        sql.Should().Contain("CK_FDC_RUNTIME_OWNERSHIP_DIGESTS");
        sql.Should().Contain("LEASE_SECRET_HASH COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain("CREATE TRIGGER TR_FDC_RUNTIME_OWNERSHIP_FENCE");
        sql.Should().Contain("AFTER UPDATE, DELETE");
        sql.Should().Contain("DECLARE @Now DATETIME2(3) = SYSUTCDATETIME()");
        sql.Should().Contain("D.LEASE_EXPIRES_AT <= @Now");
        sql.Should().Contain("I.HEARTBEAT_AT BETWEEN DATEADD(SECOND, -5, @Now) AND @Now");
        sql.Should().Contain("I.LEASE_EXPIRES_AT <= DATEADD(DAY, 1, I.HEARTBEAT_AT)");
        sql.Should().Contain("D.FENCE_TOKEN = I.FENCE_TOKEN - 1");
        sql.Should().Contain("row and fence counter are not deletable");
        sql.Should().Contain("-- SQLITE-OMIT-BEGIN");
        sql.Should().Contain("-- SQLITE-OMIT-END");
    }

    [Fact]
    public void V150_mssql_migration_preserves_a_monotonic_full_precision_retention_boundary()
    {
        var sql = MigrationSql("V150__FDC_TRACE_RETENTION_STATE.sql");

        sql.Should().Contain("CONSTRAINT PK_FDC_TRACE_RETENTION_STATE PRIMARY KEY (STATE_ID)");
        sql.Should().Contain("CONSTRAINT CK_FDC_TRACE_RETENTION_STATE_ID CHECK (STATE_ID = 'GLOBAL')");
        sql.Should().Contain("COMPLETENESS_BOUNDARY    DATETIME2(7)");
        sql.Should().Contain("DATEADD(NANOSECOND, 100, MIN(COLLECTED_AT))");
        sql.Should().Contain("FROM FDC_COLLECT_DATA WITH (TABLOCKX, HOLDLOCK)",
            "the migration transaction must exclude downlevel retention deletes until the seed and guard commit");
        sql.Should().Contain("CREATE TRIGGER TR_FDC_TRACE_RETENTION_STATE_GUARD");
        sql.Should().Contain("CREATE TRIGGER TR_FDC_COLLECT_RETENTION_DELETE_GUARD");
        sql.Should().Contain("CREATE TRIGGER TR_FDC_COLLECT_COMPLETENESS_INSERT_GUARD");
        sql.Should().Contain("FROM FDC_TRACE_RETENTION_STATE S WITH (HOLDLOCK)",
            "the insert guard must see the current boundary under serializable locking");
        sql.Should().Contain("I.COLLECTED_AT >= S.COMPLETENESS_BOUNDARY");
        sql.Should().Contain("CREATE TRIGGER TR_FDC_COLLECT_APPEND_ONLY_UPDATE");
        sql.Should().Contain("FDC raw TRACE is append-only");
        sql.Should().Contain("AFTER UPDATE, DELETE");
        sql.Should().Contain("I.COMPLETENESS_BOUNDARY < D.COMPLETENESS_BOUNDARY");
        sql.Should().Contain("completeness state is not deletable");
        sql.Should().Contain("D.COLLECTED_AT < S.COMPLETENESS_BOUNDARY");
        sql.Should().Contain("-- SQLITE-OMIT-BEGIN");
        sql.Should().Contain("-- SQLITE-OMIT-END");
    }

    [Fact]
    public void V154_mssql_migration_rejects_existing_sequence_collisions_before_unique_index()
    {
        var sql = MigrationSql("V154__POM_WORK_SCOPE_MEMBER_SEQUENCE_UNIQUENESS.sql");

        sql.Should().Contain("GROUP BY WORK_SCOPE_ID, SEQUENCE_NO");
        sql.Should().Contain("HAVING COUNT_BIG(*) > 1");
        sql.Should().Contain("THROW 51523");
        sql.Should().Contain("CREATE UNIQUE INDEX UX_POM_WORK_SCOPE_MEMBER_SEQUENCE");
        sql.Should().Contain("ON POM_WORK_SCOPE_MEMBER (WORK_SCOPE_ID, SEQUENCE_NO)");
    }

    [Fact]
    public void V156_projection_inbox_keeps_event_hash_identity_and_monotonic_sequence_cursor_contracts()
    {
        var sql = MigrationSql("V156__POM_WORK_SCOPE_PROJECTION_INBOX.sql");

        sql.Should().Contain("PRIMARY KEY (SOURCE_CLIENT_ID, EVENT_ID)");
        sql.Should().Contain("REQUEST_HASH               CHAR(64) COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain(
            "SOURCE_CLIENT_ID           NVARCHAR(100) COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain("EVENT_ID                   NVARCHAR(200) COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain("SEQUENCE_RUN_ID            NVARCHAR(100) COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain("PROJECTION_STATUS          NVARCHAR(30) COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain("CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_REVISION");
        sql.Should().NotContain("CREATE UNIQUE INDEX UX_POM_WORK_SCOPE_PROJECTION_REVISION",
            "one recovery revision may legitimately emit multiple status events");
        sql.Should().Contain("PRIMARY KEY (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID)");
        sql.Should().Contain("OPERATION_KEY        NVARCHAR(200)");
        sql.Should().Contain("PAIR_RUN_ID          NVARCHAR(100)");
        sql.Should().Contain("RECIPE_SNAPSHOT_HASH CHAR(64) COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain("CARRIERS_JSON        NVARCHAR(MAX)");
        sql.Should().Contain("TERMINAL_CLEANUP_COMPLETED = 0");
        sql.Should().Contain("PROJECTION_STATUS IN ('Completed', 'Abandoned')");
        sql.Should().Contain("TR_POM_WORK_SCOPE_PROJECTION_INBOX_APPEND_ONLY");
        sql.Should().Contain("TR_POM_WORK_SCOPE_PROJECTION_INBOX_SCOPE");
        sql.Should().Contain("requires exact equipment ownership");
        sql.Should().Contain("TR_POM_WORK_SCOPE_PROJECTION_CURRENT_IDENTITY");
        sql.Should().Contain("AFTER INSERT, UPDATE, DELETE");
        sql.Should().Contain("THROW 51529");
        sql.Should().Contain("I.SOURCE_REVISION < D.SOURCE_REVISION");
        sql.Should().Contain("I.ACCEPTED_AT <= D.ACCEPTED_AT");
        sql.Should().Contain("THROW 51530");
        sql.Should().Contain("must reference its exact inbox event");
        sql.Should().Contain("-- SQLITE-OMIT-BEGIN");
        sql.Should().Contain("-- SQLITE-OMIT-END");
    }

    [Fact]
    public void V156_projection_tables_indexes_and_guards_are_restored_by_incremental_sqlite_startup()
    {
        var cs = NewDb();
        var contributions = new[] { new PomWorkScopeProjectionSqliteSchemaContribution() };
        try
        {
            SqliteSchemaInitializer.Apply(cs, contributions);
            ExecSql(cs, """
                DROP TABLE POM_WORK_SCOPE_PROJECTION_CURRENT;
                DROP TABLE POM_WORK_SCOPE_PROJECTION_INBOX;
                """);

            SqliteSchemaInitializer.EnsureSchema(cs, contributions);

            TableExists(cs, "POM_WORK_SCOPE_PROJECTION_INBOX").Should().BeTrue();
            TableExists(cs, "POM_WORK_SCOPE_PROJECTION_CURRENT").Should().BeTrue();
            IndexExists(cs, "IX_POM_WORK_SCOPE_PROJECTION_REVISION").Should().BeTrue();
            IndexExists(cs, "IX_POM_WORK_SCOPE_PROJECTION_SCOPE_TIME").Should().BeTrue();
            IndexExists(cs, "UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE").Should().BeTrue();
            Columns(cs, "POM_WORK_SCOPE_PROJECTION_CURRENT")
                .Should().Contain(new[]
                {
                    "WORK_SCOPE_ID", "OPERATION_KEY", "PAIR_RUN_ID", "RECIPE_ID",
                    "RECIPE_SNAPSHOT_HASH", "PROGRAM_HASH", "CARRIERS_JSON",
                });
            ScalarString(cs, """
                SELECT COUNT(*) FROM sqlite_master
                 WHERE type='trigger'
                   AND name IN (
                       'TR_POM_WORK_SCOPE_PROJECTION_INBOX_UPDATE_GUARD',
                       'TR_POM_WORK_SCOPE_PROJECTION_INBOX_DELETE_GUARD',
                        'TR_POM_WORK_SCOPE_PROJECTION_INBOX_REPLACE_GUARD',
                        'TR_POM_WORK_SCOPE_PROJECTION_INBOX_SCOPE_GUARD',
                        'TR_POM_WORK_SCOPE_PROJECTION_SCOPE_DELETE_GUARD',
                        'TR_POM_WORK_SCOPE_PROJECTION_SCOPE_ID_UPDATE_GUARD',
                        'TR_POM_WORK_SCOPE_PROJECTION_SCOPE_REPLACE_GUARD',
                       'TR_POM_WORK_SCOPE_PROJECTION_CURRENT_IDENTITY_GUARD',
                       'TR_POM_WORK_SCOPE_PROJECTION_CURRENT_MONOTONIC_GUARD',
                       'TR_POM_WORK_SCOPE_PROJECTION_CURRENT_EVENT_BI',
                       'TR_POM_WORK_SCOPE_PROJECTION_CURRENT_EVENT_BU',
                       'TR_POM_WORK_SCOPE_PROJECTION_CURRENT_DELETE_GUARD',
                       'TR_POM_WORK_SCOPE_PROJECTION_CURRENT_REPLACE_GUARD')
                """).Should().Be("13");
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public void Work_scope_repository_serializes_sql_server_member_sequence_allocation()
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.POM", "Infrastructure", "WorkScopeRepository.cs"));

        source.Should().Contain("DatabaseProviderKind.SqlServer");
        source.Should().Contain("WITH (UPDLOCK, HOLDLOCK)");
        source.Should().Contain("InsertMemberSqlSqlServer");
        source.Should().Contain("MAX(SEQUENCE_NO) + 1");
    }

    [Fact]
    public void Spare_part_usage_by_work_order_named_query_has_matching_dialect_contracts()
    {
        var sqlite = NamedQuerySql("sqlite", "EMS", "EMS.SparePartUsageByWorkOrder");
        var mssql = NamedQuerySql("mssql", "EMS", "EMS.SparePartUsageByWorkOrder");

        foreach (var sql in new[] { sqlite, mssql })
        {
            sql.Should().Contain("WHERE WO_ID=@workOrderId");
            sql.Should().Contain("(@from IS NULL OR USED_AT>=@from)");
            sql.Should().Contain("(@to IS NULL OR USED_AT<@to)");
            sql.Should().NotContain("@workOrderId IS NULL",
                "the dedicated query must preserve a sargable required work-order equality");
            sql.Should().Contain("ORDER BY USED_AT DESC");
        }
    }

    [Fact]
    public void Dashboard_lot_and_work_order_queries_are_bounded_and_deterministic_in_both_dialects()
    {
        var contracts = new[]
        {
            (Module: "POM", QueryId: "POM.LotList", Order: "ORDER BY CREATED_AT DESC, LOT_ID"),
            (Module: "POM", QueryId: "POM.LotHoldList", Order: "ORDER BY CREATED_AT DESC, LOT_ID"),
            (Module: "POM", QueryId: "POM.LotDefectList", Order: "ORDER BY DEFECT_QTY DESC, CREATED_AT DESC, LOT_ID"),
            (Module: "POM", QueryId: "POM.WorkOrderList", Order: "ORDER BY PLAN_START_DATE DESC, WORK_ORDER_ID"),
            (Module: "EMS", QueryId: "EMS.WorkOrderList", Order: "ORDER BY ISSUED_AT DESC, WO_ID DESC"),
        };

        foreach (var contract in contracts)
        {
            var sqlite = NamedQuerySql("sqlite", contract.Module, contract.QueryId);
            var mssql = NamedQuerySql("mssql", contract.Module, contract.QueryId);

            sqlite.Should().Contain(contract.Order);
            sqlite.Should().Contain("LIMIT 500");
            mssql.Should().Contain(contract.Order);
            mssql.Should().Contain("SELECT TOP 500");
        }
    }

    [Fact]
    public void V121_through_v157_migrations_keep_unique_numeric_versions_and_module_owned_names()
    {
        var migrationDirectory = Path.GetDirectoryName(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
            "V121__EMS_TOOL_MOUNT_POSITION_GUARD.sql"))!;
        var expected = new Dictionary<int, (string FileName, string Owner)>
        {
            [121] = ("V121__EMS_TOOL_MOUNT_POSITION_GUARD.sql", "EMS"),
            [122] = ("V122__EST_UTILITY_METER_EVENT.sql", "EST"),
            [123] = ("V123__EST_OEE_AGGREGATION_INTEGRITY.sql", "EST"),
            [124] = ("V124__MDM_EQUIPMENT_CHANGE_HISTORY.sql", "MDM"),
            [125] = ("V125__EMS_MASTER_INTEGRITY.sql", "EMS"),
            [126] = ("V126__RMS_RECIPE_APPROVAL_HISTORY.sql", "RMS"),
            [127] = ("V127__EST_UTILITY_CONFIG_HISTORY.sql", "EST"),
            [128] = ("V128__EST_UTILITY_CONFIG_BACKFILL_VERIFICATION.sql", "EST"),
            [129] = ("V129__IVT_TRACE_PROJECTION_QUERY_INDEXES.sql", "IVT"),
            [130] = ("V130__EMS_MAINTENANCE_QUERY_INDEXES.sql", "EMS"),
            [131] = ("V131__EMS_SPARE_USAGE_WORK_ORDER_INDEX.sql", "EMS"),
            [132] = ("V132__RMS_ASSIGNMENT_EFFECTIVE_WINDOW_INDEXES.sql", "RMS"),
            [133] = ("V133__EST_OEE_RECONCILIATION_DATE_INDEXES.sql", "EST"),
            [134] = ("V134__POM_LOT_READ_PATH_INDEXES.sql", "POM"),
            [135] = ("V135__EST_CARRIER_OUTPUT_SCOPE.sql", "EST"),
            [136] = ("V136__RMS_RECIPE_PARAMETER_CONCURRENCY.sql", "RMS"),
            [137] = ("V137__IVT_MATERIAL_CONSUMPTION_APPEND_ONLY.sql", "IVT"),
            [138] = ("V138__EMS_TOOL_MASTER_CONCURRENCY.sql", "EMS"),
            [139] = ("V139__EMS_SPARE_MASTER_COMMAND_LEDGER.sql", "EMS"),
            [140] = ("V140__EMS_WORK_ORDER_CREATE_COMMAND.sql", "EMS"),
            [141] = ("V141__FDC_OPEN_STATE_RECOVERY_INDEXES.sql", "FDC"),
            [142] = ("V142__IVT_TRACE_INGESTION_CURSOR.sql", "IVT"),
            [143] = ("V143__FDC_ENDPOINT_TAG_MAP.sql", "FDC"),
            [144] = ("V144__POM_OEE_TRACK_OUT_INDEX.sql", "POM"),
            [145] = ("V145__FDC_PLC_ENDPOINT_CONFIGURATION.sql", "FDC"),
            [146] = ("V146__FDC_INTERLOCK_EFFECT_LIFECYCLE.sql", "FDC"),
            [147] = ("V147__IVT_TRACE_WORK_STATE_INTEGRITY.sql", "IVT"),
            [148] = ("V148__FDC_LIFECYCLE_TRANSITION_AND_RETENTION.sql", "FDC"),
            [149] = ("V149__FDC_RUNTIME_OWNERSHIP_FENCE.sql", "FDC"),
             [150] = ("V150__FDC_TRACE_RETENTION_STATE.sql", "FDC"),
            [151] = ("V151__IVT_TRACE_MATERIAL_CONFIGURATION_COMMANDS.sql", "IVT"),
            [152] = ("V152__POM_WORK_SCOPE_AND_TOOL_CLEANING.sql", "POM"),
            [153] = ("V153__RMS_RECIPE_EXECUTION_WORK_SCOPE.sql", "RMS"),
            [154] = ("V154__POM_WORK_SCOPE_MEMBER_SEQUENCE_UNIQUENESS.sql", "POM"),
            [155] = ("V155__POM_WORK_SCOPE_SCOPE_TYPE_INDEX.sql", "POM"),
            [156] = ("V156__POM_WORK_SCOPE_PROJECTION_INBOX.sql", "POM"),
            [157] = ("V157__POM_WORK_SCOPE_PROJECTION_APPLICATION.sql", "POM"),
        };
        var recentFiles = Directory.EnumerateFiles(migrationDirectory, "V*.sql")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => (Name: name!, Match: Regex.Match(name!, @"^V(?<version>[0-9]{3})__")))
            .Where(item => item.Match.Success)
            .Select(item => (item.Name, Version: int.Parse(item.Match.Groups["version"].Value)))
            .Where(item => item.Version is >= 121 and <= 157)
            .ToArray();

        recentFiles.GroupBy(item => item.Version).Should().OnlyContain(group => group.Count() == 1);
        foreach (var (version, contract) in expected)
        {
            var file = recentFiles.SingleOrDefault(item => item.Version == version).Name;
            file.Should().Be(contract.FileName);
            File.ReadLines(Path.Combine(migrationDirectory, contract.FileName)).First()
                .Should().StartWith($"-- Owner: {contract.Owner}.");
        }
    }

    [Fact]
    public void Sqlite_migration_catalog_requires_strict_names_and_numeric_order()
    {
        var directory = Directory.CreateTempSubdirectory("nexa-sqlite-migrations-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "V010__EMS_TEN.sql"), "-- ten");
            File.WriteAllText(Path.Combine(directory, "V002__MDM_TWO.sql"), "-- two");
            File.WriteAllText(Path.Combine(directory, "V001__SYS_ONE.sql"), "-- one");

            SqliteSchemaInitializer.GetOrderedMigrationFiles(directory)
                .Select(Path.GetFileName)
                .Should().Equal(
                    "V001__SYS_ONE.sql",
                    "V002__MDM_TWO.sql",
                    "V010__EMS_TEN.sql");
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public void Sqlite_migration_catalog_rejects_invalid_width_and_duplicate_numeric_versions()
    {
        var invalidDirectory = Directory.CreateTempSubdirectory("nexa-sqlite-invalid-migrations-").FullName;
        var duplicateDirectory = Directory.CreateTempSubdirectory("nexa-sqlite-duplicate-migrations-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(invalidDirectory, "V1__SYS_INVALID.sql"), "-- invalid");
            Action invalid = () => SqliteSchemaInitializer.GetOrderedMigrationFiles(invalidDirectory);
            invalid.Should().Throw<InvalidDataException>()
                .WithMessage("*V1__SYS_INVALID.sql*V###__UPPER_SNAKE_DESCRIPTION.sql*");

            File.WriteAllText(Path.Combine(duplicateDirectory, "V001__SYS_ONE.sql"), "-- one");
            File.WriteAllText(Path.Combine(duplicateDirectory, "V001__MDM_OTHER.sql"), "-- duplicate");
            Action duplicate = () => SqliteSchemaInitializer.GetOrderedMigrationFiles(duplicateDirectory);
            duplicate.Should().Throw<InvalidDataException>()
                .WithMessage("*Duplicate migration version 1*V001__*");
        }
        finally
        {
            try { Directory.Delete(invalidDirectory, recursive: true); } catch { /* best-effort cleanup */ }
            try { Directory.Delete(duplicateDirectory, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Sqlite_fresh_and_incremental_paths_validate_the_same_catalog_before_opening_the_database()
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "02.Backend", "NexaOne.Common", "Infrastructure", "Persistence",
            "SqliteSchemaInitializer.cs"));
        var applyStart = source.IndexOf("public static void Apply", StringComparison.Ordinal);
        var ensureStart = source.IndexOf("public static void EnsureSchema", StringComparison.Ordinal);
        var incrementalStart = source.IndexOf("public static void CreateMissingTables", StringComparison.Ordinal);
        var ensureBody = source[ensureStart..applyStart];
        var applyBody = source[applyStart..incrementalStart];
        var incrementalBody = source[incrementalStart..source.IndexOf(
            "private static void EnsureEmsToolMountPositionGuard", incrementalStart, StringComparison.Ordinal)];

        foreach (var body in new[] { applyBody, incrementalBody })
        {
            var validation = body.IndexOf("GetOrderedMigrationFiles(dir)", StringComparison.Ordinal);
            var open = body.IndexOf("conn.Open()", StringComparison.Ordinal);
            validation.Should().BeGreaterThanOrEqualTo(0);
            open.Should().BeGreaterThan(validation,
                "fresh and incremental schema paths must reject a malformed bundle before DB access");
            body.Should().Contain("foreach (var file in migrationFiles)");
        }

        var ensureValidation = ensureBody.IndexOf(
            "GetOrderedMigrationFiles(FindMigrationsDir())", StringComparison.Ordinal);
        var firstDatabaseAccess = ensureBody.IndexOf("HasUserTables(connectionString)", StringComparison.Ordinal);
        ensureValidation.Should().BeGreaterThanOrEqualTo(0);
        firstDatabaseAccess.Should().BeGreaterThan(ensureValidation,
            "EnsureSchema must validate before its first SQLite connection is opened");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static void AssertAppendOnly(
        SqliteConnection connection,
        string table,
        string predicate,
        string updateAssignment,
        string replaceSql)
    {
        Action update = () => Execute(connection, $"UPDATE {table} SET {updateAssignment} WHERE {predicate};");
        update.Should().Throw<SqliteException>().WithMessage("*append-only*");

        Action delete = () => Execute(connection, $"DELETE FROM {table} WHERE {predicate};");
        delete.Should().Throw<SqliteException>().WithMessage("*append-only*");

        Action replace = () => Execute(connection, replaceSql);
        replace.Should().Throw<SqliteException>().WithMessage("*replacement is forbidden*");

        Scalar(connection, $"SELECT COUNT(*) FROM {table} WHERE {predicate};").Should().Be("1");
    }
}
