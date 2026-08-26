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
using NexaOne.POM.Infrastructure;
using NexusCom.Data.Abstractions.Interfaces;
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
        var repository = new MrpPlanningRepository(DataSource());
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

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnString,
    };

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
