using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class QmsSqliteSchemaUpgradeTests
{
    [Fact]
    public void EnsureSchema_rebuilds_existing_qms_triggers_for_production_and_inventory_lots()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexa-qms-upgrade-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};Foreign Keys=False";
        try
        {
            SqliteSchemaInitializer.Apply(connectionString);
            ReplaceWithPomOnlyTriggers(connectionString);

            SqliteSchemaInitializer.EnsureSchema(connectionString);

            foreach (var trigger in new[]
                     {
                         "TR_QMS_INSPECTION_INTEGRITY_BI", "TR_QMS_INSPECTION_INTEGRITY_BU",
                         "TR_QMS_RESULT_INTEGRITY_BI", "TR_QMS_RESULT_INTEGRITY_BU",
                     })
            {
                Scalar(connectionString,
                        $"SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = '{trigger}'")
                    .Should().Contain("IVT_MATERIAL_LOT", $"{trigger} must be upgraded on the next startup");
            }

            SeedReferences(connectionString);
            InsertInspection(connectionString, "INSP-POM-UPGRADE", "LOT-POM-UPGRADE", "ITEM-POM");
            InsertInspection(connectionString, "INSP-IVT-UPGRADE", "LOT-IVT-UPGRADE", "ITEM-IVT");

            Scalar(connectionString, "SELECT COUNT(*) FROM QMS_INSPECTION_RESULT").Should().Be("2");
            var insertUnknownLot = () => Execute(connectionString, """
                INSERT INTO QMS_INSPECTION
                    (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, INSPECTED_AT, SAMPLE_QTY, DEFECT_QTY,
                     IS_CONFIRMED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('INSP-UNKNOWN-UPGRADE', 'Incoming', 'LOT-UNKNOWN', CURRENT_TIMESTAMP,
                        1, 0, 1, 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
                """);
            insertUnknownLot.Should().Throw<SqliteException>().WithMessage("*unknown lot*");
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* temporary database cleanup failure is non-fatal */ }
        }
    }

    private static void ReplaceWithPomOnlyTriggers(string connectionString) => Execute(connectionString, """
        DROP TRIGGER TR_QMS_INSPECTION_INTEGRITY_BI;
        DROP TRIGGER TR_QMS_INSPECTION_INTEGRITY_BU;
        DROP TRIGGER TR_QMS_RESULT_INTEGRITY_BI;
        DROP TRIGGER TR_QMS_RESULT_INTEGRITY_BU;
        CREATE TRIGGER TR_QMS_INSPECTION_INTEGRITY_BI BEFORE INSERT ON QMS_INSPECTION
        BEGIN
          SELECT RAISE(ABORT, 'QMS_INSPECTION references an unknown lot')
            WHERE NEW.LOT_ID IS NOT NULL
              AND EXISTS (SELECT 1 FROM POM_LOT)
              AND NOT EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID);
        END;
        CREATE TRIGGER TR_QMS_INSPECTION_INTEGRITY_BU BEFORE UPDATE ON QMS_INSPECTION
        BEGIN
          SELECT RAISE(ABORT, 'QMS_INSPECTION references an unknown lot')
            WHERE NEW.LOT_ID IS NOT NULL
              AND EXISTS (SELECT 1 FROM POM_LOT)
              AND NOT EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID);
        END;
        CREATE TRIGGER TR_QMS_RESULT_INTEGRITY_BI BEFORE INSERT ON QMS_INSPECTION_RESULT
        BEGIN
          SELECT RAISE(ABORT, 'QMS inspection result requires a matching lot')
            WHERE NOT EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID);
        END;
        CREATE TRIGGER TR_QMS_RESULT_INTEGRITY_BU BEFORE UPDATE ON QMS_INSPECTION_RESULT
        BEGIN
          SELECT RAISE(ABORT, 'QMS inspection result requires a matching lot')
            WHERE NOT EXISTS (SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = NEW.LOT_ID);
        END;
        """);

    private static void SeedReferences(string connectionString) => Execute(connectionString, """
        INSERT INTO MDM_EQUIPMENT
            (EQUIPMENT_ID, EQUIPMENT_NAME, PLANT_ID, AREA_ID, EQUIPMENT_TYPE, EQUIPMENT_CLASS_ID,
             VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES ('EQ-QMS-UPGRADE', 'QMS equipment', 'PLANT01', 'AREA01', 'Inspection', 'QMS',
                'Active', 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
        INSERT INTO QMS_INSPECTION_SPEC
            (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, NOMINAL_VALUE,
             TOLERANCE_PLUS, TOLERANCE_MINUS, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES ('SPEC-QMS-UPGRADE', 'Length', 'PROC01', 'Length', 'Variable', 10, .5, .5, 1,
                'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
        INSERT INTO POM_LOT
            (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
             ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
        VALUES ('LOT-POM-UPGRADE', 'PLANT01', 'ITEM-POM', 1, 0, 'Created', 'Idle',
                'PROC01', 0, 'N', 'TEST', CURRENT_TIMESTAMP);
        INSERT INTO IVT_MATERIAL_LOT
            (LOT_ID, MATERIAL_ID, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES ('LOT-IVT-UPGRADE', 'ITEM-IVT', 'InStock', 'TEST', CURRENT_TIMESTAMP,
                'TEST', CURRENT_TIMESTAMP);
        """);

    private static void InsertInspection(
        string connectionString, string inspectionId, string lotId, string productId) =>
        Execute(connectionString, $"""
            INSERT INTO QMS_INSPECTION
                (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, PRODUCT_ID, EQUIPMENT_ID, SPEC_ID,
                 INSPECTED_AT, INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('{inspectionId}', 'Incoming', '{lotId}', '{productId}',
                    'EQ-QMS-UPGRADE', 'SPEC-QMS-UPGRADE', CURRENT_TIMESTAMP, 'admin', 'Pass',
                    1, 0, 1, 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO QMS_INSPECTION_RESULT
                (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, MEASURED_VALUE,
                 ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID, IS_PASS, REMARK,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('RESULT-{inspectionId}', '{inspectionId}', 'SPEC-QMS-UPGRADE',
                    '{lotId}', 'EQ-QMS-UPGRADE', 10, NULL, CURRENT_TIMESTAMP, 'admin', 1,
                    NULL, 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            """);

    private static string Scalar(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
