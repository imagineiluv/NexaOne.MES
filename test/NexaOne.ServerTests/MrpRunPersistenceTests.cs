using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Mrp;
using NexaOne.POM.Domain.Mrp;
using NexaOne.ServiceContracts.Prc;
using MdmEquipmentDirectory = NexaOne.MDM.Infrastructure.EquipmentDirectory;
using MdmMrpMasterDirectory = NexaOne.MDM.Infrastructure.MrpMasterDirectory;
using IvtMrpInventoryDirectory = NexaOne.IVT.Infrastructure.MrpInventoryDirectory;
using PomLegacySalesOrderMrpProjection = NexaOne.POM.Infrastructure.LegacySalesOrderMrpProjection;
using PomMrpPlanningRepository = NexaOne.POM.Infrastructure.MrpPlanningRepository;
using PrcModule = NexaOne.PRC.Module;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>MRP run publication boundary: proposals, pegging and Success are one transaction.
/// Failed/legacy partial runs must not become the default source for conversion, MRP reads or CRP.</summary>
public sealed class MrpRunPersistenceTests : IClassFixture<MrpRunPersistenceTests.MrpFactory>
{
    private const string Secret = "mrp-persistence-e2e-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-mrp-persistence-test";
    private readonly MrpFactory _factory;

    public MrpRunPersistenceTests(MrpFactory factory) => _factory = factory;

    public sealed class MrpFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-mrp-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Run_publication_is_atomic_and_consumers_ignore_failed_runs()
    {
        _ = _factory.CreateClient(); // schema + deterministic development seed
        var repository = Repository();
        var trigger = $"TR_MRP_PEG_FAIL_{Guid.NewGuid():N}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON MRP_PEGGING " +
                "BEGIN SELECT RAISE(ABORT, 'forced pegging failure'); END");

        try
        {
            var failed = await repository.RunAsync("mrp-test");

            failed.Status.Should().Be("Failed");
            Scalar<string>("SELECT STATUS FROM MRP_RUN WHERE RUN_ID=@id", ("@id", failed.RunId))
                .Should().Be("Failed", "failure finalization must commit independently of the failed batch");
            Scalar<long>("SELECT COUNT(*) FROM MRP_PLANNED_ORDER WHERE RUN_ID=@id", ("@id", failed.RunId))
                .Should().Be(0, "proposal inserts must roll back when pegging fails");
            Scalar<long>("SELECT COUNT(*) FROM MRP_PEGGING WHERE RUN_ID=@id", ("@id", failed.RunId))
                .Should().Be(0, "pegging must not be partially committed");
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}");
        }

        var success = await repository.RunAsync("mrp-test");
        success.Status.Should().Be("Success", success.Message);
        success.PlannedOrderCount.Should().BeGreaterThan(0, "the seeded demand must produce proposals");
        Scalar<long>("SELECT COUNT(*) FROM MRP_PLANNED_ORDER WHERE RUN_ID=@id", ("@id", success.RunId))
            .Should().Be(success.PlannedOrderCount);
        Scalar<long>("SELECT COUNT(*) FROM MRP_PEGGING WHERE RUN_ID=@id", ("@id", success.RunId))
            .Should().BeGreaterThan(0);

        // Simulate a legacy partial run newer than the successful run. New code cannot create this
        // shape, but readers must still be safe while old data exists.
        var legacyFailedRun = $"MRP_FAILED_{Guid.NewGuid():N}"[..40];
        var legacyOrder = $"MPO_FAIL_{Guid.NewGuid():N}"[..40];
        Execute("INSERT INTO MRP_RUN (RUN_ID, STARTED_AT, FINISHED_AT, STATUS, EXECUTED_BY, CREATED_BY, UPDATED_BY) " +
                "VALUES (@run, '2100-01-01 00:00:00', '2100-01-01 00:00:01', 'Failed', 'TEST', 'TEST', 'TEST')",
            ("@run", legacyFailedRun));
        Execute("INSERT INTO MRP_PLANNED_ORDER " +
                "(PLANNED_ORDER_ID, RUN_ID, ITEM_ID, ORDER_TYPE, GROSS_QTY, NET_QTY, SUGGESTED_QTY, STATUS, PLANT_ID, CREATED_BY, UPDATED_BY) " +
                "VALUES (@id, @run, 'ITEM01', 'Production', 999999, 999999, 999999, 'Proposed', 'PLANT01', 'TEST', 'TEST')",
            ("@id", legacyOrder), ("@run", legacyFailedRun));
        Execute("INSERT INTO MRP_PEGGING (PEGGING_ID, RUN_ID, PLANNED_ORDER_ID, ITEM_ID, DEMAND_REF, QTY, CREATED_BY) " +
                "VALUES (@id, @run, @order, 'ITEM01', 'LEGACY', 999999, 'TEST')",
            ("@id", $"PEG_{Guid.NewGuid():N}"), ("@run", legacyFailedRun), ("@order", legacyOrder));

        var defaultConversion = await repository.ConvertAsync(null, null, null, "mrp-test");
        defaultConversion.RunId.Should().Be(success.RunId,
            "default conversion must select the latest successful run, not a newer failed run");

        var defaultOrders = await Query("POM.MrpPlannedOrderList", new());
        defaultOrders.Should().NotBeEmpty().And.OnlyContain(r => r["RUN_ID"].ToString() == success.RunId);
        (await Query("POM.MrpPlannedOrderList", new() { ["runId"] = legacyFailedRun })).Should().BeEmpty();

        var defaultPegging = await Query("POM.MrpPeggingList", new());
        defaultPegging.Should().NotBeEmpty();
        defaultPegging.Should().OnlyContain(r => r["PLANNED_ORDER_ID"].ToString() != legacyOrder);
        (await Query("POM.MrpPeggingList", new() { ["runId"] = legacyFailedRun })).Should().BeEmpty();

        var failedCrp = await Query("POM.CrpWorkCenterLoad", new() { ["runId"] = legacyFailedRun });
        failedCrp.Should().NotBeEmpty();
        failedCrp.Should().OnlyContain(r => decimal.Parse(r["LOAD_MIN"].ToString()!, CultureInfo.InvariantCulture) == 0m,
            "CRP must not derive capacity load from a failed run");
    }

    [Fact]
    public async Task Purchase_conversion_recovers_after_prc_commit_and_pom_mark_failure_without_duplicate_order()
    {
        _ = _factory.CreateClient();
        var repository = Repository();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var runId = $"MRP_RECOVERY_{suffix}";
        var plannedId = $"MRP_PUR_{suffix}";
        var trigger = $"TR_MRP_MARK_FAIL_{suffix}";
        var description = $"MRP {runId} / {plannedId}";

        Execute(
            "INSERT INTO MRP_RUN (RUN_ID, STARTED_AT, FINISHED_AT, STATUS, EXECUTED_BY, CREATED_BY, UPDATED_BY) " +
            "VALUES (@run, '2030-01-01 00:00:00', '2030-01-01 00:00:01', 'Success', 'TEST', 'TEST', 'TEST')",
            ("@run", runId));
        Execute(
            "INSERT INTO MRP_PLANNED_ORDER " +
            "(PLANNED_ORDER_ID, RUN_ID, ITEM_ID, ORDER_TYPE, GROSS_QTY, NET_QTY, SUGGESTED_QTY, " +
            "DUE_DATE, RELEASE_DATE, STATUS, PLANT_ID, CREATED_BY, UPDATED_BY) " +
            "VALUES (@id, @run, 'MAT01', 'Purchase', 12, 12, 12, '2030-02-01 00:00:00', " +
            "'2030-01-15 00:00:00', 'Proposed', 'PLANT01', 'TEST', 'TEST')",
            ("@id", plannedId), ("@run", runId));
        Execute(
            $"CREATE TRIGGER {trigger} BEFORE UPDATE OF STATUS ON MRP_PLANNED_ORDER " +
            "WHEN NEW.STATUS = 'Converted' BEGIN SELECT RAISE(ABORT, 'forced pom mark failure'); END");

        try
        {
            var first = await repository.ConvertAsync(runId, new[] { plannedId }, null, "mrp-test");

            first.Converted.Should().Be(0);
            first.Message.Should().Contain("forced pom mark failure");
            Scalar<long>("SELECT COUNT(*) FROM PRC_PURCHASE_ORDER WHERE DESCRIPTION=@description",
                    ("@description", description))
                .Should().Be(1, "the PRC owner command commits independently before POM marks its proposal");
            Scalar<string>("SELECT STATUS FROM MRP_PLANNED_ORDER WHERE PLANNED_ORDER_ID=@id", ("@id", plannedId))
                .Should().Be("Proposed");
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}");
        }

        var retry = await repository.ConvertAsync(runId, new[] { plannedId }, null, "mrp-test");

        retry.Message.Should().BeNull();
        retry.Converted.Should().Be(1);
        retry.PurchaseOrders.Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM PRC_PURCHASE_ORDER WHERE DESCRIPTION=@description",
                ("@description", description))
            .Should().Be(1, "stable PRC command ids make retries idempotent");
        Scalar<string>("SELECT STATUS FROM MRP_PLANNED_ORDER WHERE PLANNED_ORDER_ID=@id", ("@id", plannedId))
            .Should().Be("Converted");
    }

    [Fact]
    public async Task Run_caller_cancellation_is_rethrown_and_run_is_finalized_best_effort()
    {
        _ = _factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var actor = $"cancel-run-{Guid.NewGuid():N}";
        var dataSource = DataSource();
        var repository = new PomMrpPlanningRepository(
            dataSource,
            new CancelingDemandSource(cancellation),
            new MdmMrpMasterDirectory(dataSource),
            new IvtMrpInventoryDirectory(dataSource),
            new PrcModule(dataSource).GetPurchaseOrderPlanningBridge(),
            new MdmEquipmentDirectory(dataSource));

        Func<Task> act = () => repository.RunAsync(actor, ct: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Scalar<string>(
                "SELECT STATUS FROM MRP_RUN WHERE EXECUTED_BY=@actor ORDER BY STARTED_AT DESC LIMIT 1",
                ("@actor", actor))
            .Should().Be("Failed", "a started run must not remain Running after caller cancellation");
    }

    [Fact]
    public async Task Run_finalization_failure_does_not_mask_caller_cancellation()
    {
        _ = _factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var actor = $"cancel-cleanup-{Guid.NewGuid():N}";
        var trigger = $"TR_MRP_CANCEL_CLEANUP_{Guid.NewGuid():N}";
        Execute($@"CREATE TRIGGER {trigger} BEFORE UPDATE OF STATUS ON MRP_RUN
                   WHEN NEW.STATUS = 'Failed' AND NEW.EXECUTED_BY = '{actor}'
                   BEGIN SELECT RAISE(ABORT, 'forced MRP cleanup failure'); END");
        var dataSource = DataSource();
        var repository = new PomMrpPlanningRepository(
            dataSource,
            new CancelingDemandSource(cancellation),
            new MdmMrpMasterDirectory(dataSource),
            new IvtMrpInventoryDirectory(dataSource),
            new PrcModule(dataSource).GetPurchaseOrderPlanningBridge(),
            new MdmEquipmentDirectory(dataSource));

        try
        {
            Func<Task> act = () => repository.RunAsync(actor, ct: cancellation.Token);
            await act.Should().ThrowAsync<OperationCanceledException>(
                "a cleanup database failure must not replace caller cancellation");
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}");
        }
    }

    [Fact]
    public async Task Convert_caller_cancellation_is_not_translated_to_a_business_result()
    {
        _ = _factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var runId = $"MRP_CANCEL_{suffix}";
        var plannedId = $"MRP_PUR_{suffix}";
        Execute(
            "INSERT INTO MRP_RUN (RUN_ID, STARTED_AT, FINISHED_AT, STATUS, EXECUTED_BY, CREATED_BY, UPDATED_BY) " +
            "VALUES (@run, '2030-01-01 00:00:00', '2030-01-01 00:00:01', 'Success', 'TEST', 'TEST', 'TEST')",
            ("@run", runId));
        Execute(
            "INSERT INTO MRP_PLANNED_ORDER " +
            "(PLANNED_ORDER_ID, RUN_ID, ITEM_ID, ORDER_TYPE, GROSS_QTY, NET_QTY, SUGGESTED_QTY, " +
            "DUE_DATE, RELEASE_DATE, STATUS, PLANT_ID, CREATED_BY, UPDATED_BY) " +
            "VALUES (@id, @run, 'MAT01', 'Purchase', 12, 12, 12, '2030-02-01 00:00:00', " +
            "'2030-01-15 00:00:00', 'Proposed', 'PLANT01', 'TEST', 'TEST')",
            ("@id", plannedId), ("@run", runId));
        var dataSource = DataSource();
        var repository = new PomMrpPlanningRepository(
            dataSource,
            new PomLegacySalesOrderMrpProjection(dataSource),
            new MdmMrpMasterDirectory(dataSource),
            new IvtMrpInventoryDirectory(dataSource),
            new CancelingPurchaseOrderBridge(cancellation),
            new MdmEquipmentDirectory(dataSource));

        Func<Task> act = () => repository.ConvertAsync(
            runId, new[] { plannedId }, null, "cancel-convert", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Scalar<string>("SELECT STATUS FROM MRP_PLANNED_ORDER WHERE PLANNED_ORDER_ID=@id", ("@id", plannedId))
            .Should().Be("Proposed");
    }

    private sealed class CancelingDemandSource(CancellationTokenSource cancellation) : IMrpDemandSource
    {
        public Task<IReadOnlyList<MrpDemand>> GetOpenDemandsAsync(CancellationToken ct = default)
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancellation token should have interrupted demand loading.");
        }
    }

    private sealed class CancelingPurchaseOrderBridge(CancellationTokenSource cancellation)
        : IPurchaseOrderPlanningBridge
    {
        public Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MrpPurchaseReceipt>>([]);

        public Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
            MrpPurchaseOrderRequest request,
            CancellationToken ct = default)
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancellation token should have interrupted conversion.");
        }
    }

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnString,
    };

    private PomMrpPlanningRepository Repository()
    {
        var dataSource = DataSource();
        return new PomMrpPlanningRepository(
            dataSource,
            new PomLegacySalesOrderMrpProjection(dataSource),
            new MdmMrpMasterDirectory(dataSource),
            new IvtMrpInventoryDirectory(dataSource),
            new PrcModule(dataSource).GetPurchaseOrderPlanningBridge(),
            new MdmEquipmentDirectory(dataSource));
    }

    private void Execute(string sql, params (string Key, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Key, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> parameters)
    {
        var client = AuthenticatedClient();
        var response = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", parameters);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} must execute successfully");
        return (await response.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>())!;
    }

    private HttpClient AuthenticatedClient()
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Issuer,
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "mrp-test"),
                new Claim(NexaOne.Common.Security.Permissions.ClaimType, "pom:read"),
            },
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }
}
