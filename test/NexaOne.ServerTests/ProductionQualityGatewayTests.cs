using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Infrastructure;
using NexaOne.ServiceContracts.Qms;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// Exercises the POM quality-gate projection against a real SQLite schema. The tests intentionally
/// persist both legacy 1:1 inspections and immutable v2 multi-item inspection history.
/// </summary>
public sealed class ProductionQualityGatewayTests
    : IClassFixture<ProductionQualityGatewayTests.QualityGateFactory>
{
    private readonly QualityGateFactory _factory;

    public ProductionQualityGatewayTests(QualityGateFactory factory)
    {
        _factory = factory;
        _factory.EnsureStarted();
    }

    public sealed class QualityGateFactory : WebApplicationFactory<Program>
    {
        private bool _started;

        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-production-quality-gate-{Guid.NewGuid():N}.db");

        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "quality-gate-integration-secret-key-32bytes!!!!");
            builder.UseSetting("Jwt:Issuer", "quality-gate-test");
            builder.UseSetting("Jwt:Audience", "quality-gate-test");
        }

        /// <summary>Starts the host once so all migrations, SQLite guards, and development seed run.</summary>
        public void EnsureStarted()
        {
            if (_started) return;
            using var client = CreateClient();
            _started = true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    [Fact]
    public async Task V2_multi_item_all_pass_releases_gate()
    {
        var context = SeedProcess();
        InsertV2Inspection(
            context,
            Id("QMSI"),
            DateTime.UtcNow,
            [(context.SpecA, true), (context.SpecB, true)]);

        var result = await Gateway().EvaluateAsync(context.LotId, context.ProcessId, null);

        result.Status.Should().Be(ProductionQualityStatus.Passed);
        result.RequiredSpecCount.Should().Be(2);
        result.PassedSpecCount.Should().Be(2);
        result.AllowsCompletion.Should().BeTrue();
    }

    [Fact]
    public async Task V2_one_failed_item_blocks_only_that_specification()
    {
        var context = SeedProcess();
        InsertV2Inspection(
            context,
            Id("QMSI"),
            DateTime.UtcNow,
            [(context.SpecA, true), (context.SpecB, false)]);

        var result = await Gateway().EvaluateAsync(context.LotId, context.ProcessId, null);

        result.Status.Should().Be(ProductionQualityStatus.Failed);
        result.RequiredSpecCount.Should().Be(2);
        result.PassedSpecCount.Should().Be(1);
        result.BlockingSpecId.Should().Be(context.SpecB);
        result.AllowsCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task Latest_cancelled_execution_is_pending_without_reviving_older_pass()
    {
        var context = SeedProcess();
        var baseline = DateTime.UtcNow.AddMinutes(-2);
        InsertV2Inspection(
            context,
            Id("QMSI"),
            baseline,
            [(context.SpecA, true), (context.SpecB, true)]);

        var cancelled = Id("QMSI");
        InsertV2Inspection(
            context,
            cancelled,
            baseline.AddMinutes(1),
            [(context.SpecA, false), (context.SpecB, false)]);
        InsertCancellationEvent(cancelled, cancelled, baseline.AddMinutes(1).AddSeconds(1));

        var result = await Gateway().EvaluateAsync(context.LotId, context.ProcessId, null);

        result.Status.Should().Be(ProductionQualityStatus.Pending,
            "cancelled evidence must neither fail the lot nor reactivate an older pass");
        result.PassedSpecCount.Should().Be(0);
        result.BlockingSpecId.Should().Be(context.SpecA);
    }

    [Theory]
    [InlineData("Correction", "Corrected")]
    [InlineData("Reinspection", "Reinspected")]
    public async Task Partial_successor_keeps_superseded_items_pending(
        string relationType,
        string eventType)
    {
        var context = SeedProcess();
        var inspectedAt = DateTime.UtcNow.AddMinutes(-1);
        var parent = Id("QMSI");
        InsertV2Inspection(
            context,
            parent,
            inspectedAt,
            [(context.SpecA, true), (context.SpecB, false)]);

        var successor = Id("QMSI");
        InsertV2Inspection(
            context,
            successor,
            inspectedAt.AddSeconds(10),
            [(context.SpecA, true)],
            relationType,
            parent,
            parent);
        InsertSuccessorEvent(parent, successor, parent, eventType, inspectedAt.AddSeconds(11));

        var result = await Gateway().EvaluateAsync(context.LotId, context.ProcessId, null);

        result.Status.Should().Be(ProductionQualityStatus.Pending,
            "the superseded parent's failed item is audit history, not current failure evidence");
        result.PassedSpecCount.Should().Be(1);
        result.BlockingSpecId.Should().Be(context.SpecB);
    }

    [Fact]
    public async Task Legacy_one_header_per_result_remains_supported()
    {
        var context = SeedProcess();
        var inspectedAt = DateTime.UtcNow;
        InsertLegacyInspection(context, Id("QMSL"), context.SpecA, true, "Process", inspectedAt);
        InsertLegacyInspection(context, Id("QMSL"), context.SpecB, true, "Process", inspectedAt);

        var result = await Gateway().EvaluateAsync(context.LotId, context.ProcessId, null);

        result.Status.Should().Be(ProductionQualityStatus.Passed);
        result.RequiredSpecCount.Should().Be(2);
        result.PassedSpecCount.Should().Be(2);
    }

    [Fact]
    public async Task Newer_incoming_and_shipping_results_do_not_hide_process_results()
    {
        var context = SeedProcess();
        var processTime = DateTime.UtcNow.AddMinutes(-2);
        InsertLegacyInspection(context, Id("QMSP"), context.SpecA, true, "Process", processTime);
        InsertLegacyInspection(context, Id("QMSP"), context.SpecB, true, "Process", processTime);

        InsertLegacyInspection(
            context, Id("QMSN"), context.SpecA, false, "Incoming", processTime.AddMinutes(1));
        InsertLegacyInspection(
            context, Id("QMSN"), context.SpecB, false, "Shipping", processTime.AddMinutes(1));

        var result = await Gateway().EvaluateAsync(context.LotId, context.ProcessId, null);

        result.Status.Should().Be(ProductionQualityStatus.Passed,
            "only Process inspection evidence may participate in the production completion gate");
        result.PassedSpecCount.Should().Be(2);
    }

    /// <summary>Creates the gateway with the SQLite provider configured by the real test host.</summary>
    private ProductionQualityGateService Gateway() => new(
        new ProductionQualityGateEvidenceRepository(new EesDataSource
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnString
    }));

    /// <summary>Seeds a unique lot, equipment, process, and two active process specifications.</summary>
    private QualityContext SeedProcess()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var context = new QualityContext(
            $"QGL{suffix}",
            $"QGP{suffix}",
            $"QGE{suffix}",
            $"QGA{suffix}",
            $"QGB{suffix}");
        var now = Timestamp(DateTime.UtcNow);

        Exec("""
            INSERT INTO MDM_EQUIPMENT
              (EQUIPMENT_ID, EQUIPMENT_NAME, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
               EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@equipment, @equipment, 'PLANT01', 'AREA01', 'Inspection',
                    'QMS', 'Active', 'TEST', @now, 'TEST', @now);
            INSERT INTO POM_LOT
              (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
               ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
            VALUES (@lot, 'PLANT01', 'ITEM01', 10, 0, 'Created', 'Idle',
                    @process, 0, 'N', 'TEST', @now);
            INSERT INTO QMS_INSPECTION_SPEC
              (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, IS_ACTIVE,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@specA, @specA, @process, 'Dimension', 'Attribute', 1,
                    'TEST', @now, 'TEST', @now);
            INSERT INTO QMS_INSPECTION_SPEC
              (SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE, IS_ACTIVE,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@specB, @specB, @process, 'Appearance', 'Attribute', 1,
                    'TEST', @now, 'TEST', @now);
            """,
            ("@equipment", context.EquipmentId),
            ("@lot", context.LotId),
            ("@process", context.ProcessId),
            ("@specA", context.SpecA),
            ("@specB", context.SpecB),
            ("@now", now));

        return context;
    }

    /// <summary>Persists one immutable v2 execution header with one or more item results.</summary>
    private void InsertV2Inspection(
        QualityContext context,
        string inspectionId,
        DateTime inspectedAt,
        IReadOnlyList<(string SpecId, bool IsPass)> items,
        string relationType = "Original",
        string? parentInspectionId = null,
        string? rootInspectionId = null)
    {
        var root = rootInspectionId ?? inspectionId;
        var timestamp = Timestamp(inspectedAt);
        var allPass = items.All(item => item.IsPass);
        var idempotencyKey = $"QUALITY-GATE:{inspectionId}";
        var requestHash = new string('a', 64);

        Exec("""
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, SPEC_ID,
               INSPECTED_AT, INSPECTOR_ID, RESULT, LOT_QTY, SAMPLE_QTY, DEFECT_QTY,
               IS_CONFIRMED, IDEMPOTENCY_KEY, REQUEST_HASH, RELATION_TYPE,
               PARENT_INSPECTION_ID, ROOT_INSPECTION_ID,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspection, 'Process', @lot, @equipment, NULL,
                    @inspectedAt, 'admin', @result, 10, 1, @defectQty,
                    1, @key, @hash, @relationType, @parent, @root,
                    'admin', @inspectedAt, 'admin', @inspectedAt);
            """,
            ("@inspection", inspectionId),
            ("@lot", context.LotId),
            ("@equipment", context.EquipmentId),
            ("@inspectedAt", timestamp),
            ("@result", allPass ? "Pass" : "Fail"),
            ("@defectQty", allPass ? 0 : 1),
            ("@key", idempotencyKey),
            ("@hash", requestHash),
            ("@relationType", relationType),
            ("@parent", parentInspectionId),
            ("@root", root));

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            Exec("""
                INSERT INTO QMS_INSPECTION_RESULT
                  (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID,
                   ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID, IS_PASS,
                   ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY,
                   CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@resultId, @inspection, @spec, @lot, @equipment,
                        @attributeResult, @inspectedAt, 'admin', @isPass,
                        @sequence, 1, @defectQty,
                        'admin', @inspectedAt, 'admin', @inspectedAt);
                """,
                ("@resultId", Id("QMSR")),
                ("@inspection", inspectionId),
                ("@spec", item.SpecId),
                ("@lot", context.LotId),
                ("@equipment", context.EquipmentId),
                ("@attributeResult", item.IsPass ? "Pass" : "Fail"),
                ("@inspectedAt", timestamp),
                ("@isPass", item.IsPass ? 1 : 0),
                ("@sequence", index + 1),
                ("@defectQty", item.IsPass ? 0 : 1));
        }

        InsertEvent(
            inspectionId,
            "Confirmed",
            root,
            inspectedAt,
            idempotencyKey,
            parentInspectionId: parentInspectionId);
    }

    /// <summary>Persists the legacy one-header/one-result shape used before execution v2.</summary>
    private void InsertLegacyInspection(
        QualityContext context,
        string inspectionId,
        string specId,
        bool isPass,
        string inspectionType,
        DateTime inspectedAt)
    {
        var timestamp = Timestamp(inspectedAt);
        Exec("""
            INSERT INTO QMS_INSPECTION
              (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, EQUIPMENT_ID, SPEC_ID,
               INSPECTED_AT, INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@inspection, @inspectionType, @lot, @equipment, @spec,
                    @inspectedAt, 'admin', @result, 1, @defectQty, 1,
                    'TEST', @inspectedAt, 'TEST', @inspectedAt);
            INSERT INTO QMS_INSPECTION_RESULT
              (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID,
               ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID, IS_PASS,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@resultId, @inspection, @spec, @lot, @equipment,
                    @result, @inspectedAt, 'admin', @isPass,
                    'TEST', @inspectedAt, 'TEST', @inspectedAt);
            """,
            ("@inspection", inspectionId),
            ("@inspectionType", inspectionType),
            ("@lot", context.LotId),
            ("@equipment", context.EquipmentId),
            ("@spec", specId),
            ("@inspectedAt", timestamp),
            ("@result", isPass ? "Pass" : "Fail"),
            ("@defectQty", isPass ? 0 : 1),
            ("@resultId", Id("QMSR")),
            ("@isPass", isPass ? 1 : 0));
    }

    /// <summary>Appends a cancellation event; the immutable header/result rows remain unchanged.</summary>
    private void InsertCancellationEvent(string inspectionId, string rootInspectionId, DateTime occurredAt) =>
        InsertEvent(
            inspectionId,
            "Cancelled",
            rootInspectionId,
            occurredAt,
            $"QUALITY-GATE:CANCEL:{inspectionId}");

    /// <summary>Marks a parent execution as superseded by a correction or reinspection child.</summary>
    private void InsertSuccessorEvent(
        string parentInspectionId,
        string successorInspectionId,
        string rootInspectionId,
        string eventType,
        DateTime occurredAt) =>
        InsertEvent(
            parentInspectionId,
            eventType,
            rootInspectionId,
            occurredAt,
            $"QUALITY-GATE:{eventType}:{successorInspectionId}",
            parentInspectionId,
            successorInspectionId);

    /// <summary>Appends one valid v2 audit event using the same shape as the QMS repository.</summary>
    private void InsertEvent(
        string inspectionId,
        string eventType,
        string rootInspectionId,
        DateTime occurredAt,
        string idempotencyKey,
        string? parentInspectionId = null,
        string? relatedInspectionId = null)
    {
        Exec("""
            INSERT INTO QMS_INSPECTION_EVENT
              (EVENT_ID, INSPECTION_ID, EVENT_TYPE, RELATED_INSPECTION_ID,
               PARENT_INSPECTION_ID, ROOT_INSPECTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH,
               ACTOR_ID, REASON, OCCURRED_AT, CREATED_BY, CREATED_AT)
            VALUES (@eventId, @inspection, @eventType, @related, @parent, @root,
                    @key, @hash, 'admin', 'quality-gate integration evidence',
                    @occurredAt, 'admin', @occurredAt);
            """,
            ("@eventId", Id("QMSE")),
            ("@inspection", inspectionId),
            ("@eventType", eventType),
            ("@related", relatedInspectionId),
            ("@parent", parentInspectionId),
            ("@root", rootInspectionId),
            ("@key", idempotencyKey),
            ("@hash", new string('b', 64)),
            ("@occurredAt", Timestamp(occurredAt)));
    }

    /// <summary>Executes parameterized SQL directly against the factory's real SQLite database.</summary>
    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string Timestamp(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private sealed record QualityContext(
        string LotId,
        string ProcessId,
        string EquipmentId,
        string SpecA,
        string SpecB);
}
