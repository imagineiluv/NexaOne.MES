using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
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
}
