using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MaterialLotSchemaUpgradeTests
{
    [Fact]
    public void Existing_v048_sqlite_database_gets_lifecycle_columns_without_losing_stock()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-ivt-v119-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False";
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IVT_MATERIAL_LOT (
                      LOT_ID TEXT NOT NULL PRIMARY KEY, MATERIAL_ID TEXT, LOT_NO TEXT,
                      WAREHOUSE TEXT, CURRENT_QTY NUMERIC, UNIT TEXT, STATUS TEXT NOT NULL DEFAULT 'InStock',
                      RECEIVED_AT TEXT, EXPIRY_AT TEXT, CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                      CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                      UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM', UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                    CREATE TABLE IVT_MATERIAL_TX (
                      TX_ID TEXT NOT NULL PRIMARY KEY, LOT_ID TEXT, MATERIAL_ID TEXT, TX_TYPE TEXT NOT NULL,
                      QTY NUMERIC, FROM_WAREHOUSE TEXT, TO_WAREHOUSE TEXT, TX_AT TEXT NOT NULL,
                      PROCESSED_BY TEXT, STATUS TEXT, REMARK TEXT, CREATED_BY TEXT NOT NULL DEFAULT 'SYSTEM',
                      CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                      UPDATED_BY TEXT NOT NULL DEFAULT 'SYSTEM', UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                    INSERT INTO IVT_MATERIAL_LOT
                      (LOT_ID, MATERIAL_ID, CURRENT_QTY, UNIT, STATUS)
                    VALUES ('LEGACY-LOT', 'MAT-01', 12.345678, 'kg', 'InStock');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteSchemaInitializer.EnsureSchema(connectionString);

            using var verified = new SqliteConnection(connectionString);
            verified.Open();
            using var version = verified.CreateCommand();
            version.CommandText = "SELECT VERSION_NO FROM IVT_MATERIAL_LOT WHERE LOT_ID='LEGACY-LOT'";
            Convert.ToInt32(version.ExecuteScalar()).Should().Be(1);
            using var balance = verified.CreateCommand();
            balance.CommandText = "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID='LEGACY-LOT'";
            Convert.ToDecimal(balance.ExecuteScalar()).Should().Be(12.345678m);
            using var columns = verified.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('IVT_MATERIAL_TX') WHERE name IN " +
                                  "('IDEMPOTENCY_KEY','REQUEST_HASH','EXPECTED_VERSION','RESULT_VERSION'," +
                                  "'SOURCE_SYSTEM','SOURCE_EVENT_ID','BALANCE_BEFORE','BALANCE_AFTER'," +
                                  "'BALANCE_DELTA','RESULT_STATUS')";
            Convert.ToInt64(columns.ExecuteScalar()).Should().Be(10);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
