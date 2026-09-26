using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sls;
using NexaOne.SLS.Infrastructure;

namespace NexaOne.SLS;

/// <summary>SLS의 단일 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IMrpDemandDirectory _mrpDemandDirectory;

    public Module(EesDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _mrpDemandDirectory = new MrpDemandDirectory(dataSource);
    }

    public IMrpDemandDirectory GetMrpDemandDirectory() => _mrpDemandDirectory;
}
