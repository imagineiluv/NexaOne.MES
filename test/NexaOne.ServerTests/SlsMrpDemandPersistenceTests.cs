using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SLS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>SLS-owned MRP demand directory: only Confirmed/Producing open quantities reach MRP.</summary>
public sealed class SlsMrpDemandPersistenceTests
    : IClassFixture<SlsMrpDemandPersistenceTests.SlsFactory>
{
    private const string Secret = "sls-demand-persistence-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-sls-demand-persistence-test";
    private readonly SlsFactory _factory;

    public SlsMrpDemandPersistenceTests(SlsFactory factory) => _factory = factory;

    public sealed class SlsFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-sls-{Guid.NewGuid():N}.db");
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
    public async Task Demand_directory_returns_only_confirmed_open_quantities()
    {
        _ = _factory.CreateClient(); // schema + deterministic development seed (SO01 Confirmed, SO02 Draft)

        var demands = await Directory().GetOpenDemandsAsync();

        var so01 = demands.Should().ContainSingle(
            demand => demand.SourceRef == "SO01" && demand.ItemId == "ITEM01").Subject;
        so01.Qty.Should().Be(1000m, "SO01 ships nothing yet so the full plan quantity stays open");
        so01.PlantId.Should().Be("PLANT01");
        demands.Should().NotContain(demand => demand.SourceRef == "SO02",
            "Draft orders are not MRP demand");
    }

    [Fact]
    public async Task Delivered_closed_and_itemless_orders_never_reach_mrp()
    {
        _ = _factory.CreateClient();
        Execute(
            "INSERT INTO SLS_SALES_ORDER (SALES_ORDER_ID,PLANT_ID,PRODUCT_ID,PLAN_QTY,DELIVERED_QTY,STATUS) " +
            "VALUES ('SO-FULL','PLANT01','ITEM01',5,5,'Confirmed')," +
            "('SO-CLOSED','PLANT01','ITEM01',9,0,'Closed')," +
            "('SO-NOITEM','PLANT01',NULL,4,0,'Confirmed')");

        var demands = await Directory().GetOpenDemandsAsync();

        demands.Should().NotContain(demand => demand.SourceRef == "SO-FULL",
            "a fully delivered order has no open quantity");
        demands.Should().NotContain(demand => demand.SourceRef == "SO-CLOSED");
        demands.Should().NotContain(demand => demand.SourceRef == "SO-NOITEM",
            "an order without a product cannot become item demand");
    }

    private MrpDemandDirectory Directory() => new(DataSource());

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnString,
    };

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
