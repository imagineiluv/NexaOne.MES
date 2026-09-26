namespace NexaOne.UnitTests.Architecture;

/// <summary>SLS boundary: the demand directory is read-only persistence and owns SLS tables only.</summary>
public sealed class SlsApplicationBoundaryTests
{
    [Fact]
    public void Mrp_demand_directory_reads_only_sales_orders_and_never_writes()
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.SLS", "Infrastructure", "MrpDemandDirectory.cs"));

        source.Should().Contain("SLS_SALES_ORDER");
        source.Should().Contain("'Confirmed'");
        source.Should().Contain("'Producing'");
        source.Should().NotContain("SLS_SALES_REQUEST",
            "MRP demand reads the confirmed order, not the request document");
        source.Should().NotContain("INSERT INTO");
        source.Should().NotContain("UPDATE ");
        source.Should().NotContain("DELETE FROM");
        source.Should().NotContain("CancellationToken.None");
    }

    [Fact]
    public void Sls_module_exports_only_contract_products()
    {
        var module = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.SLS", "Module.cs"));

        module.Should().Contain("GetMrpDemandDirectory");
        module.Should().NotContain("SELECT ");
        module.Should().NotContain("QueryRepository");
    }

    [Fact]
    public void Pom_no_longer_references_sls_storage_directly()
    {
        var pomRoot = Path.Combine(
            RepositorySource.Root, "src", "04.Modules", "NexaOne.POM");

        foreach (var file in Directory.GetFiles(pomRoot, "*.cs", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).Should().NotContain("SLS_SALES_",
                "POM must consume SLS demand through the SLS-owned contract, never SLS tables");
            File.ReadAllText(file).Should().NotContain("LegacySalesOrderMrpProjection",
                "the temporary projection was superseded by NexaOne.SLS");
        }
    }
}
