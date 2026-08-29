using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class IvtMaterialConsumptionPersistenceTests :
    IClassFixture<IvtMaterialConsumptionPersistenceTests.MaterialFactory>
{
    private readonly MaterialFactory _factory;

    public IvtMaterialConsumptionPersistenceTests(MaterialFactory factory) => _factory = factory;

    public sealed class MaterialFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-ivt-consumption-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False;Default Timeout=10";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "ivt-consumption-test-secret-key-at-least-32-bytes");
            builder.UseSetting("Jwt:Issuer", "ivt-consumption-test");
            builder.UseSetting("Jwt:Audience", "ivt-consumption-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Reversal_appends_evidence_and_projects_original_status_without_mutating_it()
    {
        _ = _factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = $"LOT-{suffix}";
        var consumptionId = $"CON-{suffix}";
        var reversalId = $"REV-{suffix}";
        SeedLot(lotId, 10m);
        var service = new ConsumptionService(Repository());
        var at = new DateTime(2026, 8, 28, 1, 0, 0, DateTimeKind.Utc);

        var consumed = await service.ConsumeAsync(new MaterialConsumptionCommand(
            consumptionId, $"consume:{suffix}", "PLANT-01", "EQ-01", lotId,
            "MAT-01", 2m, "EA", "Manual", at, "TEST", $"source:{suffix}",
            OperatorId: "operator-1"));
        var reversed = await service.ReverseAsync(new MaterialConsumptionReversalCommand(
            reversalId, $"reverse:{suffix}", consumptionId, "Incorrect issue",
            at.AddMinutes(1), "TEST", "operator-2"));

        consumed.IsSuccess.Should().BeTrue(consumed.IsFailure ? consumed.Error.Description : string.Empty);
        reversed.IsSuccess.Should().BeTrue(reversed.IsFailure ? reversed.Error.Description : string.Empty);
        Scalar<string>(
                "SELECT STATUS FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE CONSUMPTION_ID=@id",
                ("@id", consumptionId))
            .Should().Be("Committed", "the original accounting evidence is immutable");
        Scalar<long>(
                "SELECT COUNT(*) FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE REVERSAL_OF_ID=@id",
                ("@id", consumptionId))
            .Should().Be(1);
        Scalar<decimal>("SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id", ("@id", lotId))
            .Should().Be(10m);

        var projected = await Repository().GetByIdAsync(consumptionId);
        projected.Should().NotBeNull();
        projected!.Status.Should().Be("Reversed");
    }

    [Fact]
    public void Consumption_evidence_rejects_update_delete_and_replace_with_recursive_triggers_disabled()
    {
        _ = _factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = $"LOT-G-{suffix}";
        var id = $"CON-G-{suffix}";
        SeedLot(lotId, 10m);
        Execute($"""
            INSERT INTO IVT_MATERIAL_CONSUMPTION_HISTORY
                (CONSUMPTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                 MATERIAL_LOT_ID, MATERIAL_ID, CONSUMPTION_MODE, QUANTITY, UNIT,
                 SOURCE_EVENT_ID, SOURCE_SYSTEM, OPERATOR_ID, STATUS, OCCURRED_AT,
                 CREATED_BY, CREATED_AT)
            VALUES
                ('{id}', 'guard:{suffix}', 'hash', 'PLANT-01', 'EQ-01',
                 '{lotId}', 'MAT-01', 'Manual', 1, 'EA', 'guard-source:{suffix}',
                 'TEST', 'operator', 'Committed', CURRENT_TIMESTAMP, 'operator', CURRENT_TIMESTAMP);
            PRAGMA recursive_triggers=OFF;
            """);

        Action update = () => Execute(
            "UPDATE IVT_MATERIAL_CONSUMPTION_HISTORY SET STATUS='Reversed' WHERE CONSUMPTION_ID=@id",
            ("@id", id));
        Action delete = () => Execute(
            "DELETE FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE CONSUMPTION_ID=@id",
            ("@id", id));
        Action replace = () => Execute($"""
            INSERT OR REPLACE INTO IVT_MATERIAL_CONSUMPTION_HISTORY
                (CONSUMPTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                 MATERIAL_LOT_ID, MATERIAL_ID, CONSUMPTION_MODE, QUANTITY, UNIT,
                 SOURCE_EVENT_ID, SOURCE_SYSTEM, OPERATOR_ID, STATUS, OCCURRED_AT,
                 CREATED_BY, CREATED_AT)
            VALUES
                ('{id}', 'guard:{suffix}', 'changed', 'PLANT-01', 'EQ-01',
                 '{lotId}', 'MAT-01', 'Manual', 1, 'EA', 'guard-source:{suffix}',
                 'TEST', 'operator', 'Committed', CURRENT_TIMESTAMP, 'operator', CURRENT_TIMESTAMP);
            """);

        update.Should().Throw<SqliteException>().WithMessage("*append-only*");
        delete.Should().Throw<SqliteException>().WithMessage("*append-only*");
        replace.Should().Throw<SqliteException>().WithMessage("*replacement*");
    }

    private ConsumptionRepository Repository() => new(DataSource());

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnectionString,
    };

    private void SeedLot(string lotId, decimal quantity) => Execute(@"
        INSERT INTO IVT_MATERIAL_LOT
            (LOT_ID, MATERIAL_ID, LOT_NO, WAREHOUSE, CURRENT_QTY, UNIT, STATUS,
             RECEIVED_AT, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
            (@id, 'MAT-01', @id, 'WH-01', @qty, 'EA', 'InStock', CURRENT_TIMESTAMP,
             1, 'seed', CURRENT_TIMESTAMP, 'seed', CURRENT_TIMESTAMP)",
        ("@id", lotId), ("@qty", quantity));

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}
