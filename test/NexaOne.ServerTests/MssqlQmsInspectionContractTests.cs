using FluentAssertions;
using Microsoft.Data.SqlClient;
using System.Globalization;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// Exercises the final V093/V097 QMS schema on SQL Server. The connection string is intentionally
/// environment-gated so the ordinary local/SQLite suite stays self-contained. The dedicated CI job
/// requires the variable before invoking this class, which prevents an accidental soft skip there.
/// </summary>
[Trait("Category", "MssqlContract")]
public sealed class MssqlQmsInspectionContractTests
{
    private static readonly string ValidRequestHash = new('a', 64);

    [Fact]
    public async Task Valid_v2_execution_accepts_multiple_result_items()
    {
        var scope = await OpenMigratedScopeAsync();
        if (scope is null)
            return;

        await using (scope)
        {
            var data = await SeedReferenceDataAsync(scope);
            var inspectionId = $"INSP-{data.Suffix}";

            await InsertHeaderAsync(scope, inspectionId, data, $"CREATE-{data.Suffix}");
            await InsertTwoResultsAsync(scope, inspectionId, data, secondItemIsValid: true);
            await InsertConfirmationAsync(scope, inspectionId, data.Suffix);

            var persistedContract = await ScalarAsync<int>(scope, """
                SELECT CASE WHEN
                    (SELECT COUNT(*) FROM QMS_INSPECTION_RESULT WHERE INSPECTION_ID = @inspectionId) = 2
                    AND
                    (SELECT COUNT(*) FROM QMS_INSPECTION_EVENT
                     WHERE INSPECTION_ID = @inspectionId AND EVENT_TYPE = 'Confirmed') = 1
                    THEN 1 ELSE 0 END;
                """, ("@inspectionId", inspectionId));

            persistedContract.Should().Be(1,
                "a confirmed V097 execution must retain both specification result items");
        }
    }

    [Fact]
    public async Task V093_quantity_check_rejects_defects_above_the_sample()
    {
        var scope = await OpenMigratedScopeAsync();
        if (scope is null)
            return;

        await using (scope)
        {
            var suffix = NewSuffix();
            var inspectionId = $"INSP-QTY-{suffix}";
            var quantityConstraintIsTrusted = await ScalarAsync<int>(scope, """
                SELECT COUNT(*)
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'QMS_INSPECTION')
                  AND name = N'CK_QMS_INSPECTION_QUANTITY'
                  AND is_disabled = 0
                  AND is_not_trusted = 0;
                """);
            quantityConstraintIsTrusted.Should().Be(1);

            var act = () => ExecuteAsync(scope, """
                INSERT INTO QMS_INSPECTION
                    (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, INSPECTED_AT,
                     INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
                     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT,
                     LOT_QTY, IDEMPOTENCY_KEY, REQUEST_HASH, RELATION_TYPE, ROOT_INSPECTION_ID)
                VALUES
                    (@inspectionId, 'Process', @lotId, @equipmentId, SYSUTCDATETIME(),
                     'admin', 'Fail', 2, 3, 1,
                     'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME(),
                     10, @idempotencyKey, @requestHash, 'Original', @inspectionId);
                """,
                ("@inspectionId", inspectionId),
                ("@lotId", $"LOT-QTY-{suffix}"),
                ("@equipmentId", $"EQ-QTY-{suffix}"),
                ("@idempotencyKey", $"QTY-{suffix}"),
                ("@requestHash", ValidRequestHash));

            var exception = await act.Should().ThrowAsync<SqlException>();
            exception.Which.Number.Should().Be(547);
        }
    }

    [Fact]
    public async Task V093_result_foreign_key_rejects_a_missing_inspection_header()
    {
        var scope = await OpenMigratedScopeAsync();
        if (scope is null)
            return;

        await using (scope)
        {
            var data = await SeedReferenceDataAsync(scope);

            var act = () => ExecuteAsync(scope, """
                INSERT INTO QMS_INSPECTION_RESULT
                    (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID,
                     MEASURED_VALUE, INSPECTED_AT, INSPECTOR_ID, IS_PASS,
                     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT,
                     ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY)
                VALUES
                    (@resultId, @missingInspectionId, @specId, @lotId, @equipmentId,
                     10.0, SYSUTCDATETIME(), 'admin', 1,
                     'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME(),
                     1, 5, 0);
                """,
                ("@resultId", $"RESULT-FK-{data.Suffix}"),
                ("@missingInspectionId", $"MISSING-{data.Suffix}"),
                ("@specId", data.VariableSpecId),
                ("@lotId", data.LotId),
                ("@equipmentId", data.EquipmentId));

            var exception = await act.Should().ThrowAsync<SqlException>();
            exception.Which.Number.Should().Be(547);
            exception.Which.Message.Should().Contain("FK_QMS_RESULT_INSPECTION");
        }
    }

    [Fact]
    public async Task V097_set_based_trigger_rejects_a_batch_containing_one_bad_verdict()
    {
        var scope = await OpenMigratedScopeAsync();
        if (scope is null)
            return;

        await using (scope)
        {
            var data = await SeedReferenceDataAsync(scope);
            var inspectionId = $"INSP-BATCH-{data.Suffix}";
            await InsertHeaderAsync(scope, inspectionId, data, $"BATCH-{data.Suffix}");

            var act = () => InsertTwoResultsAsync(
                scope, inspectionId, data, secondItemIsValid: false);

            var exception = await act.Should().ThrowAsync<SqlException>();
            exception.Which.Number.Should().Be(51020);
        }
    }

    [Fact]
    public async Task V097_idempotency_index_rejects_a_second_execution_with_the_same_key()
    {
        var scope = await OpenMigratedScopeAsync();
        if (scope is null)
            return;

        await using (scope)
        {
            var data = await SeedReferenceDataAsync(scope);
            var idempotencyKey = $"DUPLICATE-{data.Suffix}";
            await InsertHeaderAsync(scope, $"INSP-A-{data.Suffix}", data, idempotencyKey);

            var act = () => InsertHeaderAsync(
                scope, $"INSP-B-{data.Suffix}", data, idempotencyKey);

            var exception = await act.Should().ThrowAsync<SqlException>();
            exception.Which.Number.Should().BeOneOf(2601, 2627);
            exception.Which.Message.Should().Contain("UX_QMS_INSPECTION_IDEMPOTENCY");
        }
    }

    private static async Task<DatabaseScope?> OpenMigratedScopeAsync()
    {
        var connectionString = MssqlContractDatabase.GetConnectionStringOrThrowIfRequired();
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        await MssqlContractDatabase.ValidateFullMigrationSetAsync(connectionString);
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            return new DatabaseScope(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<ReferenceData> SeedReferenceDataAsync(DatabaseScope scope)
    {
        var suffix = NewSuffix();
        var data = new ReferenceData(
            suffix,
            $"LOT-{suffix}",
            $"EQ-{suffix}",
            $"SPEC-V-{suffix}",
            $"SPEC-A-{suffix}");

        await ExecuteAsync(scope, """
            INSERT INTO IVT_MATERIAL_LOT
                (LOT_ID, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@lotId, 'InStock', 'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME());

            INSERT INTO MDM_EQUIPMENT
                (EQUIPMENT_ID, EQUIPMENT_NAME, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
                 EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@equipmentId, @equipmentId, 'PLANT-QMS-TEST', 'AREA-QMS-TEST', 'Inspection',
                 'CLASS-QMS-TEST', 'Active', 'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME());

            INSERT INTO QMS_INSPECTION_SPEC
                (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE,
                 NOMINAL_VALUE, TOLERANCE_PLUS, TOLERANCE_MINUS, IS_ACTIVE,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@variableSpecId, 'Variable contract spec', 'PROCESS-QMS-TEST', 'Thickness',
                 'Variable', 10.0, 1.0, 1.0, 1,
                 'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME()),
                (@attributeSpecId, 'Attribute contract spec', 'PROCESS-QMS-TEST', 'Appearance',
                 'Attribute', NULL, NULL, NULL, 1,
                 'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME());
            """,
            ("@lotId", data.LotId),
            ("@equipmentId", data.EquipmentId),
            ("@variableSpecId", data.VariableSpecId),
            ("@attributeSpecId", data.AttributeSpecId));

        return data;
    }

    private static Task InsertHeaderAsync(
        DatabaseScope scope,
        string inspectionId,
        ReferenceData data,
        string idempotencyKey)
        => ExecuteAsync(scope, """
            INSERT INTO QMS_INSPECTION
                (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, INSPECTED_AT,
                 INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT,
                 LOT_QTY, IDEMPOTENCY_KEY, REQUEST_HASH, RELATION_TYPE, ROOT_INSPECTION_ID)
            VALUES
                (@inspectionId, 'Process', @lotId, @equipmentId, SYSUTCDATETIME(),
                 'admin', 'Pass', 10, 0, 1,
                 'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME(),
                 100, @idempotencyKey, @requestHash, 'Original', @inspectionId);
            """,
            ("@inspectionId", inspectionId),
            ("@lotId", data.LotId),
            ("@equipmentId", data.EquipmentId),
            ("@idempotencyKey", idempotencyKey),
            ("@requestHash", ValidRequestHash));

    private static Task InsertTwoResultsAsync(
        DatabaseScope scope,
        string inspectionId,
        ReferenceData data,
        bool secondItemIsValid)
        => ExecuteAsync(scope, """
            INSERT INTO QMS_INSPECTION_RESULT
                (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID,
                 MEASURED_VALUE, ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID, IS_PASS,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT,
                 ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY)
            VALUES
                (@variableResultId, @inspectionId, @variableSpecId, @lotId, @equipmentId,
                 10.5, NULL, SYSUTCDATETIME(), 'admin', 1,
                 'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME(),
                 1, 5, 0),
                (@attributeResultId, @inspectionId, @attributeSpecId, @lotId, @equipmentId,
                 NULL, 'Pass', SYSUTCDATETIME(), 'admin', @attributeVerdict,
                 'admin', SYSUTCDATETIME(), 'admin', SYSUTCDATETIME(),
                 2, 5, 0);
            """,
            ("@variableResultId", $"RESULT-V-{data.Suffix}"),
            ("@attributeResultId", $"RESULT-A-{data.Suffix}"),
            ("@inspectionId", inspectionId),
            ("@variableSpecId", data.VariableSpecId),
            ("@attributeSpecId", data.AttributeSpecId),
            ("@lotId", data.LotId),
            ("@equipmentId", data.EquipmentId),
            ("@attributeVerdict", secondItemIsValid ? 1 : 0));

    private static Task InsertConfirmationAsync(
        DatabaseScope scope,
        string inspectionId,
        string suffix)
        => ExecuteAsync(scope, """
            INSERT INTO QMS_INSPECTION_EVENT
                (EVENT_ID, INSPECTION_ID, EVENT_TYPE, ROOT_INSPECTION_ID,
                 IDEMPOTENCY_KEY, REQUEST_HASH, ACTOR_ID, OCCURRED_AT,
                 CREATED_BY, CREATED_AT)
            VALUES
                (@eventId, @inspectionId, 'Confirmed', @inspectionId,
                 @idempotencyKey, @requestHash, 'admin', SYSUTCDATETIME(),
                 'admin', SYSUTCDATETIME());
            """,
            ("@eventId", $"EVENT-{suffix}"),
            ("@inspectionId", inspectionId),
            ("@idempotencyKey", $"CONFIRM-{suffix}"),
            ("@requestHash", ValidRequestHash));

    private static async Task ExecuteAsync(
        DatabaseScope scope,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = scope.Connection.CreateCommand();
        command.Transaction = scope.Transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        DatabaseScope scope,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = scope.Connection.CreateCommand();
        command.Transaction = scope.Transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            throw new InvalidOperationException("The MSSQL contract scalar query returned no value.");
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private static void AddParameters(
        SqlCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
    }

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..12];

    private sealed record ReferenceData(
        string Suffix,
        string LotId,
        string EquipmentId,
        string VariableSpecId,
        string AttributeSpecId);

    private sealed class DatabaseScope : IAsyncDisposable
    {
        public DatabaseScope(SqlConnection connection, SqlTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
        }

        public SqlConnection Connection { get; }
        public SqlTransaction Transaction { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // An expected SQL Server constraint failure may already have ended the transaction.
            }
            finally
            {
                await Transaction.DisposeAsync();
                await Connection.DisposeAsync();
            }
        }
    }
}
