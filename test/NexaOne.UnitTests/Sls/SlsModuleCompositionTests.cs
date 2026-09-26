using NexaOne.SLS.Infrastructure;
using NexaOne.ServiceContracts.Sls;
using NexaOne.UnitTests.TestInfrastructure;

namespace NexaOne.UnitTests.Sls;

/// <summary>SLS 모듈 조립: 공개 계약 제품만 노출하고 Infrastructure 세부는 숨긴다.</summary>
public sealed class SlsModuleCompositionTests
{
    [Fact]
    public void Module_composes_one_mrp_demand_directory_from_the_data_source()
    {
        var module = new NexaOne.SLS.Module(DataSource());

        var directory = module.GetMrpDemandDirectory();

        directory.Should().BeOfType<MrpDemandDirectory>();
        module.GetMrpDemandDirectory().Should().BeSameAs(directory,
            "the module exposes one shared contract instance per composition root");
    }

    [Fact]
    public void Module_rejects_a_missing_data_source()
    {
        var act = () => new NexaOne.SLS.Module(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Mrp_demand_directory_implements_the_shared_contract()
    {
        typeof(MrpDemandDirectory).Should().Implement<IMrpDemandDirectory>();
    }

    private static NexaOne.Infrastructure.Persistence.EesDataSource DataSource() => new()
    {
        Provider = new SqliteTestDatabaseProvider(),
        ConnectionString = "Data Source=:memory:",
    };
}
