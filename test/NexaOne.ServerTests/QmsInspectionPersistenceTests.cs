using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Domain;
using NexaOne.QMS.Infrastructure;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class QmsInspectionPersistenceTests : IClassFixture<QmsInspectionPersistenceTests.QmsFactory>
{
    private readonly QmsFactory _factory;
    public QmsInspectionPersistenceTests(QmsFactory factory) => _factory = factory;

    public sealed class QmsFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-qms-persistence-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "qms-persistence-jwt-secret-key-at-least-32bytes");
            builder.UseSetting("Jwt:Issuer", "qms-persistence-test");
            builder.UseSetting("Jwt:Audience", "qms-persistence-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    private InspectionResultRepository Repo()
    {
        _ = _factory.CreateClient();
        var ds = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString
        };
        return new InspectionResultRepository(ds);
    }

    private AiInspectionRepository AiRepo()
    {
        _ = _factory.CreateClient();
        var ds = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString
        };
        return new AiInspectionRepository(ds);
    }

    private long Count(string table, string column, string id)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long ScalarLong(string sql)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private string? ReadHeaderValue(string inspectionId, string column)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM QMS_INSPECTION WHERE INSPECTION_ID = @inspectionId";
        cmd.Parameters.AddWithValue("@inspectionId", inspectionId);
        return cmd.ExecuteScalar() as string;
    }

    private void Exec(string sql)
    {
        _ = Repo();
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void SeedReferences(string lotId)
    {
        Exec($@"INSERT OR IGNORE INTO MDM_EQUIPMENT
            (EQUIPMENT_ID, EQUIPMENT_NAME, PLANT_ID, AREA_ID, EQUIPMENT_TYPE, EQUIPMENT_CLASS_ID,
             VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('EQ-QMS', 'QMS equipment', 'PLANT01', 'AREA01', 'Inspection', 'QMS',
             'Active', 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT OR IGNORE INTO QMS_INSPECTION_SPEC
            (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, NOMINAL_VALUE,
             TOLERANCE_PLUS, TOLERANCE_MINUS, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('SPEC-QMS', 'Length', 'PROC01', 'Length', 'Variable', 10, .5, .5, 1,
             'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT OR IGNORE INTO QMS_INSPECTION_SPEC
            (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, NOMINAL_VALUE,
             TOLERANCE_PLUS, TOLERANCE_MINUS, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('SPEC-QMS-ATTR', 'Appearance', 'PROC01', 'Appearance', 'Attribute', NULL, NULL, NULL, 1,
             'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT OR IGNORE INTO POM_LOT
            (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
             ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
            VALUES ('{lotId}', 'PLANT01', 'ITEM01', 1, 0, 'Created', 'Idle', 'PROC01', 0, 'N',
             'TEST', CURRENT_TIMESTAMP);");
    }

    [Fact]
    public void Development_qms_seed_uses_type_appropriate_lot_sources()
    {
        _ = Repo();

        ScalarLong("""
            SELECT COUNT(*) FROM QMS_INSPECTION
            WHERE INSPECTION_ID IN ('INS_IN1', 'INS_PR1', 'INS_SH1')
            """).Should().Be(3);
        ScalarLong("""
            SELECT COUNT(*)
            FROM QMS_INSPECTION I
            WHERE I.INSPECTION_ID IN ('INS_IN1', 'INS_PR1', 'INS_SH1')
              AND NOT (
                  (I.INSPECTION_TYPE = 'Incoming' AND EXISTS (
                      SELECT 1 FROM IVT_MATERIAL_LOT L WHERE L.LOT_ID = I.LOT_ID))
                  OR (I.INSPECTION_TYPE IN ('Process', 'Shipping') AND EXISTS (
                      SELECT 1 FROM POM_LOT L WHERE L.LOT_ID = I.LOT_ID))
              )
            """).Should().Be(0);
    }

    [Fact]
    public async Task Recording_result_atomically_creates_linked_header_and_item()
    {
        var id = $"IR-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var result = InspectionResult.Create(id, "SPEC-QMS", lotId, "EQ-QMS", DateTime.UtcNow,
            "admin", 10m, null, null, 10m, .5m, .5m, "Variable", "automatic verdict").Value;

        await Repo().AddAsync(result);

        Count("QMS_INSPECTION", "INSPECTION_ID", id).Should().Be(1);
        Count("QMS_INSPECTION_RESULT", "INSPECTION_ID", id).Should().Be(1);
        var loaded = await Repo().GetByLotAsync(lotId);
        loaded.Should().ContainSingle(x => x.Id == id && x.IsPass);
    }

    [Theory]
    [InlineData("POM", "ITEM-INCOMING")]
    [InlineData("IVT", "MATERIAL-INCOMING")]
    public async Task Incoming_result_persists_type_and_derives_product_from_lot_source(
        string lotSource, string expectedProductId)
    {
        var id = $"IR-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        if (lotSource == "POM")
        {
            SeedReferences(lotId);
            Exec($"UPDATE POM_LOT SET PRODUCT_ID = '{expectedProductId}' WHERE LOT_ID = '{lotId}';");
        }
        else
        {
            SeedReferences($"REFERENCE-{Guid.NewGuid():N}");
            Exec($@"INSERT INTO IVT_MATERIAL_LOT
                (LOT_ID, MATERIAL_ID, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('{lotId}', '{expectedProductId}', 'InStock', 'TEST', CURRENT_TIMESTAMP,
                        'TEST', CURRENT_TIMESTAMP);");
        }

        var result = InspectionResult.Create(id, "SPEC-QMS", lotId, "EQ-QMS", DateTime.UtcNow,
            "admin", 10m, null, null, 10m, .5m, .5m, "Variable", "incoming inspection",
            InspectionExecutionType.Incoming).Value;

        await Repo().AddAsync(result);

        ReadHeaderValue(id, "INSPECTION_TYPE").Should().Be("Incoming");
        ReadHeaderValue(id, "PRODUCT_ID").Should().Be(expectedProductId);
        var loaded = await Repo().GetByLotAsync(lotId);
        loaded.Should().ContainSingle(x => x.Id == id && x.InspectionType == InspectionExecutionType.Incoming);
    }

    [Fact]
    public async Task Item_failure_rolls_back_header_insert()
    {
        var id = $"IR-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var repo = Repo();
        Exec($@"CREATE TRIGGER TR_TEST_QMS_RESULT_FAIL BEFORE INSERT ON QMS_INSPECTION_RESULT
            WHEN NEW.RESULT_ID = '{id}' BEGIN SELECT RAISE(ABORT, 'forced item failure'); END;");
        var result = InspectionResult.Create(id, "SPEC-QMS", lotId, "EQ-QMS", DateTime.UtcNow,
            "admin", 10m, null, null, 10m, .5m, .5m, "Variable").Value;

        var action = () => repo.AddAsync(result);
        await action.Should().ThrowAsync<Exception>();

        Count("QMS_INSPECTION", "INSPECTION_ID", id).Should().Be(0);
        Count("QMS_INSPECTION_RESULT", "RESULT_ID", id).Should().Be(0);
        Exec("DROP TRIGGER TR_TEST_QMS_RESULT_FAIL;");
    }

    [Fact]
    public async Task V2_fresh_sqlite_persists_one_header_multiple_items_and_append_only_history()
    {
        var inspectionId = $"QMSI-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var execution = V2Execution(inspectionId, lotId, $"KEY-{Guid.NewGuid():N}");
        var confirmed = V2Confirmation(execution);

        await Repo().AddExecutionAsync(execution, confirmed, null);

        Count("QMS_INSPECTION", "INSPECTION_ID", inspectionId).Should().Be(1);
        Count("QMS_INSPECTION_RESULT", "INSPECTION_ID", inspectionId).Should().Be(2);
        Count("QMS_INSPECTION_EVENT", "INSPECTION_ID", inspectionId).Should().Be(1);
        var loaded = await Repo().GetExecutionAsync(inspectionId);
        loaded.Should().NotBeNull();
        loaded!.Items.Should().HaveCount(2);
        loaded.Items.Select(x => x.SpecId).Should().BeEquivalentTo("SPEC-QMS", "SPEC-QMS-ATTR");
        loaded.History.Should().ContainSingle(x => x.EventType == InspectionExecutionEventType.Confirmed);

        var updateHeader = () => Exec($"UPDATE QMS_INSPECTION SET REMARK='tamper' WHERE INSPECTION_ID='{inspectionId}';");
        var deleteItem = () => Exec($"DELETE FROM QMS_INSPECTION_RESULT WHERE INSPECTION_ID='{inspectionId}';");
        var updateHistory = () => Exec($"UPDATE QMS_INSPECTION_EVENT SET REASON='tamper' WHERE INSPECTION_ID='{inspectionId}';");
        updateHeader.Should().Throw<SqliteException>().WithMessage("*immutable*");
        deleteItem.Should().Throw<SqliteException>().WithMessage("*immutable*");
        updateHistory.Should().Throw<SqliteException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task V2_second_item_failure_rolls_back_header_first_item_and_history()
    {
        var inspectionId = $"QMSI-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var execution = V2Execution(inspectionId, lotId, $"KEY-{Guid.NewGuid():N}");
        var failedResultId = execution.Items[1].Id;
        Exec($@"CREATE TRIGGER TR_TEST_QMS_V2_RESULT_FAIL BEFORE INSERT ON QMS_INSPECTION_RESULT
            WHEN NEW.RESULT_ID = '{failedResultId}' BEGIN SELECT RAISE(ABORT, 'forced v2 item failure'); END;");

        var action = () => Repo().AddExecutionAsync(execution, V2Confirmation(execution), null);
        await action.Should().ThrowAsync<Exception>();

        Count("QMS_INSPECTION", "INSPECTION_ID", inspectionId).Should().Be(0);
        Count("QMS_INSPECTION_RESULT", "INSPECTION_ID", inspectionId).Should().Be(0);
        Count("QMS_INSPECTION_EVENT", "INSPECTION_ID", inspectionId).Should().Be(0);
        Exec("DROP TRIGGER TR_TEST_QMS_V2_RESULT_FAIL;");
    }

    [Fact]
    public async Task V2_sqlite_triggers_reject_broken_parent_lineage_and_unknown_event_actor()
    {
        var lotId = $"LOT-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var broken = () => Exec($@"INSERT INTO QMS_INSPECTION
            (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, INSPECTED_AT, INSPECTOR_ID,
             RESULT, LOT_QTY, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
             IDEMPOTENCY_KEY, REQUEST_HASH, RELATION_TYPE, PARENT_INSPECTION_ID, ROOT_INSPECTION_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('QMSI-BROKEN', 'Process', '{lotId}', 'EQ-QMS', CURRENT_TIMESTAMP, 'admin',
             'Pass', 10, 10, 0, 1, 'KEY-BROKEN', '{new string('a', 64)}',
             'Correction', 'QMSI-MISSING', 'QMSI-MISSING', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);");
        broken.Should().Throw<SqliteException>().WithMessage("*lineage*");

        var execution = V2Execution($"QMSI-{Guid.NewGuid():N}", lotId, $"KEY-{Guid.NewGuid():N}");
        await Repo().AddExecutionAsync(execution, V2Confirmation(execution), null);
        var badActor = () => Exec($@"INSERT INTO QMS_INSPECTION_EVENT
            (EVENT_ID, INSPECTION_ID, EVENT_TYPE, ROOT_INSPECTION_ID, IDEMPOTENCY_KEY,
             REQUEST_HASH, ACTOR_ID, REASON, OCCURRED_AT, CREATED_BY, CREATED_AT)
            VALUES ('QMSE-BAD-ACTOR', '{execution.InspectionId}', 'Cancelled', '{execution.RootInspectionId}',
             'KEY-BAD-ACTOR', '{new string('b', 64)}', 'USER-MISSING', 'bad actor', CURRENT_TIMESTAMP,
             'USER-MISSING', CURRENT_TIMESTAMP);");
        badActor.Should().Throw<SqliteException>().WithMessage("*actor*");
    }

    [Fact]
    public async Task V2_confirmed_execution_rejects_late_item_insert_and_second_cancellation()
    {
        var inspectionId = $"QMSI-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var execution = V2Execution(inspectionId, lotId, $"KEY-{Guid.NewGuid():N}");
        await Repo().AddExecutionAsync(execution, V2Confirmation(execution), null);

        var lateItem = () => Exec($@"INSERT INTO QMS_INSPECTION_RESULT
            (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, INSPECTED_AT,
             INSPECTOR_ID, IS_PASS, ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('QMSR-LATE-{Guid.NewGuid():N}', '{inspectionId}', 'SPEC-QMS', '{lotId}',
             'EQ-QMS', CURRENT_TIMESTAMP, 'admin', 1, 3, 10, 0,
             'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);");
        lateItem.Should().Throw<SqliteException>().WithMessage("*cannot accept additional result rows*");

        var first = InspectionExecutionHistory.Create(
            $"QMSE-{Guid.NewGuid():N}", inspectionId, InspectionExecutionEventType.Cancelled,
            $"CANCEL-{Guid.NewGuid():N}", new string('b', 64), "admin", DateTime.UtcNow,
            inspectionId, null, reason: "first cancellation").Value;
        await Repo().AppendHistoryAsync(first);
        var second = InspectionExecutionHistory.Create(
            $"QMSE-{Guid.NewGuid():N}", inspectionId, InspectionExecutionEventType.Cancelled,
            $"CANCEL-{Guid.NewGuid():N}", new string('c', 64), "admin", DateTime.UtcNow,
            inspectionId, null, reason: "concurrent cancellation").Value;

        var secondCancellation = () => Repo().AppendHistoryAsync(second);
        await secondCancellation.Should().ThrowAsync<Exception>();
        ScalarLong($"SELECT COUNT(*) FROM QMS_INSPECTION_EVENT WHERE INSPECTION_ID='{inspectionId}' AND EVENT_TYPE='Cancelled'")
            .Should().Be(1);
    }

    [Fact]
    public void V2_sqlite_direct_result_writes_enforce_the_same_aggregate_contract()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var inspectionId = $"QMSI-DIRECT-{suffix}";
        var emptyInspectionId = $"QMSI-EMPTY-{suffix}";
        var legacyInspectionId = $"INSP-LEGACY-{suffix}";
        var lotId = $"LOT-DIRECT-{suffix}";
        var otherLotId = $"LOT-OTHER-{suffix}";
        var hash = new string('a', 64);
        SeedReferences(lotId);
        SeedReferences(otherLotId);
        Exec($"""
            INSERT INTO MDM_EQUIPMENT
              (EQUIPMENT_ID, EQUIPMENT_NAME, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
               EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('EQ-QMS-OFF-{suffix}', 'Inactive QMS equipment', 'PLANT01', 'AREA01',
                    'Inspection', 'QMS', 'Inactive', 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO QMS_INSPECTION_SPEC
              (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, NOMINAL_VALUE,
               TOLERANCE_PLUS, TOLERANCE_MINUS, IS_ACTIVE,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('SPEC-QMS-OFF-{suffix}', 'Inactive length', 'PROC01', 'Length', 'Variable',
                    10, .5, .5, 0, 'TEST', CURRENT_TIMESTAMP, 'TEST', CURRENT_TIMESTAMP);
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, INSPECTED_AT,
               INSPECTOR_ID, RESULT, LOT_QTY, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
               IDEMPOTENCY_KEY, REQUEST_HASH, RELATION_TYPE, ROOT_INSPECTION_ID,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('{inspectionId}', 'Process', '{lotId}', 'EQ-QMS', CURRENT_TIMESTAMP,
                    'admin', 'Fail', 10, 10, 2, 1, 'DIRECT-{suffix}', '{hash}',
                    'Original', '{inspectionId}', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, SPEC_ID, INSPECTED_AT,
               INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('{legacyInspectionId}', 'Process', '{lotId}', 'EQ-QMS', 'SPEC-QMS',
                    CURRENT_TIMESTAMP, 'admin', 'Pass', 1, 0, 1,
                    'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);

        Action InsertItem(
            string resultId,
            string parentId = null!,
            string specId = "SPEC-QMS",
            string? itemLotId = null,
            string equipmentId = "EQ-QMS",
            int? sequence = 1,
            int sampleQuantity = 10,
            int defectQuantity = 0,
            decimal? measuredValue = 10m,
            string? attributeResult = null,
            bool isPass = true)
        {
            var inspection = parentId ?? inspectionId;
            var lot = itemLotId ?? lotId;
            var sequenceSql = sequence?.ToString() ?? "NULL";
            var measuredSql = measuredValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            var attributeSql = attributeResult is null ? "NULL" : $"'{attributeResult}'";
            return () => Exec($"""
                INSERT INTO QMS_INSPECTION_RESULT
                  (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID,
                   MEASURED_VALUE, ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID, IS_PASS,
                   ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY,
                   CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('{resultId}', '{inspection}', '{specId}', '{lot}', '{equipmentId}',
                        {measuredSql}, {attributeSql}, CURRENT_TIMESTAMP, 'admin', {(isPass ? 1 : 0)},
                        {sequenceSql}, {sampleQuantity}, {defectQuantity},
                        'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
                """);
        }

        InsertItem($"QMSR-ORPHAN-{suffix}", $"QMSI-MISSING-{suffix}")
            .Should().Throw<SqliteException>();
        InsertItem($"QMSR-LEGACY-{suffix}", legacyInspectionId)
            .Should().Throw<SqliteException>().WithMessage("*matching v2 header*");
        InsertItem($"QMSR-NO-SEQ-{suffix}", sequence: null)
            .Should().Throw<SqliteException>().WithMessage("*matching v2 header*");
        InsertItem($"QMSR-WRONG-LOT-{suffix}", itemLotId: otherLotId)
            .Should().Throw<SqliteException>().WithMessage("*matching header*");
        InsertItem($"QMSR-INACTIVE-EQ-{suffix}", equipmentId: $"EQ-QMS-OFF-{suffix}")
            .Should().Throw<SqliteException>();
        InsertItem($"QMSR-INACTIVE-SPEC-{suffix}", specId: $"SPEC-QMS-OFF-{suffix}")
            .Should().Throw<SqliteException>();
        InsertItem($"QMSR-SAMPLE-OVER-{suffix}", sampleQuantity: 11)
            .Should().Throw<SqliteException>().WithMessage("*cannot exceed header quantities*");
        InsertItem($"QMSR-DEFECT-OVER-{suffix}", defectQuantity: 3)
            .Should().Throw<SqliteException>().WithMessage("*cannot exceed header quantities*");
        InsertItem($"QMSR-VARIABLE-VERDICT-{suffix}", measuredValue: 20m, isPass: true)
            .Should().Throw<SqliteException>().WithMessage("*specification type*");
        InsertItem($"QMSR-ATTRIBUTE-SHAPE-{suffix}", specId: "SPEC-QMS-ATTR",
                measuredValue: 1m, attributeResult: "Pass")
            .Should().Throw<SqliteException>().WithMessage("*specification type*");
        InsertItem($"QMSR-ATTRIBUTE-NULL-{suffix}", specId: "SPEC-QMS-ATTR",
                measuredValue: null, attributeResult: null)
            .Should().Throw<SqliteException>().WithMessage("*specification type*");

        InsertItem($"QMSR-VALID-VAR-{suffix}", defectQuantity: 2)();
        InsertItem($"QMSR-DUPLICATE-{suffix}", sequence: 2)
            .Should().Throw<SqliteException>();
        InsertItem($"QMSR-VALID-ATTR-{suffix}", specId: "SPEC-QMS-ATTR", sequence: 2,
            defectQuantity: 2, measuredValue: null, attributeResult: "Pass")();

        // Each item may carry the aggregate sample quantity. The contract compares each
        // item to the header; it deliberately does not sum per-specification quantities.
        ScalarLong($"SELECT SUM(SAMPLE_QTY) FROM QMS_INSPECTION_RESULT WHERE INSPECTION_ID='{inspectionId}'")
            .Should().Be(20);

        Exec($"""
            INSERT INTO QMS_INSPECTION_EVENT
              (EVENT_ID, INSPECTION_ID, EVENT_TYPE, ROOT_INSPECTION_ID, IDEMPOTENCY_KEY,
               REQUEST_HASH, ACTOR_ID, OCCURRED_AT, CREATED_BY, CREATED_AT)
            VALUES ('QMSE-DIRECT-{suffix}', '{inspectionId}', 'Confirmed', '{inspectionId}',
                    'DIRECT-{suffix}', '{hash}', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, INSPECTED_AT,
               INSPECTOR_ID, RESULT, LOT_QTY, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
               IDEMPOTENCY_KEY, REQUEST_HASH, RELATION_TYPE, ROOT_INSPECTION_ID,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('{emptyInspectionId}', 'Process', '{lotId}', 'EQ-QMS', CURRENT_TIMESTAMP,
                    'admin', 'Pass', 10, 10, 0, 1, 'EMPTY-{suffix}', '{hash}',
                    'Original', '{emptyInspectionId}', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
        var confirmEmpty = () => Exec($"""
            INSERT INTO QMS_INSPECTION_EVENT
              (EVENT_ID, INSPECTION_ID, EVENT_TYPE, ROOT_INSPECTION_ID, IDEMPOTENCY_KEY,
               REQUEST_HASH, ACTOR_ID, OCCURRED_AT, CREATED_BY, CREATED_AT)
            VALUES ('QMSE-EMPTY-{suffix}', '{emptyInspectionId}', 'Confirmed', '{emptyInspectionId}',
                    'EMPTY-{suffix}', '{hash}', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
        confirmEmpty.Should().Throw<SqliteException>().WithMessage("*at least one result item*");
    }

    [Fact]
    public void SQLite_v2_integrity_objects_remain_single_and_effective_after_reinitialization()
    {
        _ = Repo();
        SqliteSchemaInitializer.EnsureSchema(_factory.ConnString);
        SqliteSchemaInitializer.EnsureSchema(_factory.ConnString);

        foreach (var name in new[]
                 {
                     "TR_QMS_RESULT_INTEGRITY_BI",
                     "TR_QMS_RESULT_INTEGRITY_BU",
                     "TR_QMS_V2_EVENT_BI",
                     "UX_QMS_INSPECTION_RESULT_SPEC"
                 })
            ScalarLong($"SELECT COUNT(*) FROM sqlite_master WHERE name='{name}'").Should().Be(1);
    }

    [Fact]
    public async Task Effective_lot_status_uses_valid_leaf_and_returns_pending_when_lineage_is_cancelled()
    {
        var rootId = $"QMSI-{Guid.NewGuid():N}";
        var childId = $"QMSI-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var root = V2Execution(rootId, lotId, $"KEY-{Guid.NewGuid():N}");
        await Repo().AddExecutionAsync(root, V2Confirmation(root), null);

        var initial = await Repo().GetEffectiveLotStatusAsync(lotId);
        initial.HasResults.Should().BeTrue();
        initial.AllPassed.Should().BeTrue();
        initial.ResultCount.Should().Be(2);

        var child = V2ChildExecution(childId, root, $"KEY-{Guid.NewGuid():N}");
        var relation = InspectionExecutionHistory.Create(
            $"QMSE-{Guid.NewGuid():N}", root.InspectionId,
            InspectionExecutionEventType.Corrected, child.IdempotencyKey, child.RequestHash,
            "admin", child.InspectedAt, root.RootInspectionId, root.InspectionId,
            child.InspectionId, "corrected values").Value;
        await Repo().AddExecutionAsync(child, V2Confirmation(child), relation);

        var corrected = await Repo().GetEffectiveLotStatusAsync(lotId);
        corrected.HasResults.Should().BeTrue();
        corrected.LastInspectedAt.Should().Be(child.InspectedAt);

        var cancellation = InspectionExecutionHistory.Create(
            $"QMSE-{Guid.NewGuid():N}", child.InspectionId,
            InspectionExecutionEventType.Cancelled, $"CANCEL-{Guid.NewGuid():N}",
            new string('d', 64), "admin", DateTime.UtcNow, child.RootInspectionId,
            child.ParentInspectionId, reason: "invalid correction").Value;
        await Repo().AppendHistoryAsync(cancellation);

        var pending = await Repo().GetEffectiveLotStatusAsync(lotId);
        pending.HasResults.Should().BeFalse();
        pending.AllPassed.Should().BeFalse();
        pending.ResultCount.Should().Be(0);
        pending.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task SQLite_AI_evidence_rejects_orphans_and_is_append_only_with_foreign_keys_off()
    {
        var inspectionId = $"QMSI-{Guid.NewGuid():N}";
        var lotId = $"LOT-{Guid.NewGuid():N}";
        var modelId = $"MV-{Guid.NewGuid():N}";
        var inferenceId = $"AI-{Guid.NewGuid():N}";
        var reviewId = $"AIR-{Guid.NewGuid():N}";
        SeedReferences(lotId);
        var execution = V2Execution(inspectionId, lotId, $"KEY-{Guid.NewGuid():N}");
        await Repo().AddExecutionAsync(execution, V2Confirmation(execution), null);
        (await AiRepo().InspectionExistsAsync(inspectionId)).Should().BeTrue();
        (await AiRepo().InspectionExistsAsync("QMSI-MISSING")).Should().BeFalse();
        Exec($@"INSERT INTO QMS_AI_MODEL_VERSION
            (MODEL_VERSION_ID, MODEL_ID, VERSION_NO, ARTIFACT_URI, ARTIFACT_SHA256,
             CONFIDENCE_THRESHOLD, EFFECTIVE_FROM, CREATED_BY, CREATED_AT)
            VALUES ('{modelId}', 'MODEL-{Guid.NewGuid():N}', 1, 'https://models.local/model.onnx',
             '{new string('a', 64)}', .9, CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);");

        var orphan = () => Exec($@"INSERT INTO QMS_AI_INFERENCE
            (INFERENCE_ID, IDEMPOTENCY_KEY, MODEL_VERSION_ID, INSPECTION_ID,
             IMAGE_URI, IMAGE_SHA256, RAW_VERDICT, CONFIDENCE, THRESHOLD,
             INFERRED_AT, REQUEST_HASH, CREATED_BY, CREATED_AT)
            VALUES ('AI-ORPHAN-{Guid.NewGuid():N}', 'AIK-{Guid.NewGuid():N}', '{modelId}',
             'QMSI-MISSING', 'https://images.local/missing.png', '{new string('b', 64)}',
             'Pass', .95, .9, CURRENT_TIMESTAMP, '{new string('c', 64)}', 'admin', CURRENT_TIMESTAMP);");
        orphan.Should().Throw<SqliteException>().WithMessage("*inspection does not exist*");

        var futureModelId = $"MV-FUTURE-{Guid.NewGuid():N}";
        Exec($@"INSERT INTO QMS_AI_MODEL_VERSION
            (MODEL_VERSION_ID, MODEL_ID, VERSION_NO, ARTIFACT_URI, ARTIFACT_SHA256,
             CONFIDENCE_THRESHOLD, EFFECTIVE_FROM, CREATED_BY, CREATED_AT)
            VALUES ('{futureModelId}', 'MODEL-FUTURE-{Guid.NewGuid():N}', 1,
             'https://models.local/future.onnx', '{new string('d', 64)}', .9,
             datetime('now', '+1 hour'), 'admin', CURRENT_TIMESTAMP);");
        var futureModelInference = () => Exec($@"INSERT INTO QMS_AI_INFERENCE
            (INFERENCE_ID, IDEMPOTENCY_KEY, MODEL_VERSION_ID, INSPECTION_ID,
             IMAGE_URI, IMAGE_SHA256, RAW_VERDICT, CONFIDENCE, THRESHOLD,
             INFERRED_AT, REQUEST_HASH, CREATED_BY, CREATED_AT)
            VALUES ('AI-FUTURE-{Guid.NewGuid():N}', 'AIK-{Guid.NewGuid():N}', '{futureModelId}',
             '{inspectionId}', 'https://images.local/future.png', '{new string('e', 64)}',
             'Pass', .95, .9, CURRENT_TIMESTAMP, '{new string('f', 64)}', 'admin', CURRENT_TIMESTAMP);");
        futureModelInference.Should().Throw<SqliteException>().WithMessage("*not effective*");

        Exec($@"INSERT INTO QMS_AI_INFERENCE
            (INFERENCE_ID, IDEMPOTENCY_KEY, MODEL_VERSION_ID, INSPECTION_ID,
             IMAGE_URI, IMAGE_SHA256, RAW_VERDICT, CONFIDENCE, THRESHOLD,
             INFERRED_AT, REQUEST_HASH, CREATED_BY, CREATED_AT)
            VALUES ('{inferenceId}', 'AIK-{Guid.NewGuid():N}', '{modelId}', '{inspectionId}',
             'https://images.local/evidence.png', '{new string('b', 64)}', 'Pass', .95, .9,
             CURRENT_TIMESTAMP, '{new string('c', 64)}', 'admin', CURRENT_TIMESTAMP);
            INSERT INTO QMS_AI_REVIEW
            (REVIEW_ID, INFERENCE_ID, REVIEW_SEQUENCE, REVIEWER_ID, REVIEW_VERDICT,
             REASON, REVIEWED_AT, CREATED_BY, CREATED_AT)
            VALUES ('{reviewId}', '{inferenceId}', 1, 'admin', 'Pass', 'verified',
             CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);");

        foreach (var mutation in new Action[]
                 {
                     () => Exec($"UPDATE QMS_AI_MODEL_VERSION SET ARTIFACT_URI='https://tamper.local/m' WHERE MODEL_VERSION_ID='{modelId}';"),
                     () => Exec($"DELETE FROM QMS_AI_MODEL_VERSION WHERE MODEL_VERSION_ID='{modelId}';"),
                     () => Exec($"UPDATE QMS_AI_INFERENCE SET IMAGE_URI='https://tamper.local/i' WHERE INFERENCE_ID='{inferenceId}';"),
                     () => Exec($"DELETE FROM QMS_AI_INFERENCE WHERE INFERENCE_ID='{inferenceId}';"),
                     () => Exec($"UPDATE QMS_AI_REVIEW SET REASON='tamper' WHERE REVIEW_ID='{reviewId}';"),
                     () => Exec($"DELETE FROM QMS_AI_REVIEW WHERE REVIEW_ID='{reviewId}';")
                 })
            mutation.Should().Throw<SqliteException>().WithMessage("*append-only*");
    }

    private static InspectionExecution V2Execution(
        string inspectionId, string lotId, string idempotencyKey)
    {
        var inspectedAt = DateTime.UtcNow;
        var variable = InspectionResult.Create(
            $"QMSR-{Guid.NewGuid():N}", "SPEC-QMS", lotId, "EQ-QMS", inspectedAt,
            "admin", 10m, null, null, 10m, .5m, .5m, "Variable", null,
            InspectionExecutionType.Process, inspectionId, 10, 0).Value;
        var attribute = InspectionResult.Create(
            $"QMSR-{Guid.NewGuid():N}", "SPEC-QMS-ATTR", lotId, "EQ-QMS", inspectedAt,
            "admin", null, "Pass", null, null, null, null, "Attribute", null,
            InspectionExecutionType.Process, inspectionId, 10, 0).Value;
        return InspectionExecution.Create(
            inspectionId, InspectionExecutionType.Process,
            InspectionExecutionRelationType.Original, inspectionId, null,
            lotId, "EQ-QMS", 10, 10, 0, idempotencyKey, new string('a', 64),
            inspectedAt, "admin", [variable, attribute], null, true, "v2 multi item").Value;
    }

    private static InspectionExecution V2ChildExecution(
        string inspectionId, InspectionExecution parent, string idempotencyKey)
    {
        var inspectedAt = parent.InspectedAt.AddSeconds(1);
        var variable = InspectionResult.Create(
            $"QMSR-{Guid.NewGuid():N}", "SPEC-QMS", parent.LotId, parent.EquipmentId,
            inspectedAt, "admin", 10m, null, null, 10m, .5m, .5m, "Variable", null,
            parent.InspectionType, inspectionId, 10, 0).Value;
        var attribute = InspectionResult.Create(
            $"QMSR-{Guid.NewGuid():N}", "SPEC-QMS-ATTR", parent.LotId, parent.EquipmentId,
            inspectedAt, "admin", null, "Pass", null, null, null, null, "Attribute", null,
            parent.InspectionType, inspectionId, 10, 0).Value;
        return InspectionExecution.Create(
            inspectionId, parent.InspectionType, InspectionExecutionRelationType.Correction,
            parent.RootInspectionId, parent.InspectionId, parent.LotId, parent.EquipmentId,
            10, 10, 0, idempotencyKey, new string('e', 64), inspectedAt, "admin",
            [variable, attribute], null, true, "corrected inspection").Value;
    }

    private static InspectionExecutionHistory V2Confirmation(InspectionExecution execution)
        => InspectionExecutionHistory.Create(
            $"QMSE-{Guid.NewGuid():N}", execution.InspectionId,
            InspectionExecutionEventType.Confirmed, execution.IdempotencyKey,
            execution.RequestHash, execution.InspectorId, execution.InspectedAt,
            execution.RootInspectionId, execution.ParentInspectionId,
            reason: execution.Remark).Value;
}
